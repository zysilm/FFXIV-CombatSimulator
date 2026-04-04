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

// ActorVfxCreate — spawns a .avfx on an actor (same function VFXEditor / Brio use)
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
    private readonly ChatCommandExecutor commandExecutor;
    private readonly EmoteTimelinePlayer emotePlayer;
    public EmoteTimelinePlayer EmotePlayer => emotePlayer;
    private readonly Configuration config;

    // Monotonically increasing sequence number for fabricated ActionEffects
    private uint globalSequence = 0x10000000;

    // Cached "Play Dead" emote timeline IDs (resolved at init)
    private ushort playDeadLoopTimeline;
    private ushort playDeadIntroTimeline;
    private bool playDeadResolved;

    // Battle dead ActionTimeline IDs — keeps weapons drawn (no sheathing).
    // Known IDs: battle/dead = 8935 (falling), battle/dead_pose = 8936 (settled).
    private ushort battleDeadIntroTimeline = 8935;
    private ushort battleDeadLoopTimeline = 8936;
    private bool battleDeadResolved;

    // ActorVfxCreate — spawns a .avfx particle effect attached to an actor
    private delegate nint ActorVfxCreateDelegate(
        string path, nint a2, nint a3, float a4, char a5, ushort a6, char a7);

    private ActorVfxCreateDelegate? actorVfxCreate;
    private nint actorVfxCreateAddr;
    private Hook<ActorVfxCreateDelegate>? actorVfxCreateHook;

    // ActorVfxRemove — removes a spawned VFX by pointer (same function VFXEditor uses)
    private delegate nint ActorVfxRemoveDelegate(nint vfx, char a2);

    private ActorVfxRemoveDelegate? actorVfxRemove;
    private nint actorVfxRemoveAddr;

    /// <summary>Resolved address of the native ActorVfxCreate function (0 if unresolved).</summary>
    public nint ActorVfxCreateAddress => actorVfxCreateAddr;
    /// <summary>Resolved address of the native ActorVfxRemove function (0 if unresolved).</summary>
    public nint ActorVfxRemoveAddress => actorVfxRemoveAddr;

    // GCD-based VFX tracking: hook captures actorVfxCreate pointers during the GCD
    // window after each player action, then removes them when the window expires.
    private const float VfxCleanupDelay = 2.5f;
    private float vfxTrackingTimer; // counts down from VfxCleanupDelay, 0 = not tracking
    private readonly List<TrackedVfx> trackedVfxList = new();

    private struct TrackedVfx
    {
        public nint Pointer;
        public float TimeRemaining;
    }

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
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop,
        ChatCommandExecutor commandExecutor,
        Configuration config)
    {
        this.log = log;
        this.clientState = clientState;
        this.commandExecutor = commandExecutor;
        this.config = config;
        this.emotePlayer = new EmoteTimelinePlayer(log);

        ResolvePlayDeadTimelines(dataManager);
        ResolveBattleDeadTimeline(dataManager);
        ResolveActorVfxCreate(sigScanner, gameInterop);
        ResolveActorVfxRemove(sigScanner);

        log.Info("AnimationController: Initialized with ActionEffectHandler + emote timeline system.");
    }

    private void ResolveActorVfxCreate(ISigScanner sigScanner, IGameInteropProvider gameInterop)
    {
        try
        {
            var addr = sigScanner.ScanText(
                "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8");
            actorVfxCreateAddr = addr;
            actorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(addr);

            // Hook to capture VFX pointers for GCD-based cleanup
            actorVfxCreateHook = gameInterop.HookFromAddress<ActorVfxCreateDelegate>(addr, ActorVfxCreateDetour);
            actorVfxCreateHook.Enable();
            log.Info($"AnimationController: ActorVfxCreate resolved + hooked at 0x{addr:X}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "AnimationController: Could not resolve ActorVfxCreate — hit VFX will be unavailable.");
        }
    }

    private nint ActorVfxCreateDetour(string path, nint a2, nint a3, float a4, char a5, ushort a6, char a7)
    {
        var result = actorVfxCreateHook!.Original(path, a2, a3, a4, a5, a6, a7);

        // During the GCD window after a player action, capture ALL created VFX pointers
        if (vfxTrackingTimer > 0 && result != nint.Zero)
        {
            trackedVfxList.Add(new TrackedVfx
            {
                Pointer = result,
                TimeRemaining = vfxTrackingTimer, // remove when the GCD window expires
            });
        }

        return result;
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
            log.Warning(ex, "AnimationController: Could not resolve ActorVfxRemove — VFX cleanup unavailable.");
        }
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

            // If user specified a custom emote ID, use that
            uint targetEmoteId = config.DeathEmoteId;

            if (targetEmoteId > 0)
            {
                var emote = emoteSheet.GetRow(targetEmoteId);
                playDeadLoopTimeline = (ushort)emote.ActionTimeline[0].RowId;
                playDeadIntroTimeline = (ushort)emote.ActionTimeline[1].RowId;
                playDeadResolved = playDeadLoopTimeline != 0 || playDeadIntroTimeline != 0;
                log.Info($"AnimationController: Custom death emote {targetEmoteId} → loop={playDeadLoopTimeline}, intro={playDeadIntroTimeline}");
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
                    log.Info($"AnimationController: Resolved 'Play Dead' emote (id={emote.RowId}) → loop={playDeadLoopTimeline}, intro={playDeadIntroTimeline}");
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
        log.Info($"AnimationController: Battle dead timelines — intro={battleDeadIntroTimeline}, loop={battleDeadLoopTimeline}, resolved={battleDeadResolved} (key search: intro={foundIntro}, loop={foundLoop})");
    }

    public void Tick(float deltaTime)
    {
        commandExecutor.Tick(deltaTime);

        // Decrement the GCD tracking window
        if (vfxTrackingTimer > 0)
            vfxTrackingTimer = MathF.Max(0, vfxTrackingTimer - deltaTime);

        // Remove expired tracked VFX
        TickTrackedVfx(deltaTime);
    }

    private void TickTrackedVfx(float deltaTime)
    {
        if (trackedVfxList.Count == 0 || actorVfxRemove == null) return;

        for (int i = trackedVfxList.Count - 1; i >= 0; i--)
        {
            var vfx = trackedVfxList[i];
            vfx.TimeRemaining -= deltaTime;
            trackedVfxList[i] = vfx;

            if (vfx.TimeRemaining <= 0)
            {
                try { actorVfxRemove(vfx.Pointer, (char)1); }
                catch { }
                trackedVfxList.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// No-op — VFX removal via actorVfxRemove is disabled because the same plugins
    /// that hook actorVfxCreate also hook actorVfxRemove and crash on our NPC actors.
    /// VFX expire naturally via their built-in .avfx durations.
    /// </summary>
    public void RemoveAllActiveVfx()
    {
        vfxTrackingTimer = 0;
        if (actorVfxRemove != null)
        {
            foreach (var vfx in trackedVfxList)
            {
                try { actorVfxRemove(vfx.Pointer, (char)1); }
                catch { }
            }
        }
        trackedVfxList.Clear();
    }

    /// <summary>
    /// Play attack animation + VFX + hit reaction via ActionEffectHandler.Receive().
    /// This triggers the game's full combat visual pipeline: caster animation, target hit
    /// reaction, VFX particles, damage flytext, and sound effects — all in one call.
    /// If custom commands are configured for the player, those are used instead.
    /// </summary>
    public void PlayActionEffect(ActionEffectRequest request)
    {
        if (request.Targets.Count == 0)
            return;

        try
        {
            // Check for custom command override (player only)
            if (request.IsSourcePlayer)
            {
                var customCommand = request.IsRanged
                    ? config.PlayerRangedAttackCommand
                    : config.PlayerMeleeAttackCommand;

                if (!string.IsNullOrWhiteSpace(customCommand))
                {
                    // Use custom chat command instead of ActionEffect pipeline
                    commandExecutor.ExecuteCommand(customCommand, cooldown: 0.8f);
                    log.Verbose($"Custom attack command: {customCommand}");
                    return;
                }
            }

            // Spawn skill VFX via ActorVfxCreate (off by default — other plugins that
            // hook this function may crash when accessing our modified NPC actors)
            if (config.EnableSkillVfx)
                SpawnActionVfx(request);

            // Use ActionEffectHandler.Receive() for flytext + damage numbers
            CallActionEffectReceive(request);

            // Start GCD tracking window — the hook captures all VFX created
            // by the animation system over the next 2.5s, then removes them
            if (request.IsSourcePlayer)
                vfxTrackingTimer = VfxCleanupDelay;
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

            if (!string.IsNullOrEmpty(request.CastVfxPath))
                SpawnAndTrack(request.CastVfxPath, casterAddr, orientAddr, casterEntityId);

            if (!string.IsNullOrEmpty(request.StartVfxPath))
                SpawnAndTrack(request.StartVfxPath, casterAddr, orientAddr, casterEntityId);

            foreach (var path in request.CasterVfxPaths)
                SpawnAndTrack(path, casterAddr, orientAddr, casterEntityId);

            // Spawn target VFX (hit/impact effects from ActionTimelineHit TMB)
            foreach (var target in request.Targets)
            {
                if (target.Damage <= 0 && target.Healing <= 0) continue;

                // Fresh resolve for each target
                var (targetAddr, targetEntityId) = ResolveActorAddress((uint)target.TargetId, false);
                if (targetAddr == 0) continue;

                if (request.TargetVfxPaths.Count > 0)
                {
                    foreach (var path in request.TargetVfxPaths)
                        SpawnAndTrack(path, targetAddr, casterAddr, targetEntityId);
                }
                else if (config.EnableHitVfx)
                {
                    var vfxPath = config.HitVfxPath;
                    if (!string.IsNullOrWhiteSpace(vfxPath))
                        SpawnAndTrack(vfxPath, targetAddr, casterAddr, targetEntityId);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to spawn action VFX.");
        }
    }

    /// <summary>
    /// Resolve an entity ID to its current address via managed Dalamud APIs.
    /// Returns (0, 0) if the entity is no longer present.
    /// </summary>
    private (nint address, uint entityId) ResolveActorAddress(uint entityId, bool isPlayer)
    {
        if (isPlayer)
        {
            var player = clientState.LocalPlayer;
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
    /// Spawn a VFX via ActorVfxCreate. The VFX is NOT tracked for manual removal —
    /// actorVfxRemove is hooked by the same plugins that hook actorVfxCreate and
    /// crashes on our NPC actors. VFX expire naturally via their built-in .avfx duration.
    /// </summary>
    private void SpawnAndTrack(string path, nint attachTo, nint orientTo, uint ownerEntityId)
    {
        try
        {
            actorVfxCreate!(path, attachTo, orientTo, -1, (char)0, 0, (char)0);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[VFX] actorVfxCreate crashed for '{path}' — skipping");
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

    /// <summary>
    /// Put an NPC into "battle ready" visual state — weapon drawn, combat stance.
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

            if (playDeadResolved)
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
    /// If a custom command is configured, uses that. Otherwise uses BypassEmote-style timeline.
    /// When ragdoll weapon drop is enabled, uses battle/dead ActionTimeline via BaseOverride
    /// instead of the play-dead emote — play-dead sheathes weapons and the game engine
    /// actively fights any attempt to keep them visible.
    /// </summary>
    public void PlayPlayerDeath()
    {
        try
        {
            // If a custom command is set, use it
            var command = config.PlayerDeathCommand;
            if (!string.IsNullOrWhiteSpace(command))
            {
                commandExecutor.ExecuteCommand(command);
                log.Info($"Player death command executed: {command}");
                return;
            }

            var player = clientState.LocalPlayer;
            if (player == null) return;
            var character = (Character*)player.Address;

            // When weapon drop is enabled, use battle/dead ActionTimeline instead of
            // play-dead emote. Battle death keeps weapons drawn (no sheathing).
            if (config.RagdollWeaponDrop && battleDeadResolved)
            {
                emotePlayer.PlayLoopedEmote(character, battleDeadLoopTimeline, battleDeadIntroTimeline);
                log.Info($"Player death via battle/dead (intro={battleDeadIntroTimeline}, loop={battleDeadLoopTimeline}).");
                return;
            }

            // Default: play-dead emote (may sheath weapons)
            if (playDeadResolved)
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
            var player = clientState.LocalPlayer;
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
    /// Execute victory animation/command.
    /// </summary>
    public void PlayVictory(bool isPlayerVictory, IReadOnlyList<SimulatedNpc>? npcs = null)
    {
        if (isPlayerVictory)
        {
            var command = config.PlayerVictoryCommand;
            if (!string.IsNullOrWhiteSpace(command))
            {
                commandExecutor.ExecuteCommand(command);
                log.Info($"Player victory command executed: {command}");
            }
        }
        else
        {
            // Play emote on each surviving NPC via timeline (bypasses unlock checks)
            var emoteId = config.TargetVictoryEmoteId;
            if (emoteId > 0 && npcs != null)
            {
                try
                {
                    // Get player's object ID so the emote targets the player (facing, height adjust)
                    ulong playerObjId = 0;
                    var player = clientState.LocalPlayer;
                    if (player != null)
                    {
                        var playerObj = (GameObject*)player.Address;
                        playerObjId = playerObj->GetGameObjectId().Id;
                    }

                    var emoteSheet = Core.Services.DataManager.GetExcelSheet<Emote>();
                    if (emoteSheet != null)
                    {
                        var emote = emoteSheet.GetRow(emoteId);
                        var loopTimeline = (ushort)emote.ActionTimeline[0].RowId;
                        var introTimeline = (ushort)emote.ActionTimeline[1].RowId;

                        foreach (var npc in npcs)
                        {
                            if (npc.BattleChara == null || !npc.State.IsAlive) continue;
                            var character = (Character*)npc.BattleChara;
                            if (introTimeline != 0 || loopTimeline != 0)
                                emotePlayer.PlayLoopedEmote(character, loopTimeline, introTimeline, playerObjId);
                            log.Info($"NPC '{npc.Name}' playing victory emote {emoteId} toward player 0x{playerObjId:X}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Warning(ex, $"Failed to play target victory emote {emoteId}");
                }
            }
        }
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
    /// Spawn a hit VFX on the player character (independent of the action pipeline).
    /// Called when the player takes damage from NPC attacks.
    /// </summary>
    public void SpawnHitVfxOnPlayer()
    {
        if (actorVfxCreate == null) return;

        try
        {
            var player = clientState.LocalPlayer;
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
            var player = clientState.LocalPlayer;
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
        actorVfxCreateHook?.Dispose();
    }
}
