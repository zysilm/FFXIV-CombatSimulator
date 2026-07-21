using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;

// ActorVfxCreate spawns a .avfx on an actor (same function VFXEditor / Brio use)
// Signature from VFXEditor: scans the function body directly

namespace CombatSimulator.Animation;

public class ActionEffectRequest
{
    public uint SourceEntityId { get; set; }
    public Vector3 SourcePosition { get; set; }
    public uint ActionId { get; set; }
    public float AnimationLock { get; set; } = 0.6f;
    public float SourceRotation { get; set; }
    public bool IsSourcePlayer { get; set; }
    public bool IsRanged { get; set; }
    public NpcAttackStyle AttackStyle { get; set; } = NpcAttackStyle.Auto;
    public ushort AnimationStartTimelineId { get; set; }
    public ushort AnimationEndTimelineId { get; set; }

    // VFX paths resolved from Lumina + TMB data
    public string CastVfxPath { get; set; } = string.Empty;
    public string StartVfxPath { get; set; } = string.Empty;
    public List<string> CasterVfxPaths { get; set; } = new();
    public List<string> TargetVfxPaths { get; set; } = new();

    /// <summary>Cast/channel VFX already spawned when the cast began — don't spawn them again
    /// at the strike (see AnimationController.PlayNpcCastVfx).</summary>
    public bool SuppressCastVfx { get; set; }

    public List<TargetEffect> Targets { get; set; } = new();
}

public class TargetEffect
{
    public ulong TargetId { get; set; }
    public int Damage { get; set; }
    public int Healing { get; set; }
    public bool IsCritical { get; set; }
    public bool IsDirectHit { get; set; }
    public SimDamageType DamageType { get; set; }
}

public unsafe class AnimationController : IDisposable
{
    private readonly IPluginLog log;
    private readonly IClientState clientState;
    private readonly EmoteTimelinePlayer emotePlayer;
    public EmoteTimelinePlayer EmotePlayer => emotePlayer;
    private readonly Configuration config;
    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, float> actionAnimationDurationCache = new();
    private readonly Dictionary<string, ushort> actionTimelineKeyCache = new(StringComparer.OrdinalIgnoreCase);

    // Monotonically increasing sequence number for fabricated ActionEffects
    private uint globalSequence = 0x10000000;

    // Cached "Play Dead" emote timeline IDs (resolved at init)
    private ushort playDeadLoopTimeline;
    private ushort playDeadIntroTimeline;
    private bool playDeadResolved;

    // Battle dead ActionTimeline IDs keep weapons drawn (no sheathing).
    // Known IDs: battle/dead = 8935 (falling), battle/dead_pose = 8936 (settled).
    private ushort battleDeadIntroTimeline = 8935;
    private ushort battleDeadLoopTimeline = 8936;
    private bool battleDeadResolved;
    private ushort guardTimeline;
    // Guard-sound suppression. The guard timeline emits one of two sub-sounds (idx 39/40)
    // from a shared battle .scd — both are the crisp guard "ting" (confirmed via GuardSoundDiag).
    // We open a short window on each guard press and redirect those sub-sounds to a silent .scd
    // (SoundFilter's trick), so the animation plays without the sound; success feedback is the
    // guard VFX instead.
    private static readonly int[] GuardSoundIndices = { 39, 40 };
    private long guardSoundWindowUntilTicks;
    private nint noSoundScdPtr;
    private nint noSoundInfoPtr;
    // Set for one tick when a successful guard re-triggers the guard timeline: the next guard
    // ting (idx 39/40) is let through instead of muted, so the game itself emits the genuine ting
    // as success feedback. Direct replay via the play function doesn't work (it needs the caller's
    // finalize step), so we re-trigger the timeline instead.
    private bool allowGuardTingOnce;
    private bool guardVisualRestorePending;
    private bool guardSavedWeaponDrawn;
    private byte guardSavedModelState;
    private float guardVisualRestoreTimer;
    private bool playerSessionVisualCaptured;
    private bool playerSessionWeaponDrawn;
    private byte playerSessionModelState;
    private bool playerSessionInCombat;
    private bool playerSessionIsHostile;
    private ushort monsterRangedAutoAttackTimeline;
    private ushort npcMeleeAutoAttackTimeline;
    private ushort damageTimeline = 68; // ActionTimeline "battle/damage" — additive hit reaction

    // ActorVfxCreate spawns a .avfx particle effect attached to an actor.
    private delegate nint ActorVfxCreateDelegate(
        string path, nint a2, nint a3, float a4, char a5, ushort a6, char a7);

    private ActorVfxCreateDelegate? actorVfxCreate;
    private nint actorVfxCreateAddr;

    // ActorVfxRemove removes a spawned VFX by pointer (same function VFXEditor uses).
    private delegate nint ActorVfxRemoveDelegate(nint vfx, char a2);

    private ActorVfxRemoveDelegate? actorVfxRemove;
    private nint actorVfxRemoveAddr;

    private delegate void ActorVfxDtorDelegate(nint vfx);
    private Hook<ActorVfxDtorDelegate>? actorVfxDtorHook;

    // Game's per-effect SFX dispatch — the same function SoundFilter hooks to mute sounds.
    // a1 points at the sound info; the loaded .scd data pointer lives at *(byte**)(a1+8);
    // idx selects the sub-sound. Returning Original keeps audio untouched (log-only for now).
    private delegate void* PlaySpecificSoundDelegate(long a1, int idx);
    private Hook<PlaySpecificSoundDelegate>? playSpecificSoundHook;
    private readonly List<TrackedActorVfx> trackedActorVfx = new();
    // Manually-spawned VFX are orphans — nothing in the game's action lifecycle reaps them, so they
    // are tracked and removed on a timer or they linger until a client restart. The budget is split
    // by action type: spells run a cast bar and a long effect, so they need far longer on screen
    // than a physical weaponskill.
    private const float PhysicalVfxTtl = 2.5f;
    private const float SpellVfxTtl = 6.0f;
    private const float UntrackedVfxTtl = 0.0f;
    private const int MaxTrackedActorVfx = 256;

    private sealed class TrackedActorVfx
    {
        public nint Ptr;
        public float Remaining;
        public string Path = string.Empty;
        public uint OwnerEntityId;
    }

    /// <summary>Resolved address of the native ActorVfxCreate function (0 if unresolved).</summary>
    public nint ActorVfxCreateAddress => actorVfxCreateAddr;
    /// <summary>Resolved address of the native ActorVfxRemove function (0 if unresolved).</summary>
    public nint ActorVfxRemoveAddress => actorVfxRemoveAddr;

    // Default hit VFX path candidates (tried in order until one sticks)
    public static readonly string[] HitVfxCandidates =
    {
        "vfx/common/eff/dk02ht_totu0y.avfx",
        "vfx/common/eff/cmhit_fire1t.avfx",
    };

    public AnimationController(
        IPluginLog log,
        IClientState clientState,
        IDataManager dataManager,
        IGameInteropProvider gameInterop,
        ISigScanner sigScanner,
        Configuration config)
    {
        this.log = log;
        this.clientState = clientState;
        this.config = config;
        this.dataManager = dataManager;
        this.emotePlayer = new EmoteTimelinePlayer(log);

        ResolvePlayDeadTimelines(dataManager);
        ResolveBattleDeadTimeline(dataManager);
        ResolveGuardTimeline(dataManager);
        ResolveDamageTimeline(dataManager);
        ResolveMonsterRangedAttackTimeline(dataManager);
        ResolveMeleeAutoAttackTimeline(dataManager);
        ResolveActorVfxCreate(sigScanner);
        ResolveActorVfxRemove(sigScanner);
        ResolveActorVfxDtor(gameInterop, sigScanner);
        ResolvePlaySpecificSoundHook(gameInterop, sigScanner);

        log.Info("AnimationController: Initialized with ActionEffectHandler + emote timeline system.");
    }

    private void ResolveActorVfxCreate(ISigScanner sigScanner)
    {
        try
        {
            var addr = sigScanner.ScanText(
                "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8");
            actorVfxCreateAddr = addr;
            actorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(addr);
            log.Info($"AnimationController: ActorVfxCreate resolved at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: Could not resolve ActorVfxCreate; hit VFX will be unavailable.");
        }
    }

    private void ResolveActorVfxRemove(ISigScanner sigScanner)
    {
        try
        {
            // Same signature + pointer chase as VFXEditor (Constants.ActorVfxRemoveSig)
            var tempAddr = sigScanner.ScanText("0F 11 48 10 48 8D 05") + 7;
            var addr = Marshal.ReadIntPtr(tempAddr + Marshal.ReadInt32(tempAddr) + 4);
            actorVfxRemoveAddr = addr;
            actorVfxRemove = Marshal.GetDelegateForFunctionPointer<ActorVfxRemoveDelegate>(addr);
            log.Info($"AnimationController: ActorVfxRemove resolved at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: Could not resolve ActorVfxRemove; VFX cleanup unavailable.");
        }
    }

    private void ResolveActorVfxDtor(IGameInteropProvider gameInterop, ISigScanner sigScanner)
    {
        try
        {
            var addr = sigScanner.ScanText(
                "48 89 5C 24 ?? 57 48 83 EC ?? 48 8D 05 ?? ?? ?? ?? 48 8B D9 48 89 01 8B FA 48 8D 05 ?? ?? ?? ?? 48 89 81 ?? ?? ?? ?? 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 ?? 48 8B 01 48 8B D3");
            actorVfxDtorHook = gameInterop.HookFromAddress<ActorVfxDtorDelegate>(addr, ActorVfxDtorDetour);
            actorVfxDtorHook.Enable();
            log.Info($"AnimationController: ActorVfxDtor hook enabled at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: Could not hook ActorVfxDtor; tracked VFX cleanup will use TTL only.");
        }
    }

    private void ActorVfxDtorDetour(nint vfx)
    {
        try
        {
            for (var i = trackedActorVfx.Count - 1; i >= 0; i--)
            {
                if (trackedActorVfx[i].Ptr == vfx)
                {
                    trackedActorVfx.RemoveAt(i);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Error pruning tracked actor VFX on dtor.");
        }

        actorVfxDtorHook!.Original(vfx);
    }

    /// <summary>
    /// Whether hit VFX spawning is available (ActorVfxCreate was resolved).
    /// </summary>
    public bool HitVfxAvailable => actorVfxCreate != null;

    public ushort ResolveActionTimelineKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        if (actionTimelineKeyCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    if (!row.Key.ToString().Equals(key, StringComparison.OrdinalIgnoreCase))
                        continue;

                    cached = (ushort)row.RowId;
                    actionTimelineKeyCache[key] = cached;
                    return cached;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"AnimationController: failed to resolve ActionTimeline key '{key}'.");
        }

        actionTimelineKeyCache[key] = 0;
        return 0;
    }

    private void ResolvePlayDeadTimelines(IDataManager dataManager)
    {
        try
        {
            var emoteSheet = dataManager.GetExcelSheet<Emote>();
            if (emoteSheet == null)
            {
                log.Warning("AnimationController: Emote sheet not found.");
                return;
            }

            // Auto-detect: search for "Play Dead" emote by name
            foreach (var emote in emoteSheet)
            {
                var name = emote.Name.ToString();
                if (name.Equals("Play Dead", StringComparison.OrdinalIgnoreCase))
                {
                    playDeadLoopTimeline = (ushort)emote.ActionTimeline[0].RowId;
                    playDeadIntroTimeline = (ushort)emote.ActionTimeline[1].RowId;
                    playDeadResolved = playDeadLoopTimeline != 0 || playDeadIntroTimeline != 0;
                    log.Info($"AnimationController: Resolved 'Play Dead' emote (id={emote.RowId}) -> loop={playDeadLoopTimeline}, intro={playDeadIntroTimeline}");
                    return;
                }
            }

            log.Warning("AnimationController: Could not find 'Play Dead' emote. Death will use fallback (CharacterModes.Dead for NPCs).");
        }
        catch (Exception ex)
        {
            log.Error(ex, "AnimationController: Failed to resolve Play Dead emote timelines.");
        }
    }

    private void ResolveBattleDeadTimeline(IDataManager dataManager)
    {
        // Search by key first (survives patch ID shifts)
        ushort foundIntro = 0, foundLoop = 0;
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    var key = row.Key.ToString();
                    if (key == "battle/dead")
                        foundIntro = (ushort)row.RowId;
                    else if (key == "battle/dead_pose")
                        foundLoop = (ushort)row.RowId;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: ActionTimeline sheet search failed.");
        }

        // Use key search results, fall back to hardcoded IDs (8935/8936)
        if (foundIntro != 0) battleDeadIntroTimeline = foundIntro;
        if (foundLoop != 0) battleDeadLoopTimeline = foundLoop;

        battleDeadResolved = battleDeadIntroTimeline != 0 && battleDeadLoopTimeline != 0;
        log.Info($"AnimationController: Battle dead timelines: intro={battleDeadIntroTimeline}, loop={battleDeadLoopTimeline}, resolved={battleDeadResolved} (key search: intro={foundIntro}, loop={foundLoop})");
    }

    private void ResolveGuardTimeline(IDataManager dataManager)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                var key = row.Key.ToString();
                if (key.Contains("guard", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("block", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("parry", StringComparison.OrdinalIgnoreCase))
                {
                    guardTimeline = (ushort)row.RowId;
                    log.Info($"AnimationController: Resolved guard timeline {key} -> {guardTimeline}.");
                    return;
                }
            }

            log.Warning("AnimationController: Could not find a guard/block/parry ActionTimeline; guard will use fallback flinch timeline.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: Failed to resolve guard timeline.");
        }
    }

    private void ResolveDamageTimeline(IDataManager dataManager)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            if (sheet == null)
                return;

            // "battle/damage" is the combat-stance hit reaction (additive flinch). Resolve by key
            // so it survives row-id shifts; fall back to the known id 68.
            foreach (var row in sheet)
            {
                if (row.Key.ToString().Equals("battle/damage", StringComparison.OrdinalIgnoreCase))
                {
                    damageTimeline = (ushort)row.RowId;
                    log.Info($"AnimationController: Resolved damage timeline battle/damage -> {damageTimeline}.");
                    return;
                }
            }

            log.Warning($"AnimationController: Could not find ActionTimeline battle/damage; using fallback {damageTimeline}.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: Failed to resolve damage timeline.");
        }
    }

    /// <summary>
    /// Action-Mode hit reaction: play the additive "battle/damage" flinch on the struck actor
    /// (player, companion, or NPC) as a one-shot blend over its current animation.
    /// </summary>
    public void PlayHitReactionAnimation(nint actorAddress)
    {
        if (actorAddress == nint.Zero || damageTimeline == 0)
            return;

        emotePlayer.PlayOneShot((Character*)actorAddress, damageTimeline);
    }

    /// <summary>Set an actor's animation timeline speed (used for hitstop: 0 = frozen, 1 = normal).
    /// Caller is responsible for restoring it.</summary>
    public void SetActorAnimationSpeed(nint actorAddress, float speed)
    {
        if (actorAddress == nint.Zero)
            return;
        try { ((Character*)actorAddress)->Timeline.OverallSpeed = speed; }
        catch (Exception ex) { log.Warning(ex, "SetActorAnimationSpeed failed."); }
    }

    /// <summary>Spawn a one-shot hit spark on the struck target (reuses the configured HitVfxPath).</summary>
    public void SpawnHitSpark(nint targetAddress)
    {
        if (targetAddress == nint.Zero || actorVfxCreate == null)
            return;
        var path = !string.IsNullOrWhiteSpace(config.HitVfxPath) ? config.HitVfxPath : HitVfxCandidates[0];
        var player = Core.Services.ObjectTable.LocalPlayer;
        var caster = player != null ? player.Address : targetAddress;
        // ActorVfxCreate spawns on the THIRD arg (orientTo) → pass the target there so the spark
        // lands on the enemy, not on the caster/player.
        try { SpawnAndTrack(path, caster, targetAddress, 0, UntrackedVfxTtl); }
        catch (Exception ex) { log.Error(ex, "SpawnHitSpark failed."); }
    }

    private void ResolvePlaySpecificSoundHook(IGameInteropProvider gameInterop, ISigScanner sigScanner)
    {
        try
        {
            SetUpNoSound();

            // SoundFilter's PlaySpecificSound. This sig is a function prologue, so ScanText
            // lands on the entry directly — no E8/call-site offset resolution needed (the
            // earlier InitSound attempt failed precisely because it used a call-site sig).
            const string playSpecificSoundSig =
                "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 33 F6 8B DA 48 8B F9 0F BA E2 0F";
            var addr = sigScanner.ScanText(playSpecificSoundSig);
            playSpecificSoundHook = gameInterop.HookFromAddress<PlaySpecificSoundDelegate>(addr, PlaySpecificSoundDetour);
            playSpecificSoundHook.Enable();
            log.Info($"AnimationController: PlaySpecificSound hook enabled at 0x{addr:X}.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: Failed to enable PlaySpecificSound hook.");
        }
    }

    /// <summary>How long a manually-spawned VFX lives, by action type (see the TTL constants).</summary>
    private static float VfxTtlFor(SimDamageType damageType)
        => damageType == SimDamageType.Magical ? SpellVfxTtl : PhysicalVfxTtl;

    public float ResolveActionAnimationDuration(uint actionId)
    {
        actionId = actionId == 0 ? 7u : actionId;
        if (actionAnimationDurationCache.TryGetValue(actionId, out var cached))
            return cached;

        var duration = ResolveActionAnimationDurationUncached(actionId);
        actionAnimationDurationCache[actionId] = duration;
        return duration;
    }

    private float ResolveActionAnimationDurationUncached(uint actionId)
    {
        const float fallbackSeconds = 0.6f;

        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            var row = sheet?.GetRowOrDefault(actionId);
            if (row == null)
                return fallbackSeconds;

            var action = row.Value;
            var duration = ResolveTimelineDuration(action.AnimationEnd.ValueNullable);

            return duration is > 0.05f ? duration.Value : fallbackSeconds;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"AnimationController: Failed to resolve animation duration for action {actionId}.");
            return fallbackSeconds;
        }
    }

    private float? ResolveTimelineDuration(ActionTimeline? timeline)
    {
        if (timeline == null)
            return null;

        var key = timeline.Value.Key.ExtractText();
        if (string.IsNullOrWhiteSpace(key) || key.Contains("[SKL_ID]", StringComparison.Ordinal))
            return null;

        return ResolveTmbDuration($"chara/action/{key}.tmb");
    }

    private float? ResolveTmbDuration(string tmbPath)
    {
        try
        {
            var file = dataManager.GetFile(tmbPath);
            var data = file?.Data;
            if (data == null || data.Length < 12)
                return null;

            if (data[0] != (byte)'T' || data[1] != (byte)'M' ||
                data[2] != (byte)'L' || data[3] != (byte)'B')
                return null;

            var itemCount = BitConverter.ToInt32(data, 8);
            var offset = 12;
            var maxTime = 0;

            for (var i = 0; i < itemCount - 2 && offset + 12 <= data.Length; i++)
            {
                var size = BitConverter.ToInt32(data, offset + 4);
                if (size <= 0 || offset + size > data.Length)
                    break;

                if (data[offset] == (byte)'C')
                {
                    var time = BitConverter.ToInt16(data, offset + 10);
                    if (time > maxTime)
                        maxTime = time;
                }

                offset += size;
            }

            return maxTime > 0 ? maxTime / 30f : null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"AnimationController: Failed to parse TMB duration: {tmbPath}");
            return null;
        }
    }

    // Build the silent-sound redirect target (SoundFilter's gaya_nosound.scd trick): an
    // unmanaged "sound info" whose data pointer (+8) points at a silent .scd, used to swap out
    // the guard sound without skipping the original call (skipping risks a null-deref crash).
    private void SetUpNoSound()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("CombatSimulator.Resources.gaya_nosound.scd");
            if (stream == null)
            {
                log.Warning("AnimationController: gaya_nosound.scd not embedded; guard sound mute disabled.");
                return;
            }

            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            noSoundScdPtr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, noSoundScdPtr, bytes.Length);
            noSoundInfoPtr = BuildSoundInfo(noSoundScdPtr);

            log.Info("AnimationController: guard silent-sound redirect initialized.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: failed to set up guard sound mute; no mute applied.");
            noSoundInfoPtr = 0;
        }
    }

    // Builds the minimal unmanaged "sound info" PlaySpecificSound expects (SoundFilter's layout):
    // a 256B block whose +8 points at .scd data, +0x88 = 0x54, +0x94 = 0. Used both for the
    // silent redirect and for replaying the guard ting from the live guard .scd.
    private static nint BuildSoundInfo(nint scdData)
    {
        var info = Marshal.AllocHGlobal(256);
        for (var i = 0; i < 256; i += 8)
            Marshal.WriteInt64(info + i, 0);
        Marshal.WriteIntPtr(info + 8, scdData);
        Marshal.WriteInt32(info + 0x88, 0x54);
        Marshal.WriteInt16(info + 0x94, 0);
        return info;
    }

    // During the guard window, redirect the crisp guard sub-sounds (idx 39/40) to the silent
    // .scd; all other audio passes through untouched. Original is always called.
    private void* PlaySpecificSoundDetour(long a1, int idx)
    {
        try
        {
            if (a1 != 0 &&
                Environment.TickCount64 <= guardSoundWindowUntilTicks &&
                Array.IndexOf(GuardSoundIndices, idx) >= 0)
            {
                // A successful guard re-triggers the timeline with allowGuardTingOnce set, so that
                // one ting passes through; every other in-window press ting is muted.
                var willMute = noSoundInfoPtr != 0 && !allowGuardTingOnce;
                allowGuardTingOnce = false;

                log.Verbose($"[Guard] ting idx={idx} muted={willMute}");

                if (willMute)
                {
                    a1 = noSoundInfoPtr;
                    idx = 0;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Guard sound mute error.");
        }

        return playSpecificSoundHook!.Original(a1, idx);
    }

    private void ResolveMonsterRangedAttackTimeline(IDataManager dataManager)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                if (row.Key.ToString() == "battle/auto_attack_shot1_mon")
                {
                    monsterRangedAutoAttackTimeline = (ushort)row.RowId;
                    log.Info($"AnimationController: Resolved monster ranged auto-attack timeline battle/auto_attack_shot1_mon -> {monsterRangedAutoAttackTimeline}.");
                    return;
                }
            }

            log.Warning("AnimationController: Could not find ActionTimeline key battle/auto_attack_shot1_mon.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: Failed to resolve monster ranged auto-attack timeline.");
        }
    }

    private void ResolveMeleeAutoAttackTimeline(IDataManager dataManager)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                if (row.Key.ToString() == "battle/auto_attack")
                {
                    npcMeleeAutoAttackTimeline = (ushort)row.RowId;
                    log.Info($"AnimationController: Resolved melee auto-attack timeline battle/auto_attack -> {npcMeleeAutoAttackTimeline}.");
                    return;
                }
            }

            log.Warning("AnimationController: Could not find ActionTimeline key battle/auto_attack.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: Failed to resolve melee auto-attack timeline.");
        }
    }

    /// <summary>
    /// Advance manually tracked actor VFX timers and remove stale caster-side VFX.
    /// </summary>
    public void Tick(float deltaTime)
    {
        TickGuardVisualRestore(deltaTime);
        TickPendingTargetVfx(deltaTime);

        for (var i = trackedActorVfx.Count - 1; i >= 0; i--)
        {
            var tracked = trackedActorVfx[i];
            tracked.Remaining -= deltaTime;
            if (tracked.Remaining <= 0)
                RemoveTrackedActorVfxAt(i);
        }
    }

    public void RemoveAllActiveVfx()
    {
        for (var i = trackedActorVfx.Count - 1; i >= 0; i--)
            RemoveTrackedActorVfxAt(i);
        trackedActorVfx.Clear();
    }

    /// <summary>
    /// Play attack animation + VFX + hit reaction via ActionEffectHandler.Receive().
    /// This triggers the game's full combat visual pipeline: caster animation, target hit
    /// reaction, VFX particles, damage flytext, and sound effects in one call.
    /// </summary>
    /// <summary>
    /// Spawn an action's CAST-time VFX (the channelling circle / cast glow) on an NPC caster, at the
    /// moment the cast BEGINS. Ranged/magic attacks get no windup swing, so nothing marked the cast
    /// visually until the strike — where the cast VFX appeared and vanished in the same instant.
    /// Returns true if anything was spawned, so the strike can skip re-spawning it.
    /// </summary>
    public bool PlayNpcCastVfx(SimulatedNpc npc, ActionData data)
    {
        if (!config.EnableCharacterVfx)
            return false;
        if (string.IsNullOrEmpty(data.CastVfxPath) && string.IsNullOrEmpty(data.StartVfxPath))
            return false;

        try
        {
            var (casterAddr, casterEntityId) = ResolveActorAddress(npc.SimulatedEntityId, false);
            if (casterAddr == 0)
                return false;

            // Lit at the START of the cast, so it must outlive the whole cast bar.
            var ttl = VfxTtlFor(data.DamageType);

            var spawned = false;
            if (!string.IsNullOrEmpty(data.CastVfxPath))
            {
                SpawnAndTrack(data.CastVfxPath, casterAddr, casterAddr, casterEntityId, ttl);
                spawned = true;
            }
            if (!string.IsNullOrEmpty(data.StartVfxPath))
            {
                SpawnAndTrack(data.StartVfxPath, casterAddr, casterAddr, casterEntityId, ttl);
                spawned = true;
            }
            return spawned;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to spawn cast VFX for '{npc.Name}'.");
            return false;
        }
    }

    /// <summary>
    /// Spawn only an action's VFX (caster + target), with no animation or flytext. Used at the
    /// strike of a melee skill whose swing already played as a windup pose — so the skill still
    /// shows its VFX without triggering a second caster animation. Honours the
    /// EnableCharacterVfx/EnableTargetVfx config internally.
    /// </summary>
    public void PlayActionVfx(ActionEffectRequest request)
    {
        if (request.Targets.Count == 0)
            return;

        try
        {
            if (config.EnableCharacterVfx || config.EnableTargetVfx)
                SpawnActionVfx(request);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to play action VFX.");
        }
    }

    public void PlayActionEffect(ActionEffectRequest request)
    {
        if (request.Targets.Count == 0)
            return;

        try
        {
            // Spawn skill VFX via ActorVfxCreate (off by default; other plugins that
            // hook this function may crash when accessing our modified NPC actors)
            if (config.EnableCharacterVfx || config.EnableTargetVfx)
                SpawnActionVfx(request);

            // Use ActionEffectHandler.Receive() for flytext + damage numbers
            CallActionEffectReceive(request);

            if (request.IsSourcePlayer)
                PlayPlayerActionTimeline(request);

            if (!request.IsSourcePlayer && request.AttackStyle == NpcAttackStyle.Ranged)
                PlayMonsterRangedAttackTimeline(request.SourceEntityId);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to play action effect.");
        }
    }

    /// <summary>
    /// Spawn action-specific VFX using paths extracted from Lumina data + TMB files.
    /// All actor addresses are resolved via managed ObjectTable at the moment of use
    /// to prevent stale pointer crashes.
    /// </summary>
    private void SpawnActionVfx(ActionEffectRequest request)
    {
        if (actorVfxCreate == null) return;

        try
        {
            // Resolve caster address via managed ObjectTable (fresh lookup, never cached)
            var (casterAddr, casterEntityId) = ResolveActorAddress(request.SourceEntityId, request.IsSourcePlayer);
            if (casterAddr == 0) return;

            // Resolve first target for orientation
            nint firstTargetAddr = 0;
            uint firstTargetEntityId = 0;
            if (request.Targets.Count > 0)
            {
                (firstTargetAddr, firstTargetEntityId) = ResolveActorAddress((uint)request.Targets[0].TargetId, false);
            }

            // Spawn caster VFX (cast circle, skill effects from AnimationEnd TMB)
            nint orientAddr = firstTargetAddr != 0 ? firstTargetAddr : casterAddr;

            if (config.EnableCharacterVfx)
            {
                var ttl = request.AttackStyle == NpcAttackStyle.Magic ? SpellVfxTtl : PhysicalVfxTtl;

                if (!request.SuppressCastVfx && !string.IsNullOrEmpty(request.CastVfxPath))
                    SpawnAndTrack(request.CastVfxPath, casterAddr, orientAddr, casterEntityId, ttl);

                if (!request.SuppressCastVfx && !string.IsNullOrEmpty(request.StartVfxPath))
                    SpawnAndTrack(request.StartVfxPath, casterAddr, orientAddr, casterEntityId, ttl);

                foreach (var path in request.CasterVfxPaths)
                    SpawnAndTrack(path, casterAddr, orientAddr, casterEntityId, ttl);
            }

            // Spawn target VFX (hit/impact effects from ActionTimelineHit TMB)
            if (config.EnableTargetVfx)
            {
                foreach (var target in request.Targets)
                {
                    if (target.Damage <= 0 && target.Healing <= 0) continue;

                    // Fresh resolve for each target
                    var (targetAddr, targetEntityId) = ResolveActorAddress((uint)target.TargetId, false);
                    if (targetAddr == 0) continue;

                    // ActorVfxCreate spawns the effect on its THIRD arg (orientTo), so the struck
                    // TARGET must be passed there (not the caster) or the hit VFX lands on the player.
                    // EmitTargetVfx defers player hits to the contact frame (hit-feedback delay).
                    if (request.TargetVfxPaths.Count > 0)
                    {
                        foreach (var path in request.TargetVfxPaths)
                            EmitTargetVfx(path, request, (uint)target.TargetId, casterAddr, targetAddr, targetEntityId);
                    }

                    if ((request.TargetVfxPaths.Count == 0 || request.IsSourcePlayer) && config.EnableHitVfx)
                    {
                        var vfxPath = config.HitVfxPath;
                        if (!string.IsNullOrWhiteSpace(vfxPath))
                            EmitTargetVfx(vfxPath, request, (uint)target.TargetId, casterAddr, targetAddr, targetEntityId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to spawn action VFX.");
        }
    }

    private void PlayPlayerActionTimeline(ActionEffectRequest request)
    {
        if (request.AttackStyle != NpcAttackStyle.Magic && !request.IsRanged)
            return;

        var timelineId = request.AnimationEndTimelineId != 0
            ? request.AnimationEndTimelineId
            : request.AnimationStartTimelineId;
        if (timelineId == 0)
            return;

        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        var targetObjId = request.Targets.Count > 0 ? request.Targets[0].TargetId : 0;
        emotePlayer.PlayOneShot((Character*)player.Address, timelineId, targetObjId);
    }

    /// <summary>
    /// Resolve an entity ID to its current address via managed Dalamud APIs.
    /// Returns (0, 0) if the entity is no longer present.
    /// </summary>
    private (nint address, uint entityId) ResolveActorAddress(uint entityId, bool isPlayer)
    {
        if (isPlayer)
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null)
                return (player.Address, player.EntityId);
        }
        else
        {
            foreach (var obj in Core.Services.ObjectTable)
            {
                if (obj.EntityId == entityId)
                    return (obj.Address, obj.EntityId);
            }
        }
        return (0, 0);
    }

    // Player hit/target VFX, deferred so it lands on the weapon's contact frame in sync with the
    // hit-feedback delay (camera/hitstop/spark), instead of at swing start. Re-resolves both actors
    // at fire time so a target that died/despawned during the windup is simply skipped.
    private struct PendingTargetVfx
    {
        public string Path;
        public uint TargetSimId;
        public uint CasterEntityId;
        public bool CasterIsPlayer;
        public float Timer;
    }
    private readonly List<PendingTargetVfx> pendingTargetVfx = new();

    private void EmitTargetVfx(string path, ActionEffectRequest request, uint targetSimId, nint casterAddrNow, nint targetAddrNow, uint targetEntityId)
    {
        var delay = (request.IsSourcePlayer && config.EnableHitFeedback) ? MathF.Max(0f, config.HitFeedbackDelay) : 0f;
        if (delay <= 0.001f)
        {
            SpawnAndTrack(path, casterAddrNow, targetAddrNow, targetEntityId, UntrackedVfxTtl);
            return;
        }
        pendingTargetVfx.Add(new PendingTargetVfx
        {
            Path = path,
            TargetSimId = targetSimId,
            CasterEntityId = request.SourceEntityId,
            CasterIsPlayer = request.IsSourcePlayer,
            Timer = delay,
        });
    }

    private void TickPendingTargetVfx(float dt)
    {
        for (var i = pendingTargetVfx.Count - 1; i >= 0; i--)
        {
            var p = pendingTargetVfx[i];
            p.Timer -= dt;
            if (p.Timer > 0f)
            {
                pendingTargetVfx[i] = p;
                continue;
            }
            pendingTargetVfx.RemoveAt(i);
            var (tAddr, tId) = ResolveActorAddress(p.TargetSimId, false);
            if (tAddr == 0)
                continue;
            var (cAddr, _) = ResolveActorAddress(p.CasterEntityId, p.CasterIsPlayer);
            SpawnAndTrack(p.Path, cAddr, tAddr, tId, UntrackedVfxTtl);
        }
    }

    /// <summary>
    /// Spawn a VFX via ActorVfxCreate. Positive ttl values track the returned pointer
    /// for cleanup; ttl <= 0 leaves the VFX to expire naturally.
    /// </summary>
    private void SpawnAndTrack(string path, nint attachTo, nint orientTo, uint ownerEntityId, float ttl)
    {
        try
        {
            var ptr = actorVfxCreate!(path, attachTo, orientTo, -1, (char)0, 0, (char)0);
            if (ptr == nint.Zero || actorVfxRemove == null || ttl <= 0)
                return;

            trackedActorVfx.Add(new TrackedActorVfx
            {
                Ptr = ptr,
                Remaining = ttl,
                Path = path,
                OwnerEntityId = ownerEntityId,
            });

            while (trackedActorVfx.Count > MaxTrackedActorVfx)
                RemoveTrackedActorVfxAt(0);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[VFX] actorVfxCreate crashed for '{path}' — skipping");
        }
    }

    private void RemoveTrackedActorVfxAt(int index)
    {
        if (index < 0 || index >= trackedActorVfx.Count)
            return;

        var tracked = trackedActorVfx[index];
        trackedActorVfx.RemoveAt(index);

        if (tracked.Ptr == nint.Zero || actorVfxRemove == null)
            return;

        // World teardown frees the vfx objects with their owners; still untrack (above), but
        // calling the native remove on the stale pointer is an uncatchable AV.
        if (Core.Services.ObjectTable.LocalPlayer == null)
            return;

        try
        {
            actorVfxRemove(tracked.Ptr, (char)0);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[VFX] actorVfxRemove failed for '{tracked.Path}' owner=0x{tracked.OwnerEntityId:X}");
        }
    }

    /// <summary>
    /// Fabricate ActionEffect data and feed it to ActionEffectHandler.Receive().
    /// This is the same code path the game uses when receiving combat results from the server.
    /// </summary>
    private void CallActionEffectReceive(ActionEffectRequest request)
    {
        var targetCount = Math.Min(request.Targets.Count, 32);

        // Resolve caster Character*
        Character* casterPtr = FindCharacter(request.SourceEntityId, request.IsSourcePlayer);
        if (casterPtr == null)
        {
            log.Warning($"ActionEffect: Could not find caster 0x{request.SourceEntityId:X}");
            return;
        }

        // Allocate native memory for the structs
        var headerSize = sizeof(ActionEffectHandler.Header);
        var effectsSize = sizeof(ActionEffectHandler.TargetEffects) * targetCount;
        var idsSize = sizeof(GameObjectId) * targetCount;

        var headerPtr = (ActionEffectHandler.Header*)Marshal.AllocHGlobal(headerSize);
        var effectsPtr = (ActionEffectHandler.TargetEffects*)Marshal.AllocHGlobal(effectsSize);
        var targetIdsPtr = (GameObjectId*)Marshal.AllocHGlobal(idsSize);

        try
        {
            // Zero initialize
            NativeMemory.Clear(headerPtr, (nuint)headerSize);
            NativeMemory.Clear(effectsPtr, (nuint)effectsSize);
            NativeMemory.Clear(targetIdsPtr, (nuint)idsSize);

            // Fill header
            var seq = globalSequence++;
            headerPtr->AnimationTargetId = request.Targets[0].TargetId;
            headerPtr->ActionId = request.ActionId;
            headerPtr->GlobalSequence = seq;
            headerPtr->AnimationLock = request.AnimationLock;
            headerPtr->SourceSequence = 0; // Not client-initiated (prevents the game from expecting a server response)
            headerPtr->RotationInt = QuantizeRotation(request.SourceRotation);
            headerPtr->SpellId = (ushort)(request.ActionId & 0xFFFF);
            headerPtr->ActionType = 1; // Action
            headerPtr->NumTargets = (byte)targetCount;
            headerPtr->ShowInLog = false; // Don't pollute the real action log
            headerPtr->ForceAnimationLock = false; // Don't lock the player

            // Fill per-target effects
            for (int i = 0; i < targetCount; i++)
            {
                var target = request.Targets[i];
                targetIdsPtr[i] = (GameObjectId)target.TargetId;

                // Build damage effect entry
                ref var effect = ref effectsPtr[i].Effects[0];
                if (target.Damage > 0)
                {
                    effect.Type = 3; // Damage
                    effect.Value = (ushort)(target.Damage & 0xFFFF);
                    effect.Param0 = 0;
                    if (target.IsCritical) effect.Param0 |= 0x01;
                    if (target.IsDirectHit) effect.Param0 |= 0x02;
                    // Encode damage type: bits 2-3
                    effect.Param0 |= target.DamageType switch
                    {
                        SimDamageType.Physical => 0,
                        SimDamageType.Magical => (byte)(1 << 2),
                        SimDamageType.Unique => (byte)(2 << 2),
                        _ => 0,
                    };
                    // High bits for large damage values
                    if (target.Damage > 0xFFFF)
                    {
                        effect.Param3 = (byte)((target.Damage >> 16) & 0xFF);
                        effect.Param4 = (byte)((target.Damage >> 24) & 0xFF);
                    }
                }
                else if (target.Healing > 0)
                {
                    effect.Type = 4; // Heal
                    effect.Value = (ushort)(target.Healing & 0xFFFF);
                    if (target.IsCritical) effect.Param0 |= 0x01;
                }
            }

            // Call the game's ActionEffect handler
            var casterPos = request.SourcePosition;
            ActionEffectHandler.Receive(
                request.SourceEntityId,
                casterPtr,
                &casterPos,
                headerPtr,
                effectsPtr,
                targetIdsPtr);

            log.Verbose($"ActionEffectHandler.Receive: caster=0x{request.SourceEntityId:X}, " +
                        $"action={request.ActionId}, targets={targetCount}, seq={seq}");
        }
        finally
        {
            Marshal.FreeHGlobal((nint)headerPtr);
            Marshal.FreeHGlobal((nint)effectsPtr);
            Marshal.FreeHGlobal((nint)targetIdsPtr);
        }
    }

    private void PlayMonsterRangedAttackTimeline(uint sourceEntityId)
    {
        if (monsterRangedAutoAttackTimeline == 0)
        {
            log.Warning("Monster ranged auto-attack timeline is unresolved; cannot play battle/auto_attack_shot1_mon.");
            return;
        }

        var casterPtr = FindCharacter(sourceEntityId, isPlayer: false);
        if (casterPtr == null)
        {
            log.Warning($"Monster ranged auto-attack timeline: caster 0x{sourceEntityId:X} not found.");
            return;
        }

        log.Info($"Playing monster ranged auto-attack timeline {monsterRangedAutoAttackTimeline} for caster 0x{sourceEntityId:X}.");
        emotePlayer.PlayOneShot(casterPtr, monsterRangedAutoAttackTimeline);
    }

    /// <summary>
    /// Put an NPC into "battle ready" visual state: weapon drawn, combat stance.
    /// Sets InCombat, IsHostile, IsWeaponDrawn flags, and switches to combat animation set.
    /// </summary>
    public void SetBattleStance(SimulatedNpc npc)
    {
        try
        {
            if (npc.BattleChara == null) return;

            var character = (Character*)npc.BattleChara;

            // Set combat flags on CharacterData
            character->CharacterData.InCombat = true;
            character->CharacterData.IsHostile = true;

            // Set weapon drawn flag on Timeline
            character->Timeline.IsWeaponDrawn = true;

            // Switch to combat animation set
            character->Timeline.ModelState = 1;

            log.Verbose($"Battle stance set for NPC '{npc.Name}'.");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to set battle stance for {npc.Name}.");
        }
    }

    /// <summary>
    /// Reset NPC visual state to normal (sheathed weapon, idle stance).
    /// </summary>
    public void ClearBattleStance(SimulatedNpc npc)
    {
        try
        {
            if (npc.BattleChara == null) return;

            var character = (Character*)npc.BattleChara;

            character->CharacterData.InCombat = false;
            character->Timeline.IsWeaponDrawn = false;
            character->Timeline.ModelState = 0;

            log.Verbose($"Battle stance cleared for NPC '{npc.Name}'.");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to clear battle stance for {npc.Name}.");
        }
    }

    /// <summary>
    /// Clear the local player's combat visual flags after CombatSimulator-owned combat ends.
    /// This does not touch the active timeline; it only returns idle selection to the normal
    /// sheathed/non-hostile state so animation mods cannot keep resolving combat idle.
    /// </summary>
    public void ClearPlayerBattleStance()
    {
        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null || player.Address == nint.Zero)
                return;

            var character = (Character*)player.Address;
            character->CharacterData.InCombat = false;
            character->CharacterData.IsHostile = false;
            character->Timeline.IsWeaponDrawn = false;
            character->Timeline.ModelState = 0;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to clear player battle stance.");
        }
    }

    public void CapturePlayerCombatVisualState()
    {
        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null || player.Address == nint.Zero)
                return;

            var character = (Character*)player.Address;
            playerSessionWeaponDrawn = character->Timeline.IsWeaponDrawn;
            playerSessionModelState = character->Timeline.ModelState;
            playerSessionInCombat = character->CharacterData.InCombat;
            playerSessionIsHostile = character->CharacterData.IsHostile;
            playerSessionVisualCaptured = true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to capture player combat visual state.");
        }
    }

    public void RestorePlayerCombatVisualState()
    {
        if (!playerSessionVisualCaptured)
            return;

        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null && player.Address != nint.Zero)
            {
                var character = (Character*)player.Address;
                character->Timeline.IsWeaponDrawn = playerSessionWeaponDrawn;
                character->Timeline.ModelState = playerSessionModelState;
                character->CharacterData.InCombat = playerSessionInCombat;
                character->CharacterData.IsHostile = playerSessionIsHostile;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to restore player combat visual state.");
        }
        finally
        {
            playerSessionVisualCaptured = false;
        }
    }

    public void RestorePlayerGuardVisualState()
    {
        if (!guardVisualRestorePending)
            return;

        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null && player.Address != nint.Zero)
            {
                var character = (Character*)player.Address;
                character->Timeline.IsWeaponDrawn = guardSavedWeaponDrawn;
                character->Timeline.ModelState = guardSavedModelState;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to restore player guard visual state.");
        }
        finally
        {
            guardVisualRestorePending = false;
            guardVisualRestoreTimer = 0f;
        }
    }

    /// <summary>
    /// Emergency cleanup for a local player stuck in a CombatSimulator-owned action/death pose.
    /// This is intentionally heavier than ClearPlayerBattleStance and should only be used from
    /// explicit recovery flows.
    /// </summary>
    public void RecoverPlayerAnimationState()
    {
        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null || player.Address == nint.Zero)
                return;

            var character = (Character*)player.Address;
            character->Timeline.OverallSpeed = 1.0f;
            character->Timeline.BaseOverride = 0;
            character->Timeline.IsWeaponDrawn = false;
            character->Timeline.ModelState = 0;
            character->CharacterData.InCombat = false;
            character->CharacterData.IsHostile = false;
            character->Mode = CharacterModes.Normal;
            character->ModeParam = 0;
            emotePlayer.ResetEmote(character);

            // If the current draw/animation instance cached a bad pose, state resets above are
            // not enough. Use the same self-copy redraw trigger used by spawned humanoids to
            // make the client rebuild its draw-data/animation binding without a full restart.
            character->CharacterSetup.CopyFromCharacter(
                character, CharacterSetupContainer.CopyFlags.None);
            character->SetMode(CharacterModes.Normal, 0);
            emotePlayer.ResetEmote(character);

            guardVisualRestorePending = false;
            guardVisualRestoreTimer = 0f;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to recover player animation state.");
        }
    }

    /// <summary>
    /// Play death animation on an NPC using BypassEmote-style timeline (playdead).
    /// Falls back to CharacterModes.Dead if playdead timelines aren't available.
    /// </summary>
    public void PlayDeathAnimation(SimulatedNpc npc)
    {
        try
        {
            if (npc.BattleChara == null)
                return;

            var character = (Character*)npc.BattleChara;

            if (ShouldUseCombatDeath(character) && battleDeadResolved)
            {
                emotePlayer.PlayLoopedEmote(character, battleDeadLoopTimeline, battleDeadIntroTimeline);
                log.Verbose($"Drawn-weapon death triggered for NPC '{npc.Name}'.");
            }
            else if (playDeadResolved)
            {
                // Use BypassEmote-style timeline: plays playdead on any character (no unlock needed)
                emotePlayer.PlayLoopedEmote(character, playDeadLoopTimeline, playDeadIntroTimeline);
                log.Verbose($"Death emote (timeline) triggered for NPC '{npc.Name}'.");
            }
            else
            {
                // Fallback: set dead mode directly
                character->SetMode(CharacterModes.Dead, 0);
                log.Verbose($"Death mode (fallback) triggered for NPC '{npc.Name}'.");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to play death animation for {npc.Name}.");
        }
    }

    /// <summary>
    /// Play death animation on the player character.
    /// Uses the combat death timeline if the player is in combat, including mounted cases
    /// where the mount timeline can hide the drawn-weapon state.
    /// </summary>
    public void PlayPlayerDeath(bool forceCombatDeath = false)
    {
        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null) return;
            var character = (Character*)player.Address;

            PlayDeathAnimationOnActor(character, forceCombatDeath);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to play player death animation.");
        }
    }

    public void PlayDeathAnimationOnActor(Character* character, bool forceCombatDeath = false)
    {
        if (character == null) return;

        try
        {
            character->Timeline.OverallSpeed = 1.0f;
            if ((forceCombatDeath || ShouldUseCombatDeath(character)) && battleDeadResolved)
            {
                emotePlayer.PlayLoopedEmote(character, battleDeadLoopTimeline, battleDeadIntroTimeline);
                log.Info($"Death timeline triggered (intro={battleDeadIntroTimeline}, loop={battleDeadLoopTimeline}).");
            }
            else if (playDeadResolved)
            {
                emotePlayer.PlayLoopedEmote(character, playDeadLoopTimeline, playDeadIntroTimeline);
                log.Info("Death emote timeline triggered.");
            }
            else
            {
                character->SetMode(CharacterModes.Dead, 0);
                log.Info("Death mode fallback triggered.");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to play death animation on actor.");
        }
    }

    private static bool ShouldUseCombatDeath(Character* character)
        => character->CharacterData.InCombat || character->Timeline.IsWeaponDrawn || character->Timeline.ModelState == 1;

    /// <summary>
    /// Reset death animation on an NPC character (clear timeline override).
    /// </summary>
    public void ResetDeathAnimation(FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara* battleChara)
    {
        if (battleChara == null) return;

        // World teardown (logout/shutdown) frees every actor before our cleanup gets to run;
        // a pointer null-check cannot see that, and the native AV that follows is uncatchable
        // by the try below — it produced a crash dump from exactly this path (ResetEmote on a
        // freed Character during HandleLoggedOut).
        if (Core.Services.ObjectTable.LocalPlayer == null) return;

        try
        {
            var character = (Character*)battleChara;
            if (playDeadResolved)
                emotePlayer.ResetEmote(character);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to reset death animation.");
        }
    }

    /// <summary>
    /// Reset death animation on the local player (clear timeline override).
    /// </summary>
    public void ResetPlayerDeathAnimation()
    {
        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null) return;

            var character = (Character*)player.Address;
            emotePlayer.ResetEmote(character);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to reset player death animation.");
        }
    }

    /// <summary>
    /// Execute victory animation.
    /// </summary>
    public void PlayVictory(bool isPlayerVictory, IReadOnlyList<SimulatedNpc>? npcs = null)
    {
    }

    /// <summary>
    /// Play NPC auto-attack with full animation + VFX pipeline.
    /// </summary>
    public void PlayNpcAutoAttack(SimulatedNpc npc, ulong targetId, int damage)
    {
        PlayActionEffect(new ActionEffectRequest
        {
            SourceEntityId = npc.SimulatedEntityId,
            SourcePosition = GetNpcPosition(npc),
            ActionId = 7, // Auto-attack
            AnimationLock = 0.6f,
            SourceRotation = GetNpcRotation(npc),
            IsSourcePlayer = false,
            IsRanged = false,
            Targets =
            {
                new TargetEffect
                {
                    TargetId = targetId,
                    Damage = damage,
                    DamageType = SimDamageType.Physical,
                }
            }
        });
    }

    /// <summary>
    /// Play an emote on an NPC character (looped: intro then BaseOverride loop).
    /// </summary>
    public void PlayNpcEmote(SimulatedNpc npc, ushort loopTimeline, ushort introTimeline)
    {
        if (npc.BattleChara == null) return;
        var character = (Character*)npc.BattleChara;
        EmotePlayer.PlayLoopedEmote(character, loopTimeline, introTimeline);
    }

    /// <summary>
    /// Play the NPC's melee attack animation via ActionEffectHandler with NumTargets=0.
    /// The NPC faces and animates toward the player but no effect hits any entity,
    /// so the player's death expression is not disturbed.
    /// </summary>
    public void PlayNpcMeleeAnimationOnly(SimulatedNpc npc)
    {
        var casterPtr = FindCharacter(npc.SimulatedEntityId, isPlayer: false);
        if (casterPtr == null) return;

        var headerSize   = sizeof(ActionEffectHandler.Header);
        var effectsSize  = sizeof(ActionEffectHandler.TargetEffects);
        var idsSize      = sizeof(GameObjectId);

        var headerPtr    = (ActionEffectHandler.Header*)Marshal.AllocHGlobal(headerSize);
        var effectsPtr   = (ActionEffectHandler.TargetEffects*)Marshal.AllocHGlobal(effectsSize);
        var targetIdsPtr = (GameObjectId*)Marshal.AllocHGlobal(idsSize);

        try
        {
            NativeMemory.Clear(headerPtr,    (nuint)headerSize);
            NativeMemory.Clear(effectsPtr,   (nuint)effectsSize);
            NativeMemory.Clear(targetIdsPtr, (nuint)idsSize);

            // Use FFXIV's null-entity sentinel so no entity is targeted.
            // NPC still plays the attack animation (driven by ActionId/AnimationLock/RotationInt)
            // but nothing receives a hit state, preserving the player's death expression.
            headerPtr->AnimationTargetId  = 0xE0000000UL;
            headerPtr->ActionId           = 7;
            headerPtr->GlobalSequence     = globalSequence++;
            headerPtr->AnimationLock      = 0.6f;
            headerPtr->SourceSequence     = 0;
            headerPtr->RotationInt        = QuantizeRotation(GetNpcRotation(npc));
            headerPtr->SpellId            = 7;
            headerPtr->ActionType         = 1;
            headerPtr->NumTargets         = 0;
            headerPtr->ShowInLog          = false;
            headerPtr->ForceAnimationLock = false;

            var casterPos = GetNpcPosition(npc);
            ActionEffectHandler.Receive(
                npc.SimulatedEntityId, casterPtr, &casterPos,
                headerPtr, effectsPtr, targetIdsPtr);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)headerPtr);
            Marshal.FreeHGlobal((nint)effectsPtr);
            Marshal.FreeHGlobal((nint)targetIdsPtr);
        }
    }

    /// <summary>
    /// Action Mode: play a melee swing on the local player with no target — whiff
    /// feedback so a light attack that connects with nothing still animates and the
    /// button never feels dead. Hits play the full weaponskill animation through the
    /// ActionEffect pipeline instead.
    /// </summary>
    /// <summary>
    /// Action Mode wind-up body language: play a non-hit preparatory battle timeline directly
    /// on the NPC. This avoids using ActionEffect for the windup, so the real strike can still
    /// use the full hit-feedback pipeline at resolve time without creating a duplicate attack.
    /// </summary>
    public bool PlayNpcWindupPose(SimulatedNpc npc, uint actionId)
    {
        var casterPtr = FindCharacter(npc.SimulatedEntityId, isPlayer: false);
        if (casterPtr == null) return false;

        var headerSize   = sizeof(ActionEffectHandler.Header);
        var effectsSize  = sizeof(ActionEffectHandler.TargetEffects);
        var idsSize      = sizeof(GameObjectId);

        var headerPtr    = (ActionEffectHandler.Header*)Marshal.AllocHGlobal(headerSize);
        var effectsPtr   = (ActionEffectHandler.TargetEffects*)Marshal.AllocHGlobal(effectsSize);
        var targetIdsPtr = (GameObjectId*)Marshal.AllocHGlobal(idsSize);

        try
        {
            NativeMemory.Clear(headerPtr,    (nuint)headerSize);
            NativeMemory.Clear(effectsPtr,   (nuint)effectsSize);
            NativeMemory.Clear(targetIdsPtr, (nuint)idsSize);

            var aid = actionId == 0 ? 7u : actionId;
            headerPtr->AnimationTargetId  = 0xE0000000UL;
            headerPtr->ActionId           = aid;
            headerPtr->GlobalSequence     = globalSequence++;
            headerPtr->AnimationLock      = 0.6f;
            headerPtr->SourceSequence     = 0;
            headerPtr->RotationInt        = QuantizeRotation(GetNpcRotation(npc));
            headerPtr->SpellId            = (ushort)(aid & 0xFFFF);
            headerPtr->ActionType         = 1;
            headerPtr->NumTargets         = 0;
            headerPtr->ShowInLog          = false;
            headerPtr->ForceAnimationLock = false;

            var casterPos = GetNpcPosition(npc);
            ActionEffectHandler.Receive(
                npc.SimulatedEntityId, casterPtr, &casterPos,
                headerPtr, effectsPtr, targetIdsPtr);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"Failed to play NPC wind-up action effect for {npc.Name}.");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal((nint)headerPtr);
            Marshal.FreeHGlobal((nint)effectsPtr);
            Marshal.FreeHGlobal((nint)targetIdsPtr);
        }
    }

    public void PlayPlayerMeleeSwing()
    {
        if (npcMeleeAutoAttackTimeline == 0) return;
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;
        EmotePlayer.PlayOneShot((Character*)player.Address, npcMeleeAutoAttackTimeline, 0);
    }

    /// <summary>
    /// Action Mode: play the player's own action animation as a whiff (NumTargets=0)
    /// through the same ActionEffectHandler pipeline that hits use — so an out-of-range
    /// or no-target light attack still visibly swings and the key never feels dead. Uses
    /// the pressed actionId's real weaponskill animation (far more reliable than the
    /// emote-timeline path in <see cref="PlayPlayerMeleeSwing"/>).
    /// </summary>
    public void PlayPlayerActionAnimationOnly(uint actionId)
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero) return;
        var casterPtr = (Character*)player.Address;

        var headerSize   = sizeof(ActionEffectHandler.Header);
        var effectsSize  = sizeof(ActionEffectHandler.TargetEffects);
        var idsSize      = sizeof(GameObjectId);

        var headerPtr    = (ActionEffectHandler.Header*)Marshal.AllocHGlobal(headerSize);
        var effectsPtr   = (ActionEffectHandler.TargetEffects*)Marshal.AllocHGlobal(effectsSize);
        var targetIdsPtr = (GameObjectId*)Marshal.AllocHGlobal(idsSize);

        try
        {
            NativeMemory.Clear(headerPtr,    (nuint)headerSize);
            NativeMemory.Clear(effectsPtr,   (nuint)effectsSize);
            NativeMemory.Clear(targetIdsPtr, (nuint)idsSize);

            var aid = actionId == 0 ? 7u : actionId;
            headerPtr->AnimationTargetId  = 0xE0000000UL;
            headerPtr->ActionId           = aid;
            headerPtr->GlobalSequence     = globalSequence++;
            headerPtr->AnimationLock      = 0.6f;
            headerPtr->SourceSequence     = 0;
            headerPtr->RotationInt        = QuantizeRotation(player.Rotation);
            headerPtr->SpellId            = (ushort)aid;
            headerPtr->ActionType         = 1;
            headerPtr->NumTargets         = 0;
            headerPtr->ShowInLog          = false;
            headerPtr->ForceAnimationLock = false;

            var casterPos = player.Position;
            ActionEffectHandler.Receive(
                player.EntityId, casterPtr, &casterPos,
                headerPtr, effectsPtr, targetIdsPtr);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)headerPtr);
            Marshal.FreeHGlobal((nint)effectsPtr);
            Marshal.FreeHGlobal((nint)targetIdsPtr);
        }
    }

    /// <summary>
    /// Spawn a hit VFX on the player character (independent of the action pipeline).
    /// Called when the player takes damage from NPC attacks.
    /// </summary>
    public void SpawnHitVfxOnPlayer()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player != null)
            SpawnHitVfxOnActor(player.Address);
    }

    /// <summary>
    /// Spawn the configured hit-impact VFX on any actor (player, companion, or NPC). Used as the
    /// Action-Mode hit reaction instead of a flinch timeline.
    /// </summary>
    public void SpawnHitVfxOnActor(nint actorAddress)
    {
        if (actorVfxCreate == null || actorAddress == nint.Zero) return;

        try
        {
            var vfxPath = config.HitVfxPath;
            if (string.IsNullOrWhiteSpace(vfxPath)) return;

            actorVfxCreate(vfxPath, actorAddress, actorAddress, -1, (char)0, 0, (char)0);
            log.Verbose($"Hit VFX spawned on actor 0x{actorAddress:X}: {vfxPath}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to spawn hit VFX on actor.");
        }
    }

    public void PlayPlayerGuardAnimation()
    {
        try
        {
            // Open the guard-sound window so the PlaySpecificSound hook mutes the crisp press ting.
            guardSoundWindowUntilTicks = Environment.TickCount64 + 1000;
            allowGuardTingOnce = false;

            PlayGuardTimeline();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to play player guard animation.");
        }
    }

    public void PlayPlayerGuardSuccess()
    {
        // Direct replay of the suppressed ting via the play function doesn't work (the game needs a
        // caller-side finalize step). Instead, let the next ting through and re-trigger the guard
        // timeline so the game itself emits the genuine ting as success feedback. The pose restarts
        // from frame 0 — negligible even for a preemptive guard (~200ms).
        allowGuardTingOnce = true;
        PlayGuardTimeline();

        SpawnConfiguredVfxOnPlayer(config.GuardSuccessVfxPath, ttl: 0.35f);
    }

    private void PlayGuardTimeline()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return;

        var character = (Character*)player.Address;
        var timeline = config.GuardTimelineId != 0 ? config.GuardTimelineId : guardTimeline;
        if (!guardVisualRestorePending)
        {
            guardSavedWeaponDrawn = character->Timeline.IsWeaponDrawn;
            guardSavedModelState = character->Timeline.ModelState;
            guardVisualRestorePending = true;
        }

        guardVisualRestoreTimer = MathF.Max(0.25f, config.GuardActiveWindow + config.GuardRecovery);
        character->Timeline.IsWeaponDrawn = true;
        character->Timeline.ModelState = 1;
        character->Timeline.PlayActionTimeline(timeline != 0 ? timeline : (ushort)78);
    }

    private void TickGuardVisualRestore(float deltaTime)
    {
        if (!guardVisualRestorePending)
            return;

        guardVisualRestoreTimer = MathF.Max(0f, guardVisualRestoreTimer - deltaTime);
        if (guardVisualRestoreTimer <= 0f)
            RestorePlayerGuardVisualState();
    }

    public void PlayEnemyTelegraphWarning(SimulatedNpc npc)
    {
        if (npc.Address == nint.Zero)
            return;

        SpawnConfiguredVfx(npc.Address, npc.Address, config.EnemyTelegraphVfxPath, ttl: 0.45f, npc.SimulatedEntityId);
    }

    private void SpawnConfiguredVfxOnPlayer(string path, float ttl)
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null || player.Address == nint.Zero)
            return;

        SpawnConfiguredVfx(player.Address, player.Address, path, ttl, player.EntityId);
    }

    private void SpawnConfiguredVfx(nint attachTo, nint orientTo, string path, float ttl, uint ownerEntityId)
    {
        if (actorVfxCreate == null || string.IsNullOrWhiteSpace(path))
            return;

        SpawnAndTrack(path, attachTo, orientTo, ownerEntityId, ttl);
    }

    private Character* FindCharacter(uint entityId, bool isPlayer)
    {
        if (isPlayer)
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player != null)
                return (Character*)player.Address;
        }
        else
        {
            foreach (var obj in Core.Services.ObjectTable)
            {
                if (obj.EntityId == entityId)
                    return (Character*)obj.Address;
            }
        }
        return null;
    }

    private Vector3 GetNpcPosition(SimulatedNpc npc)
    {
        if (npc.BattleChara != null)
        {
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
            return go->Position;
        }
        return Vector3.Zero;
    }

    private float GetNpcRotation(SimulatedNpc npc)
    {
        if (npc.BattleChara != null)
        {
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.BattleChara;
            return go->Rotation;
        }
        return 0;
    }

    private static ushort QuantizeRotation(float radians)
    {
        // Game maps: 0 -> -pi, 65535 -> pi
        float normalized = (radians + MathF.PI) / (2f * MathF.PI);
        normalized = ((normalized % 1f) + 1f) % 1f; // Wrap to [0, 1)
        return (ushort)(normalized * 65535f);
    }

    public void Dispose()
    {
        RemoveAllActiveVfx();
        try
        {
            actorVfxDtorHook?.Disable();
            actorVfxDtorHook?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Error disposing ActorVfxDtor hook.");
        }
        actorVfxDtorHook = null;

        try
        {
            playSpecificSoundHook?.Disable();
            playSpecificSoundHook?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Error disposing PlaySpecificSound hook.");
        }
        playSpecificSoundHook = null;

        // Free the silent-sound buffers only after the hook is gone so the detour can't use them.
        if (noSoundInfoPtr != 0)
        {
            Marshal.FreeHGlobal(noSoundInfoPtr);
            noSoundInfoPtr = 0;
        }
        if (noSoundScdPtr != 0)
        {
            Marshal.FreeHGlobal(noSoundScdPtr);
            noSoundScdPtr = 0;
        }
    }
}
