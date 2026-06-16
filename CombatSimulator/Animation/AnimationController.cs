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
    private ushort monsterRangedAutoAttackTimeline;
    private ushort npcMeleeAutoAttackTimeline;

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
    private readonly List<TrackedActorVfx> trackedActorVfx = new();
    private const float CastVfxTtl = 1.5f;
    private const float StartVfxTtl = 1.5f;
    private const float CasterTimelineVfxTtl = 3.0f;
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
        "vfx/common/eff/dk05th_stdn0t.avfx",
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
        this.emotePlayer = new EmoteTimelinePlayer(log);

        ResolvePlayDeadTimelines(dataManager);
        ResolveBattleDeadTimeline(dataManager);
        ResolveMonsterRangedAttackTimeline(dataManager);
        ResolveMeleeAutoAttackTimeline(dataManager);
        ResolveActorVfxCreate(sigScanner);
        ResolveActorVfxRemove(sigScanner);
        ResolveActorVfxDtor(gameInterop, sigScanner);

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
        if (trackedActorVfx.Count == 0)
            return;

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
                if (!string.IsNullOrEmpty(request.CastVfxPath))
                    SpawnAndTrack(request.CastVfxPath, casterAddr, orientAddr, casterEntityId, CastVfxTtl);

                if (!string.IsNullOrEmpty(request.StartVfxPath))
                    SpawnAndTrack(request.StartVfxPath, casterAddr, orientAddr, casterEntityId, StartVfxTtl);

                foreach (var path in request.CasterVfxPaths)
                    SpawnAndTrack(path, casterAddr, orientAddr, casterEntityId, CasterTimelineVfxTtl);
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

                    if (request.TargetVfxPaths.Count > 0)
                    {
                        foreach (var path in request.TargetVfxPaths)
                            SpawnAndTrack(path, targetAddr, casterAddr, targetEntityId, UntrackedVfxTtl);
                    }

                    if ((request.TargetVfxPaths.Count == 0 || request.IsSourcePlayer) && config.EnableHitVfx)
                    {
                        var vfxPath = config.HitVfxPath;
                        if (!string.IsNullOrWhiteSpace(vfxPath))
                            SpawnAndTrack(vfxPath, targetAddr, casterAddr, targetEntityId, UntrackedVfxTtl);
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

            if ((forceCombatDeath || ShouldUseCombatDeath(character)) && battleDeadResolved)
            {
                emotePlayer.PlayLoopedEmote(character, battleDeadLoopTimeline, battleDeadIntroTimeline);
                log.Info($"Player drawn-weapon death triggered (intro={battleDeadIntroTimeline}, loop={battleDeadLoopTimeline}).");
            }
            else if (playDeadResolved)
            {
                emotePlayer.PlayLoopedEmote(character, playDeadLoopTimeline, playDeadIntroTimeline);
                log.Info("Player death emote (timeline) triggered.");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to play player death animation.");
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
    /// Spawn a hit VFX on the player character (independent of the action pipeline).
    /// Called when the player takes damage from NPC attacks.
    /// </summary>
    public void SpawnHitVfxOnPlayer()
    {
        if (actorVfxCreate == null) return;

        try
        {
            var player = Core.Services.ObjectTable.LocalPlayer;
            if (player == null) return;

            var vfxPath = config.HitVfxPath;
            if (string.IsNullOrWhiteSpace(vfxPath)) return;

            var playerAddr = player.Address;
            actorVfxCreate(vfxPath, playerAddr, playerAddr, -1, (char)0, 0, (char)0);
            log.Verbose($"Hit VFX spawned on player: {vfxPath}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to spawn hit VFX on player.");
        }
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
    }
}
