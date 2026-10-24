// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using CombatSimulator.ActionCombat;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatSimulator.Voice;

/// <summary>
/// Gives enemies battle voices: watches combat for voice-worthy moments, picks each speaker's
/// voice, and rate-limits the result so a large fight stays readable.
///
/// Everything is local playback through the client's own sound player; nothing is sent anywhere.
/// With <see cref="Configuration.EnableEnemyVoices"/> off the per-frame tick returns after one
/// flag check and the native player is never touched.
/// </summary>
public sealed unsafe class VoiceDirector
{
    private const string BuiltInLabelsResource = "CombatSimulator.Resources.VoiceEntryLabels.json";
    private const float VictoryDelay = 1.5f;
    // A committed attack within this long after its own cast started is that cast resolving,
    // which was already voiced at cast start.
    private const float CastResolveGrace = 1.0f;

    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly CombatEngine combatEngine;
    private readonly Func<bool> hasLivingCompanions;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly VoiceRateSettings rateSettings = new();
    private readonly VoiceRateLimiter limiter;
    private readonly Dictionary<uint, Tracked> tracked = new();
    private readonly List<VoiceCandidate> candidates = new();
    private readonly List<VoiceEvent> scratchEvents = new();
    private readonly List<(uint EntityId, VoiceEvent Event)> notified = new();
    private readonly List<(uint EntityId, VoiceEvent Event, float At)> delayed = new();
    private readonly HashSet<uint> seenThisFrame = new();
    private readonly List<(ulong, float)> speakers = new();
    private readonly Dictionary<string, int> entryCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random random = new();
    private (VoiceSource Source, uint Entry, bool Positional)? auditionRequest;
    private (nint Handle, string Path, uint Entry) lastAudition;
    private bool hadState;

    private sealed class Tracked
    {
        public readonly VoiceActorWatch Watch = new();
        public nint Address;
        public float LastCastStart = float.NegativeInfinity;
        public float LastCastTotal;
        public bool Alive;
        public bool Engaged;
        public float Distance;
        public Vector3 Position;
    }

    public VoiceDirector(Configuration config, NpcSelector npcSelector, CombatEngine combatEngine,
        Func<bool> hasLivingCompanions, IDataManager dataManager, IPluginLog log)
    {
        this.config = config;
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.hasLivingCompanions = hasLivingCompanions;
        this.dataManager = dataManager;
        this.log = log;
        limiter = new VoiceRateLimiter(rateSettings);
        Player = new NativeSoundPlayer(dataManager, log);
        Resolver = new VoiceProfileResolver(dataManager, config);
        ReloadLabels();
    }

    public NativeSoundPlayer Player { get; }
    public VoiceProfileResolver Resolver { get; }
    public VoiceLabelTable Labels { get; } = new();
    public string LastVoiceDescription { get; private set; } = string.Empty;

    /// <summary>
    /// Raised after a voice actually starts: speaker address, event, source and entry. Seam for
    /// phase 2 speech bubbles (a text source per enemy does not exist yet).
    /// </summary>
    public Action<nint, VoiceEvent, VoiceSource, int>? VoicePlayed { get; set; }

    /// <summary>Built-in labels first, then the owner's recorded ones on top.</summary>
    public void ReloadLabels()
    {
        Labels.Clear();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BuiltInLabelsResource);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var builtIn = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, List<int>>>>(reader.ReadToEnd());
                Labels.Merge(builtIn);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Enemy voices: built-in entry labels unreadable ({ex.Message}); using recorded labels only.");
        }
        Labels.Merge(config.EnemyVoiceEntryLabels);
    }

    // ---- Event inputs (cheap no-ops while the feature is off) ----

    /// <summary>An enemy committed an attack (CombatModeRouter). Auto-attacks are not voiced.</summary>
    public void NotifyAttackCommitted(SimulatedNpc npc, in NpcAttackRequest request)
    {
        if (!config.EnableEnemyVoices || request.IsAutoAttack)
            return;
        if (tracked.TryGetValue(npc.SimulatedEntityId, out var t) &&
            limiter.Now - t.LastCastStart <= t.LastCastTotal + CastResolveGrace)
            return;
        notified.Add((npc.SimulatedEntityId, VoiceEvent.Skill));
    }

    /// <summary>The player died. The killer gloats; if the fight is lost, another enemy calls it.</summary>
    public void NotifyPlayerDeath()
    {
        if (!config.EnableEnemyVoices)
            return;
        var killer = combatEngine.LastPlayerKillerEntityId;
        if (killer != 0)
            notified.Add((killer, VoiceEvent.KilledPlayer));
        if (!hasLivingCompanions())
            delayed.Add((0, VoiceEvent.Victory, limiter.Now + VictoryDelay));
    }

    public void Reset()
    {
        tracked.Clear();
        notified.Clear();
        delayed.Clear();
        candidates.Clear();
        limiter.Reset();
        Resolver.Clear();
        hadState = false;
    }

    // ---- Audition (panel) ----

    /// <summary>Queue one play for the next framework tick, so the native call happens on the
    /// framework thread like every other voice.</summary>
    public void RequestAudition(VoiceSource source, uint entry, bool positional) =>
        auditionRequest = (source, entry, positional);

    public void StopAudition()
    {
        var (handle, path, entry) = lastAudition;
        Player.TryStop(handle, path, entry);
        lastAudition = default;
    }

    public string? LastAuditionPath => lastAudition.Path;

    /// <summary>Sound-entry count from the .scd header (0 when unreadable). Same header offset as
    /// the offline sweep: u16 sound-entry count at 0x30.</summary>
    public int ReadEntryCount(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return 0;
        // Cached: the panel asks every frame and a monster pack is a few hundred KB.
        if (entryCounts.TryGetValue(path, out var cached))
            return cached;
        var count = 0;
        try
        {
            var data = dataManager.GetFile(path)?.Data;
            count = data != null && data.Length >= 0x32 ? BitConverter.ToUInt16(data, 0x30) : 0;
        }
        catch (Exception)
        {
            count = 0;
        }
        entryCounts[path] = count;
        return count;
    }

    public string? ResolvePath(VoiceSource source) =>
        VoiceSelection.ResolvePath(source, Resolver.ClientLanguage, dataManager.FileExists);

    // ---- Frame ----

    public void Tick(float deltaTime)
    {
        if (!config.EnableEnemyVoices)
        {
            auditionRequest = null;
            if (hadState)
                Reset();
            return;
        }
        hadState = true;

        rateSettings.TokensPerSecond = Math.Clamp(config.EnemyVoicesPerSecond, 0.1f, 10f);
        rateSettings.MaxConcurrent = Math.Clamp(config.EnemyVoiceMaxConcurrent, 1, 8);
        rateSettings.ClosestSpeakers = Math.Clamp(config.EnemyVoiceClosestSpeakers, 1, 16);
        limiter.Advance(deltaTime);

        RunAudition();

        if (!combatEngine.IsActive)
        {
            if (tracked.Count > 0 || delayed.Count > 0 || notified.Count > 0)
                Reset();
            return;
        }

        var playerPos = Core.Services.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        candidates.Clear();
        seenThisFrame.Clear();
        speakers.Clear();

        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (npc.BattleChara == null)
                continue;
            var id = npc.SimulatedEntityId;
            seenThisFrame.Add(id);
            if (!tracked.TryGetValue(id, out var t) || t.Address != npc.Address)
            {
                // New entity, or its native slot was reused: start clean.
                if (t != null)
                {
                    limiter.Forget(id);
                    Resolver.Forget(t.Address, id);
                }
                tracked[id] = t = new Tracked { Address = npc.Address };
            }

            var go = (GameObject*)npc.BattleChara;
            t.Position = go->Position;
            t.Distance = Vector3.Distance(t.Position, playerPos);
            t.Alive = npc.IsAlive;
            t.Engaged = npc.IsEngaged;
            speakers.Add((id, t.Distance));

            scratchEvents.Clear();
            t.Watch.Observe(new VoiceActorSnapshot(npc.IsAlive, npc.IsEngaged, npc.State.IsCasting,
                npc.State.CurrentHp, npc.State.MaxHp), scratchEvents);
            foreach (var e in scratchEvents)
            {
                if (e == VoiceEvent.Skill)
                {
                    t.LastCastStart = limiter.Now;
                    t.LastCastTotal = npc.State.CastTimeTotal;
                }
                candidates.Add(new VoiceCandidate(id, e, t.Distance));
                if (e == VoiceEvent.Death)
                    AddAllyDeath(id, t.Position);
            }

            if (npc.IsAlive && npc.IsEngaged && config.EnemyVoiceIdleTaunts &&
                random.NextDouble() < deltaTime / Math.Max(5f, config.EnemyVoiceTauntInterval))
                candidates.Add(new VoiceCandidate(id, VoiceEvent.Taunt, t.Distance));
        }

        // Drop actors that left the selection (despawned/deselected).
        if (tracked.Count > seenThisFrame.Count)
        {
            var gone = new List<uint>();
            foreach (var id in tracked.Keys)
                if (!seenThisFrame.Contains(id))
                    gone.Add(id);
            foreach (var id in gone)
            {
                Resolver.Forget(tracked[id].Address, id);
                limiter.Forget(id);
                tracked.Remove(id);
            }
        }

        foreach (var (id, e) in notified)
            if (tracked.TryGetValue(id, out var t))
                candidates.Add(new VoiceCandidate(id, e, t.Distance));
        notified.Clear();

        for (var i = delayed.Count - 1; i >= 0; i--)
        {
            if (delayed[i].At > limiter.Now)
                continue;
            var speaker = delayed[i].EntityId != 0 ? delayed[i].EntityId : PickVictorySpeaker();
            if (speaker != 0 && tracked.TryGetValue(speaker, out var t))
                candidates.Add(new VoiceCandidate(speaker, delayed[i].Event, t.Distance));
            delayed.RemoveAt(i);
        }

        if (candidates.Count == 0)
            return;
        var allowed = VoiceRateLimiter.Closest(speakers, rateSettings.ClosestSpeakers);
        foreach (var c in VoiceRateLimiter.Arbitrate(candidates, allowed))
            TrySpeak(c);
    }

    private void AddAllyDeath(uint deadId, Vector3 deadPos)
    {
        uint best = 0;
        var bestDist = Math.Max(1f, config.EnemyVoiceAllyDeathRadius);
        foreach (var (id, t) in tracked)
        {
            if (id == deadId || !t.Alive)
                continue;
            var d = Vector3.Distance(t.Position, deadPos);
            if (d < bestDist)
            {
                bestDist = d;
                best = id;
            }
        }
        if (best != 0)
            candidates.Add(new VoiceCandidate(best, VoiceEvent.AllyDeath, tracked[best].Distance));
    }

    /// <summary>Victory is called by the nearest living enemy that did not land the killing blow
    /// (the killer already spoke); the killer is the fallback.</summary>
    private uint PickVictorySpeaker()
    {
        var killer = combatEngine.LastPlayerKillerEntityId;
        uint best = 0;
        var bestDist = float.MaxValue;
        foreach (var (id, t) in tracked)
        {
            if (!t.Alive || id == killer || t.Distance >= bestDist)
                continue;
            bestDist = t.Distance;
            best = id;
        }
        return best != 0 ? best : killer;
    }

    private void TrySpeak(in VoiceCandidate c)
    {
        var id = (uint)c.ActorKey;
        if (!limiter.CanAdmit(c.ActorKey, c.Event) || !tracked.TryGetValue(id, out var t))
            return;
        var npc = npcSelector.GetSelectedNpc(id);
        if (npc == null || npc.Address != t.Address)
            return;

        var source = Resolver.Resolve(new VoiceSpeaker(t.Address, id, npc.BNpcBaseId, npc.IsClientControlled));
        var entry = source.IsNone ? -1 : Labels.Pick(source.LabelKey, c.Event, VoiceProfileResolver.EntryCount(source), random.Next);
        var path = entry < 0 ? null : ResolvePath(source);
        if (path == null)
        {
            // Nothing labeled or no file: spend the event so it does not retry every frame.
            limiter.SpendCooldownOnly(c.ActorKey, c.Event);
            return;
        }
        if (!limiter.TryAdmit(c.ActorKey, c.Event))
            return;

        Vector3? at = config.EnemyVoicePositional ? t.Position + new Vector3(0f, 1.2f, 0f) : null;
        if (Player.Play(path, (uint)entry, at, config.EnemyVoiceVolume, config.EnemyVoiceVolumeCategory) == 0)
            return;
        LastVoiceDescription = $"{npc.Name}: {c.Event} -> {source} #{entry}";
        VoicePlayed?.Invoke(t.Address, c.Event, source, entry);
    }

    private void RunAudition()
    {
        if (auditionRequest is not { } request)
            return;
        auditionRequest = null;
        var path = ResolvePath(request.Source);
        if (path == null)
        {
            LastVoiceDescription = $"Audition: no file for {request.Source}";
            return;
        }
        StopAudition();
        Vector3? at = request.Positional ? Core.Services.ObjectTable.LocalPlayer?.Position : null;
        var handle = Player.Play(path, request.Entry, at, config.EnemyVoiceVolume, config.EnemyVoiceVolumeCategory);
        lastAudition = (handle, path, request.Entry);
        LastVoiceDescription = handle != 0 ? $"Audition: {path} #{request.Entry}" : $"Audition refused: {path} #{request.Entry}";
    }
}
