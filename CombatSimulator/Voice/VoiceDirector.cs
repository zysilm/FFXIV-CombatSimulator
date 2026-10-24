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
    private const string VoiceLinesResource = "CombatSimulator.Resources.VoiceLines.json";
    // Bubble time when a line's .scd header could not be read for its length.
    private const float FallbackBubbleSeconds = 3f;
    private const float VictoryDelay = 1.5f;
    private const float MaxVictoryHold = 6f;
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
    // Holds released this frame, so a release that still finds the line gate closed falls through
    // to its shout instead of being queued again.
    private readonly HashSet<(uint EntityId, VoiceEvent Event)> releasedHolds = new();
    private readonly HashSet<uint> seenThisFrame = new();
    private readonly List<(ulong, float)> speakers = new();
    private readonly Dictionary<string, int> entryCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random random = new();
    private (VoiceSource Source, uint Entry, bool Positional)? auditionRequest;
    private (nint Handle, string Path, uint Entry) lastAudition;
    private bool hadState;
    private readonly VoiceLineSettings lineSettings = new();
    private readonly VoiceLinePicker linePicker;
    // Per voice id: bubble seconds (null = unreadable). Per yell id: client-language text.
    private readonly Dictionary<uint, float?> lineSeconds = new();
    private readonly Dictionary<(VoiceLineTextSheet, uint), string> yellText = new();
    private bool firstLineLogged;
    private int lastDeadCompanions = -1;
    private bool? lastPlayerAlive;

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
        linePicker = new VoiceLinePicker(lineSettings);
        Player = new NativeSoundPlayer(dataManager, log);
        Resolver = new VoiceProfileResolver(dataManager, config);
        ReloadLabels();
        LoadLines();
    }

    public NativeSoundPlayer Player { get; }
    public VoiceProfileResolver Resolver { get; }
    public VoiceLabelTable Labels { get; } = new();
    public string LastVoiceDescription { get; private set; } = string.Empty;
    public VoiceLinePicker Lines => linePicker;
    public int LinesSpoken { get; private set; }

    /// <summary>Spawned companions that are currently dead. A rise means an enemy just downed
    /// one: the same gloat as killing the player. Set by the plugin; null = no companions.</summary>
    public Func<int>? DeadCompanionCount { get; set; }

    private sealed class LineDto
    {
        public uint Voice { get; set; }
        public uint Yell { get; set; }
        public string[]? Events { get; set; }
        public string? Gender { get; set; }
        public bool Story { get; set; }
        public float Weight { get; set; } = 1f;
        public string? Age { get; set; }
        public string? Sheet { get; set; }
    }

    private sealed class LineFile
    {
        public List<LineDto>? Lines { get; set; }
    }

    /// <summary>The curated pool ships inside the DLL, so voiced lines work with no setup.</summary>
    private void LoadLines()
    {
        var parsed = new List<VoiceLineEntry>();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(VoiceLinesResource);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var file = JsonConvert.DeserializeObject<LineFile>(reader.ReadToEnd());
                foreach (var dto in file?.Lines ?? new List<LineDto>())
                {
                    var events = new List<VoiceEvent>();
                    foreach (var name in dto.Events ?? Array.Empty<string>())
                        if (Enum.TryParse<VoiceEvent>(name, true, out var e))
                            events.Add(e);
                    VoiceLineVoice? voice = dto.Gender?.ToLowerInvariant() switch
                    {
                        "male" => VoiceLineVoice.Male,
                        "female" => VoiceLineVoice.Female,
                        "nonhuman" => VoiceLineVoice.NonHuman,
                        _ => null,
                    };
                    var sheet = VoiceLineEntry.ParseSheet(dto.Sheet);
                    if (voice == null || sheet == null || dto.Voice == 0 || events.Count == 0)
                        continue;
                    parsed.Add(new VoiceLineEntry(dto.Voice, dto.Yell, events.ToArray(), voice.Value, dto.Story, dto.Weight,
                        VoiceLineEntry.ParseAge(dto.Age), sheet.Value));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Enemy voices: voiced line pool unreadable ({ex.Message}); voiced lines off.");
        }
        linePicker.SetEntries(parsed);
    }

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
        releasedHolds.Clear();
        candidates.Clear();
        limiter.Reset();
        linePicker.Reset();
        Resolver.Clear();
        hadState = false;
        // Re-seeded on the next combat frame: companions already dead are not a fresh kill.
        lastDeadCompanions = -1;
        lastPlayerAlive = null;
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
        lineSettings.Gap = Math.Clamp(config.EnemyVoiceLineGap, 0f, 30f);
        linePicker.Advance(deltaTime);

        RunAudition();

        if (!combatEngine.IsActive)
        {
            if (tracked.Count > 0 || delayed.Count > 0 || notified.Count > 0)
                Reset();
            return;
        }

        // Watched here rather than via CombatEngine.OnPlayerDeath, which only fires when the
        // ragdoll is enabled: the killing-blow and victory lines must not depend on that.
        var playerAlive = combatEngine.State.PlayerState.IsAlive;
        if (lastPlayerAlive == true && !playerAlive)
            NotifyPlayerDeath();
        lastPlayerAlive = playerAlive;

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
                    linePicker.Forget(id);
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
                linePicker.Forget(id);
                tracked.Remove(id);
            }
        }

        foreach (var (id, e) in notified)
            if (tracked.TryGetValue(id, out var t))
                candidates.Add(new VoiceCandidate(id, e, t.Distance));
        notified.Clear();

        // A companion went down. Who struck the blow is not tracked, so the nearest living
        // enemy gloats; that is also the one the player is most likely watching.
        var deadCompanions = DeadCompanionCount?.Invoke() ?? 0;
        if (lastDeadCompanions >= 0 && deadCompanions > lastDeadCompanions)
        {
            var gloater = NearestLiving(0);
            if (gloater != 0)
                candidates.Add(new VoiceCandidate(gloater, VoiceEvent.KilledPlayer, tracked[gloater].Distance));
        }
        lastDeadCompanions = deadCompanions;

        releasedHolds.Clear();
        for (var i = delayed.Count - 1; i >= 0; i--)
        {
            if (delayed[i].At > limiter.Now)
                continue;
            // The killer's gloat is usually still on screen when the win is due; hold the call
            // until the line gate opens (a few seconds at most) instead of losing it.
            // A held line names its speaker, so wait for that actor too; the victory call (no
            // speaker yet) only waits for the line gate.
            if (config.EnemyVoiceLines && limiter.Now - delayed[i].At < MaxVictoryHold &&
                !(delayed[i].EntityId != 0
                    ? SpeakerReady(delayed[i].EntityId, delayed[i].Event)
                    : linePicker.CanSpeak(0, delayed[i].Event)))
                continue;
            var speaker = delayed[i].EntityId != 0 ? delayed[i].EntityId : PickVictorySpeaker();
            if (speaker != 0 && tracked.TryGetValue(speaker, out var t))
            {
                candidates.Add(new VoiceCandidate(speaker, delayed[i].Event, t.Distance));
                releasedHolds.Add((speaker, delayed[i].Event));
            }
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
        var best = NearestLiving(killer);
        return best != 0 ? best : killer;
    }

    private uint NearestLiving(uint except)
    {
        uint best = 0;
        var bestDist = float.MaxValue;
        foreach (var (id, t) in tracked)
        {
            if (!t.Alive || id == except || t.Distance >= bestDist)
                continue;
            bestDist = t.Distance;
            best = id;
        }
        return best;
    }

    private bool SpeakerReady(ulong actorKey, VoiceEvent e) =>
        linePicker.CanSpeak(actorKey, e) && limiter.CanAdmit(actorKey, e);

    private void TrySpeak(in VoiceCandidate c)
    {
        var id = (uint)c.ActorKey;
        if (!tracked.TryGetValue(id, out var t))
            return;
        var npc = npcSelector.GetSelectedNpc(id);
        if (npc == null || npc.Address != t.Address)
            return;

        // Opening calls and killing-blow gloats land right as another line is often still showing
        // (the killing attack's own skill shout, the first enemy's engage), and the killer is often
        // still mid-shout itself. Dropping them read as "enemies never react to a kill", so they
        // wait — for the line gate and for the speaker — like the victory call does.
        var holdable = config.EnemyVoiceLines && c.Event is (VoiceEvent.Engage or VoiceEvent.KilledPlayer) &&
                       !releasedHolds.Contains((id, c.Event));
        if (holdable && !SpeakerReady(c.ActorKey, c.Event))
        {
            var heldEvent = c.Event;
            if (!delayed.Exists(d => d.EntityId == id && d.Event == heldEvent))
                delayed.Add((id, heldEvent, limiter.Now));
            return;
        }
        if (!limiter.CanAdmit(c.ActorKey, c.Event))
            return;

        if (config.EnemyVoiceLines && TrySpeakLine(c, npc, t))
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

    /// <summary>
    /// A curated voiced line with its bubble. False means "not this time" (gate closed, chance
    /// roll, no fitting line, file missing) and the caller falls through to label-based shouts.
    /// </summary>
    private bool TrySpeakLine(in VoiceCandidate c, SimulatedNpc npc, Tracked t)
    {
        if (linePicker.Count == 0 || !linePicker.CanSpeak(c.ActorKey, c.Event))
            return false;
        // Monsters have no curated lines (none is non-human), so they stay on their own sounds
        // rather than speak with a human voice.
        var (voice, age) = Resolver.ResolveBodyAndAge(t.Address, (uint)c.ActorKey);
        if (voice == VoiceLineVoice.NonHuman || linePicker.Available(c.Event, voice) == 0)
            return false;
        if (random.NextDouble() >= VoiceLinePicker.Chance(c.Event))
            return false;

        var language = Resolver.ClientLanguage;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var line = linePicker.Pick(c.Event, voice, random.NextDouble, age);
            if (line == null)
                return false;
            // Client-language audio only: the bubble shows client-language text, and a German
            // bubble over an English voice reads as broken. A missing file retires the line.
            var path = line.PathFor(language);
            var text = LineText(line);
            if (text.Length == 0 || !dataManager.FileExists(path))
            {
                linePicker.MarkUnavailable(line.VoiceId);
                continue;
            }
            var seconds = BubbleSeconds(line.VoiceId, path);
            if (seconds == null)
            {
                linePicker.MarkUnavailable(line.VoiceId);
                continue;
            }
            if (!limiter.TryAdmit(c.ActorKey, c.Event))
                return false;

            Vector3? at = config.EnemyVoicePositional ? t.Position + new Vector3(0f, 1.2f, 0f) : null;
            var handle = Player.Play(path, 0, at, config.EnemyVoiceVolume, config.EnemyVoiceVolumeCategory);
            if (!firstLineLogged)
            {
                // One line per session so a silent install is diagnosable from dalamud.log.
                firstLineLogged = true;
                log.Information($"Enemy voices: first voiced line vo {line.VoiceId} / yell {line.YellId} \"{text}\" path={path} " +
                                $"handle=0x{handle:X} playerAvailable={Player.IsAvailable}{(Player.FailureReason != null ? $" ({Player.FailureReason})" : string.Empty)}");
            }
            // The bubble still shows if the sound player refused: the line is readable either way.
            if (config.EnemyVoiceBubbles)
                VoiceBubble.Open(t.Address, text, seconds.Value, log);
            linePicker.Commit(c.ActorKey, c.Event, line, config.EnemyVoiceBubbles ? seconds.Value : seconds.Value - 1f);
            LinesSpoken++;
            LastVoiceDescription = $"{npc.Name}: {c.Event} -> \"{text}\" (vo {line.VoiceId})";
            if (handle != 0)
                VoicePlayed?.Invoke(t.Address, c.Event, new VoiceSource(VoiceSourceKind.YellLine, string.Empty, line.VoiceId - VoiceSelection.YellLineBase), 0);
            return true;
        }
        return false;
    }

    /// <summary>A curated line's words in the client language, from its text sheet ("" if missing).</summary>
    public string LineText(VoiceLineEntry line)
    {
        var key = (line.Sheet, line.YellId);
        if (yellText.TryGetValue(key, out var cached))
            return cached;
        var text = string.Empty;
        try
        {
            text = line.Sheet switch
            {
                VoiceLineTextSheet.InstanceContentTextData =>
                    dataManager.GetExcelSheet<Lumina.Excel.Sheets.InstanceContentTextData>()?.GetRowOrDefault(line.YellId)?.Text.ExtractText(),
                _ => dataManager.GetExcelSheet<Lumina.Excel.Sheets.NpcYell>()?.GetRowOrDefault(line.YellId)?.Text.ExtractText(),
            };
            text = text?.Trim() ?? string.Empty;
        }
        catch (Exception)
        {
            text = string.Empty;
        }
        yellText[key] = text;
        return text;
    }

    /// <summary>Bubble time from the file's header; null when the file cannot be read (some
    /// vo_line entries exist in the index but are empty, and those must never reach PlaySound).</summary>
    private float? BubbleSeconds(uint voiceId, string path)
    {
        if (lineSeconds.TryGetValue(voiceId, out var cached))
            return cached;
        float? result;
        try
        {
            var data = dataManager.GetFile(path)?.Data;
            result = data == null || data.Length < 0x40
                ? null
                : VoiceLineAudio.BubbleSeconds(VoiceLineAudio.TryReadDuration(data, out var s) ? s : FallbackBubbleSeconds - 1.2f);
        }
        catch (Exception)
        {
            result = null;
        }
        lineSeconds[voiceId] = result;
        return result;
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
