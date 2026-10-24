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
    private const string VoicePersonasResource = "CombatSimulator.Resources.VoicePersonas.json";
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
    private readonly Random random = new();
    // A voice preview asked for by the panel, played on the next framework tick (entity id, 0 = none).
    private uint previewRequest;
    private bool hadState;
    // SoundVolumeCategory for PlaySound: 2 = "Other" (scaled by the game's others' sounds slider).
    private const int VolumeCategory = 2;
    // Voices play at the speaker, a little above its feet.
    private static readonly Vector3 MouthOffset = new(0f, 1.2f, 0f);
    private readonly VoiceLineSettings lineSettings = new();
    private readonly VoiceLinePicker linePicker;
    // One speaker persona (voice actor) per enemy; empty data or a language without personas keeps
    // the per-line choice.
    private readonly VoicePersonaAssigner personas = new();
    private VoicePersonaTable personaTable = VoicePersonaTable.Empty;
    private bool linesHaveJaPersonas, linesHaveEnPersonas;
    // Per voice id: bubble seconds (null = unreadable). Per yell id: client-language text.
    private readonly Dictionary<uint, float?> lineSeconds = new();
    private readonly Dictionary<(VoiceLineTextSheet, uint), string> yellText = new();
    private bool firstLineLogged;
    private int lastDeadCompanions = -1;
    private bool? lastPlayerAlive;
    // Taunts over a fallen player: countdown to the next one (-1 = not running) and who spoke last.
    private float defeatTauntTimer = -1f;
    private uint lastDefeatSpeaker;
    // When each enemy's current line ends, so overlapping taunts never stack on one speaker.
    private readonly Dictionary<uint, float> lineEndsAt = new();
    // A guaranteed killing-blow line waits this long for its speaker before it is given up.
    private const float MaxKillHold = 30f;

    /// <summary>True while a scripted scene owns the speech bubbles (private stories); enemy lines
    /// hold off instead of talking over it.</summary>
    public Func<bool>? LinesSuppressed { get; set; }

    /// <summary>Actors driven by something else (Enemy Control, a story performer) that must not be
    /// picked for scripted taunts.</summary>
    public Func<nint, bool>? IsActorReserved { get; set; }

    private sealed class Tracked
    {
        public readonly VoiceActorWatch Watch = new();
        public nint Address;
        public string Name = string.Empty;
        public uint BNpcBaseId;
        public uint BNpcNameId;
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
        Resolver = new VoiceProfileResolver(dataManager);
        ReloadLabels();
        LoadLines();
        LoadPersonas();
    }

    public NativeSoundPlayer Player { get; }
    public VoiceProfileResolver Resolver { get; }
    public VoiceLabelTable Labels { get; } = new();
    public VoiceLinePicker Lines => linePicker;

    /// <summary>Average seconds between idle taunts of one engaged enemy, derived from the one
    /// chattiness knob (the pause between lines): the default 3.5 s pause gives a taunt every ~14 s.</summary>
    public static float IdleTauntInterval(float lineGap) => Math.Clamp(4f * lineGap, 5f, 60f);

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
        public bool Extended { get; set; }
        public bool Fx { get; set; }
        public string? Pja { get; set; }
        public string? Pen { get; set; }
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
                        VoiceLineEntry.ParseAge(dto.Age), sheet.Value, dto.Extended, dto.Fx,
                        string.IsNullOrEmpty(dto.Pja) ? null : dto.Pja, string.IsNullOrEmpty(dto.Pen) ? null : dto.Pen));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Enemy voices: voiced line pool unreadable ({ex.Message}); voiced lines off.");
        }
        // The whole pool; the picker itself keeps "fx" (processed boss/primal) lines from human speakers.
        linePicker.SetEntries(parsed);
        linesHaveJaPersonas = parsed.Exists(l => l.PersonaJa != null);
        linesHaveEnPersonas = parsed.Exists(l => l.PersonaEn != null);
    }

    /// <summary>Persona metadata ships inside the DLL too; without it lines are picked one by one.</summary>
    private void LoadPersonas()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(VoicePersonasResource);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                personaTable = VoicePersonaTable.Parse(reader.ReadToEnd());
            }
        }
        catch (Exception ex)
        {
            personaTable = VoicePersonaTable.Empty;
            log.Warning($"Enemy voices: speaker personas unreadable ({ex.Message}); lines are picked per line.");
        }
    }

    /// <summary>Data and language for the assigner; a language whose lines carry no persona id gets
    /// none (per-line choice).</summary>
    private void ConfigurePersonas()
    {
        var language = Resolver.ClientLanguage;
        var hasIds = VoicePersonaTable.PersonaLanguage(language) switch
        {
            "ja" => linesHaveJaPersonas,
            "en" => linesHaveEnPersonas,
            _ => false,
        };
        personas.Configure(hasIds ? personaTable : VoicePersonaTable.Empty, language, config.EnemyVoiceAllowStoryCharacters);
    }

    /// <summary>Personas pick the lines (data present for the client language).</summary>
    public bool PersonasActive => personas.Active;
    public string? PersonaLanguage => personas.Language;

    /// <summary>One row of the voice picker: a tracked enemy, its current persona, the hand-picked one
    /// (null = automatic) and every persona that fits its body, with readable labels.</summary>
    public sealed record VoiceEnemy(uint EntityId, string Name, VoicePersona? Current, string? ChosenId,
        IReadOnlyList<(string Id, string Label, string Category)> Choices);

    /// <summary>Tracked enemies for the voice picker, nearest first (empty when personas are off).</summary>
    public List<VoiceEnemy> VoiceEnemies()
    {
        var list = new List<VoiceEnemy>(tracked.Count);
        if (!personas.Active)
            return list;
        var order = new List<(uint Id, Tracked T)>();
        foreach (var (id, t) in tracked)
            order.Add((id, t));
        order.Sort((a, b) => a.T.Distance.CompareTo(b.T.Distance));
        var choices = AllVoiceChoices();
        foreach (var (id, t) in order)
        {
            var key = OverrideKey(t);
            var chosen = key != null && config.EnemyVoicePersonaOverrides.TryGetValue(key, out var c) ? c : null;
            list.Add(new VoiceEnemy(id, t.Name, PersonaOf(id, t).Persona, chosen, choices));
        }
        return list;
    }

    private List<(string, string, string)>? choiceCache;
    private (VoicePersonaTable, string?, bool) choiceCacheFor;

    /// <summary>Every assignable voice of the client language, any gender or boss, for the picker:
    /// the owner may give any enemy any voice.</summary>
    private List<(string, string, string)> AllVoiceChoices()
    {
        var table = personas.Table;
        var keyNow = (table, personas.Language, personas.AllowStoryCharacters);
        if (choiceCache == null || choiceCacheFor != keyNow)
        {
            choiceCache = new List<(string, string, string)>();
            foreach (var p in table.AllChoices(personas.Language!, personas.AllowStoryCharacters))
                choiceCache.Add((p.Id, table.Label(p), p.Category));
            choiceCacheFor = keyNow;
        }
        return choiceCache;
    }

    /// <summary>Which recorded voice kind an enemy's lines come from: its persona's (a hand-picked
    /// voice may differ from the body), else the body's own.</summary>
    private VoiceLineVoice LineVoice(uint id, VoiceLineVoice body) =>
        personas.Active && personas.Get(id)?.Persona is { } p ? p.SpeakerVoice : body;

    /// <summary>Hand-pick an enemy's voice (null = automatic again). Saved per kind of enemy and applied
    /// at once to every tracked enemy of that kind.</summary>
    public void SetVoiceChoice(uint entityId, string? personaId)
    {
        if (!tracked.TryGetValue(entityId, out var t) || OverrideKey(t) is not { } key)
            return;
        if (personaId == null)
            config.EnemyVoicePersonaOverrides.Remove(key);
        else
            config.EnemyVoicePersonaOverrides[key] = personaId;
        config.Save();
        foreach (var (id, other) in tracked)
            if (OverrideKey(other) == key)
            {
                personas.Forget(id);
                PersonaOf(id, other);
            }
    }

    /// <summary>Play one line of the enemy's current voice at the enemy (next framework tick).</summary>
    public void RequestPreview(uint entityId) => previewRequest = entityId;

    private static string? OverrideKey(Tracked t) => VoicePersonaAssigner.OverrideKey(t.BNpcBaseId, t.BNpcNameId, t.Name);

    /// <summary>The enemy's persona, assigned on first sight from its body (gender, BodyType age; a
    /// boss voice for monsters and demihumans), its identity and any hand-picked voice for its kind;
    /// stays for as long as the enemy is tracked.</summary>
    private VoicePersonaAssignment PersonaOf(uint id, Tracked t)
    {
        var existing = personas.Get(id);
        if (existing != null)
            return existing;
        var (voice, age) = Resolver.ResolveBodyAndAge(t.Address, id);
        var key = OverrideKey(t);
        var chosen = key != null && config.EnemyVoicePersonaOverrides.TryGetValue(key, out var c) ? c : null;
        return personas.Assign(id, VoicePersonaAssigner.IdentitySeed(t.BNpcBaseId, t.BNpcNameId, id), voice, age, chosen);
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
        if (GuaranteedKills)
            QueueKill(killer);
        else if (killer != 0)
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
        personas.Clear();
        hadState = false;
        // Re-seeded on the next combat frame: companions already dead are not a fresh kill.
        lastDeadCompanions = -1;
        lastPlayerAlive = null;
        defeatTauntTimer = -1f;
        lastDefeatSpeaker = 0;
        lineEndsAt.Clear();
    }

    private bool GuaranteedKills => config.EnemyVoiceGuaranteeKills;

    /// <summary>
    /// Queue a killing-blow line that must be heard. It skips the nearest-speakers cut and the
    /// per-actor kill cooldown, and waits (up to <see cref="MaxKillHold"/>) for the speaker to finish
    /// its own shout and for the line gate. A killer that cannot speak — dead, a monster, no
    /// fitting line — hands the line to the nearest living enemy that can.
    /// </summary>
    private void QueueKill(uint preferred)
    {
        var speaker = KillSpeaker(preferred);
        if (speaker == 0)
            return;
        limiter.ClearCooldown(speaker, VoiceEvent.KilledPlayer);
        if (!delayed.Exists(d => d.EntityId == speaker && d.Event == VoiceEvent.KilledPlayer))
            delayed.Add((speaker, VoiceEvent.KilledPlayer, limiter.Now));
    }

    private uint KillSpeaker(uint preferred)
    {
        if (CanVoiceLine(preferred, VoiceEvent.KilledPlayer))
            return preferred;
        uint best = 0;
        var bestDist = float.MaxValue;
        foreach (var (id, t) in tracked)
        {
            if (t.Distance >= bestDist || !CanVoiceLine(id, VoiceEvent.KilledPlayer))
                continue;
            bestDist = t.Distance;
            best = id;
        }
        return best;
    }

    /// <summary>Whether this tracked enemy is alive and has a curated line for the event in its voice
    /// (with personas: in its own persona or a fallback of it). Monsters and demihumans speak only
    /// through a boss persona, so they need personas (ja/en client).</summary>
    private bool CanVoiceLine(uint id, VoiceEvent e)
    {
        if (id == 0 || !tracked.TryGetValue(id, out var t) || !t.Alive)
            return false;
        var voice = Resolver.ResolveBody(t.Address, id);
        if (voice == VoiceLineVoice.NonHuman && !personas.Active)
            return false;
        if (personas.Active)
        {
            voice = LineVoice(id, voice);
            var chain = personas.Get(id)?.Chain;
            return chain != null && chain.Count > 0 && linePicker.Available(e, voice, chain, personas.Language!) > 0;
        }
        return linePicker.Available(e, voice) > 0;
    }

    public string? ResolvePath(VoiceSource source) =>
        VoiceSelection.ResolvePath(source, Resolver.ClientLanguage, dataManager.FileExists);

    // ---- Frame ----

    public void Tick(float deltaTime)
    {
        if (!config.EnableEnemyVoices)
        {
            previewRequest = 0;
            if (hadState)
                Reset();
            return;
        }
        hadState = true;

        limiter.Advance(deltaTime);
        lineSettings.Gap = Math.Clamp(config.EnemyVoiceLineGap, 0f, 30f);
        linePicker.Advance(deltaTime);
        ConfigurePersonas();

        RunPreview();

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
                    personas.Forget(id);
                }
                tracked[id] = t = new Tracked
                {
                    Address = npc.Address, Name = npc.Name, BNpcBaseId = npc.BNpcBaseId, BNpcNameId = npc.BNpcNameId,
                };
            }
            // First sight fixes the voice; later frames are a lookup.
            if (personas.Active)
                PersonaOf(id, t);

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

            if (npc.IsAlive && npc.IsEngaged &&
                random.NextDouble() < deltaTime / IdleTauntInterval(lineSettings.Gap))
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
                personas.Forget(id);
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
            if (GuaranteedKills)
                QueueKill(gloater);
            else if (gloater != 0)
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
            var guaranteedKill = delayed[i].Event == VoiceEvent.KilledPlayer && GuaranteedKills;
            var maxHold = guaranteedKill ? MaxKillHold : MaxVictoryHold;
            if (limiter.Now - delayed[i].At < maxHold &&
                !(delayed[i].EntityId != 0
                    ? SpeakerReady(delayed[i].EntityId, delayed[i].Event)
                    : linePicker.CanSpeak(0, delayed[i].Event)))
                continue;
            var speaker = delayed[i].EntityId != 0 ? delayed[i].EntityId : PickVictorySpeaker();
            // The held killer may have died or been despawned while it waited.
            if (guaranteedKill && !CanVoiceLine(speaker, VoiceEvent.KilledPlayer))
                speaker = KillSpeaker(speaker);
            if (speaker != 0 && tracked.TryGetValue(speaker, out var t))
            {
                candidates.Add(new VoiceCandidate(speaker, delayed[i].Event, t.Distance));
                releasedHolds.Add((speaker, delayed[i].Event));
            }
            delayed.RemoveAt(i);
        }

        if (candidates.Count > 0)
        {
            var allowed = VoiceRateLimiter.Closest(speakers, rateSettings.ClosestSpeakers);
            // A ranged killer can stand outside the nearest few; its line is guaranteed anyway.
            if (GuaranteedKills)
                foreach (var c in candidates)
                    if (c.Event == VoiceEvent.KilledPlayer)
                        allowed.Add(c.ActorKey);
            foreach (var c in VoiceRateLimiter.Arbitrate(candidates, allowed))
                TrySpeak(c);
        }

        TickDefeatTaunts(deltaTime, playerAlive);
    }

    /// <summary>
    /// After the party is wiped, surviving enemies take turns taunting the fallen player at the
    /// configured interval, from the chosen categories. Waits for the line gate like any line;
    /// ignores the per-event cooldowns, which are tuned for a running fight. Stops on revive,
    /// reset or while a scripted scene owns the bubbles.
    /// </summary>
    private void TickDefeatTaunts(float deltaTime, bool playerAlive)
    {
        // Not paused by a story: the defeat scene is exactly when the rest of the pack jeers. The
        // story's own performer is skipped in PickDefeatTaunter so it never talks over its lines.
        if (!config.EnemyVoiceDefeatTaunts || playerAlive || hasLivingCompanions())
        {
            defeatTauntTimer = -1f;
            return;
        }

        var interval = Math.Clamp(config.EnemyVoiceDefeatTauntInterval, 1f, 60f);
        if (defeatTauntTimer < 0f)
        {
            // The first taunt follows the kill and victory calls, not on top of them.
            defeatTauntTimer = Math.Max(interval, 3f);
            return;
        }
        defeatTauntTimer -= deltaTime;
        if (defeatTauntTimer > 0f)
            return;

        // Any of the speaker's own kill, victory and taunt lines (VoiceLinePicker.FallenTauntPool), so
        // one voice varies instead of repeating its single kill line. Speakers able to voice a kill
        // line are exactly those with a non-empty pool (same three events, same persona chain).
        // Taunts may overlap (down to one a second, each enemy under its own bubble), but an
        // enemy still talking is skipped; if everyone is mid-line, try again next frame.
        const VoiceEvent e = VoiceEvent.KilledPlayer;
        var speaker = PickDefeatTaunter(e);
        if (speaker == 0 || !tracked.TryGetValue(speaker, out var t))
            return;
        var npc = npcSelector.GetSelectedNpc(speaker);
        if (npc == null || npc.Address != t.Address)
            return;
        defeatTauntTimer = interval;
        if (TrySpeakLine(new VoiceCandidate(speaker, e, t.Distance), npc, t, scripted: true, fallenTaunt: true))
            lastDefeatSpeaker = speaker;
    }

    /// <summary>One of the three nearest enemies able to voice the event and not mid-line,
    /// avoiding whoever spoke the previous taunt when there is a choice.</summary>
    private uint PickDefeatTaunter(VoiceEvent e)
    {
        var able = new List<(uint Id, float Distance)>();
        foreach (var (id, t) in tracked)
            if (CanVoiceLine(id, e) && !(lineEndsAt.TryGetValue(id, out var end) && end > linePicker.Now) &&
                IsActorReserved?.Invoke(t.Address) != true)
                able.Add((id, t.Distance));
        if (able.Count == 0)
            return 0;
        able.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var choices = able.GetRange(0, Math.Min(3, able.Count));
        if (choices.Count > 1)
            choices.RemoveAll(c => c.Id == lastDefeatSpeaker);
        return choices[random.Next(choices.Count)].Id;
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
        // A story opening must not swallow a held killing-blow line; everything else waits.
        if (LinesSuppressed?.Invoke() == true && c.Event != VoiceEvent.KilledPlayer)
            return;
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
        var holdable = c.Event is (VoiceEvent.Engage or VoiceEvent.KilledPlayer) &&
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

        if (TrySpeakLine(c, npc, t))
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

        if (Player.Play(path, (uint)entry, t.Position + MouthOffset, config.EnemyVoiceVolume, VolumeCategory) == 0)
            return;
        VoicePlayed?.Invoke(t.Address, c.Event, source, entry);
    }

    /// <summary>
    /// A curated voiced line with its bubble. False means "not this time" (gate closed, chance
    /// roll, no fitting line, file missing) and the caller falls through to label-based shouts.
    /// <paramref name="fallenTaunt"/>: a taunt over the fallen party, drawn from the speaker's whole
    /// kill/victory/taunt pool instead of the event chain.
    /// </summary>
    private bool TrySpeakLine(in VoiceCandidate c, SimulatedNpc npc, Tracked t, bool scripted = false, bool fallenTaunt = false)
    {
        // Scripted talk (taunts over a fallen player) runs on its own clock and may overlap other
        // speakers; the line gate, event cooldowns, chance roll and rate budget are for reacting
        // to a running fight.
        if (linePicker.Count == 0 || !scripted && !linePicker.CanSpeak(c.ActorKey, c.Event))
            return false;
        // Monsters and demihumans speak only through a boss persona (processed boss voices); without
        // personas (de/fr client) they stay on their own sounds rather than borrow random voices.
        var (voice, age) = Resolver.ResolveBodyAndAge(t.Address, (uint)c.ActorKey);
        if (voice == VoiceLineVoice.NonHuman && !personas.Active)
            return false;
        // With personas the enemy speaks only its persona's lines (or a fallback's of the same kind); no
        // line there is silence for the line, never another random voice.
        var persona = personas.Active ? PersonaOf((uint)c.ActorKey, t) : null;
        if (persona?.Persona is { } assigned)
            voice = assigned.SpeakerVoice;
        var personaLanguage = personas.Language;
        if (persona != null
                ? persona.Chain.Count == 0 || linePicker.Available(c.Event, voice, persona.Chain, personaLanguage!) == 0
                : linePicker.Available(c.Event, voice) == 0)
            return false;
        if (!scripted && random.NextDouble() >= VoiceLinePicker.Chance(c.Event))
            return false;

        var language = Resolver.ClientLanguage;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var line = persona == null ? linePicker.Pick(c.Event, voice, random.NextDouble, age)
                : fallenTaunt ? linePicker.PickFallenTaunt(voice, persona.Chain, personaLanguage!, random.NextDouble)
                : linePicker.Pick(c.Event, voice, persona.Chain, personaLanguage!, random.NextDouble);
            if (line == null)
                return false;
            if (!Playable(line, language, out var path, out var text, out var seconds))
                continue;
            if (!scripted && !limiter.TryAdmit(c.ActorKey, c.Event))
                return false;

            var handle = PlayLine(line, path, text, seconds, t);
            linePicker.Commit(c.ActorKey, c.Event, line, config.EnemyVoiceBubbles ? seconds : seconds - 1f);
            lineEndsAt[(uint)c.ActorKey] = linePicker.Now + seconds;
            if (handle != 0)
                VoicePlayed?.Invoke(t.Address, c.Event, new VoiceSource(VoiceSourceKind.YellLine, string.Empty, line.VoiceId - VoiceSelection.YellLineBase), 0);
            return true;
        }
        return false;
    }

    /// <summary>Client-language audio only: the bubble shows client-language text, and a German
    /// bubble over an English voice reads as broken. A missing file, text or unreadable header
    /// retires the line for the session.</summary>
    private bool Playable(VoiceLineEntry line, string language, out string path, out string text, out float seconds)
    {
        path = line.PathFor(language);
        text = LineText(line);
        seconds = 0f;
        var s = text.Length == 0 || !dataManager.FileExists(path) ? null : BubbleSeconds(line.VoiceId, path);
        if (s == null)
        {
            linePicker.MarkUnavailable(line.VoiceId);
            return false;
        }
        seconds = s.Value;
        return true;
    }

    /// <summary>Play a checked line at the speaker, with its bubble. Returns the sound handle (0 =
    /// refused; the bubble still shows, the line is readable either way).</summary>
    private nint PlayLine(VoiceLineEntry line, string path, string text, float seconds, Tracked t)
    {
        var handle = Player.Play(path, 0, t.Position + MouthOffset, config.EnemyVoiceVolume, VolumeCategory);
        if (!firstLineLogged)
        {
            // One line per session so a silent install is diagnosable from dalamud.log.
            firstLineLogged = true;
            log.Information($"Enemy voices: first voiced line vo {line.VoiceId} / yell {line.YellId} \"{text}\" path={path} " +
                            $"handle=0x{handle:X} playerAvailable={Player.IsAvailable}{(Player.FailureReason != null ? $" ({Player.FailureReason})" : string.Empty)}");
        }
        if (config.EnemyVoiceBubbles)
            VoiceBubble.Open(t.Address, text, seconds, log);
        return handle;
    }

    /// <summary>The panel's Preview: one line of the enemy's own persona, at the enemy, through the same
    /// checks and player as a combat line. Leaves the fight's line gate and cooldowns alone.</summary>
    private void RunPreview()
    {
        var id = previewRequest;
        previewRequest = 0;
        if (id == 0 || !personas.Active || !tracked.TryGetValue(id, out var t))
            return;
        var npc = npcSelector.GetSelectedNpc(id);
        if (npc == null || npc.Address != t.Address || PersonaOf(id, t).Persona is not { } persona)
            return;
        var voice = persona.SpeakerVoice;
        var language = Resolver.ClientLanguage;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var line = linePicker.PickAnyOf(voice, persona.Id, personas.Language!, random.NextDouble);
            if (line == null)
                return;
            if (!Playable(line, language, out var path, out var text, out var seconds))
                continue;
            PlayLine(line, path, text, seconds, t);
            linePicker.MarkHeard(line.VoiceId);
            lineEndsAt[id] = linePicker.Now + seconds;
            return;
        }
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
}
