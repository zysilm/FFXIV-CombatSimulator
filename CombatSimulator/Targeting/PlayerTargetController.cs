using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Targeting;

/// <summary>
/// Custom in-simulation target lock system. During an active simulation it takes
/// over the game's targeting keybinds (confirm / cancel / cycle) so the player
/// drives our own locked-target state instead of the native target system.
///
/// Design:
///  - Confirm (OK): when no target is locked, acquire one — prefer an enemy that
///    is currently attacking the player, otherwise the nearest enemy.
///  - Cancel (CANCEL): release the lock (back to no target).
///  - Next / Prev: cycle among living enemies (ordered left→right on screen) while
///    a target is locked.
///  - No locked target ⇒ the player performs no attacks (gated in UseActionHook
///    and CombatEngine.TickAutoAttack).
///
/// The system reads logical InputCodes via InputManager.GetInputStatus, which the
/// game has already resolved against the user's keybinds (keyboard or gamepad), so
/// it automatically respects whatever the player has bound. Native targeting is
/// suppressed by hooking TargetSystem.HandleTargetingKeybinds and skipping the
/// original while we are in control — that function is the field-targeting input
/// pump, so UI confirm/cancel (handled on a different path) is unaffected.
/// </summary>
public sealed unsafe class PlayerTargetController : IDisposable
{
    private readonly CombatEngine combatEngine;
    private readonly NpcSelector npcSelector;
    private readonly Configuration config;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private delegate void HandleTargetingKeybindsDelegate(TargetSystem* thisPtr);
    private Hook<HandleTargetingKeybindsDelegate>? keybindHook;

    // The two flag params are declared as byte (not bool) to sidestep 1-byte-vs-4-byte
    // bool marshalling ambiguity; we never read them, only pass them straight through.
    private delegate bool SetHardTargetDelegate(TargetSystem* thisPtr, GameObject* obj, byte ignoreTargetModes, byte a4, int a5);
    private Hook<SetHardTargetDelegate>? hardTargetHook;

    // Client-only simulated actors (spawned NPCs, companions, sensed map enemies we
    // re-id) are assigned entity ids at/above this floor. The game's HARD target is
    // synced to the server, so such an id must never become the native hard target — see
    // SetHardTargetDetour. Mirror of NpcSelector.SimulatedEntityIdFloor.
    private const uint SimulatedEntityIdFloor = 0xF0000000;

    private SimulatedNpc? lockedTarget;

    // Auto-counter state machine: armed by default; pressing cancel suppresses it
    // (no auto-lock even when hit), confirm-locking re-arms it. Reset to armed when
    // the controller goes inactive (combat stop/reset).
    private bool autoCounterSuppressed;
    // Attacker queued by the combat engine's "player was hit" callback (set on the
    // engine tick, consumed on the next targeting tick — see Tick/TryAutoCounter).
    private uint pendingAutoCounterAttackerId;

    // Rising-edge detection state for the polled input codes.
    private bool prevOk, prevCancel, prevNext, prevPrev, prevLeft, prevRight;

    public PlayerTargetController(
        IGameInteropProvider gameInterop,
        CombatEngine combatEngine,
        NpcSelector npcSelector,
        Configuration config,
        IObjectTable objectTable,
        IGameGui gameGui,
        IPluginLog log)
    {
        this.combatEngine = combatEngine;
        this.npcSelector = npcSelector;
        this.config = config;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.log = log;

        try
        {
            keybindHook = gameInterop.HookFromAddress<HandleTargetingKeybindsDelegate>(
                (nint)TargetSystem.MemberFunctionPointers.HandleTargetingKeybinds,
                KeybindDetour);
            keybindHook.Enable();
            log.Info("Targeting keybind hook created and enabled.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to hook HandleTargetingKeybinds — custom targeting input will not work.");
        }

        try
        {
            hardTargetHook = gameInterop.HookFromAddress<SetHardTargetDelegate>(
                (nint)TargetSystem.MemberFunctionPointers.SetHardTarget,
                SetHardTargetDetour);
            hardTargetHook.Enable();
            log.Info("SetHardTarget guard hook created and enabled.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to hook SetHardTarget — simulated actors could be hard-targeted and leak to the server.");
        }
    }

    /// <summary>
    /// True when the SetHardTarget guard hook is installed. When false, a mouse click on a
    /// simulated actor could set it as the native hard target and sync a fake entity id to
    /// the server — surfaced in the Diagnose tab.
    /// </summary>
    public bool IsHardTargetGuardHealthy => hardTargetHook is { IsDisposed: false };

    // The game's hard target is sent to the server. A client-only simulated actor must
    // never become it: intercept SetHardTarget and, when the requested object is one of
    // ours (entity id at/above the simulated floor), suppress the native target entirely.
    // While our own custom targeting is driving an active sim, the click is instead routed
    // into our internal lock so clicking a simulated enemy still selects it — with no
    // packet ever leaving the client.
    private bool SetHardTargetDetour(TargetSystem* thisPtr, GameObject* obj, byte ignoreTargetModes, byte a4, int a5)
    {
        try
        {
            if (obj != null && obj->EntityId >= SimulatedEntityIdFloor)
            {
                if (config.EnableCustomTargeting && combatEngine.IsActive)
                {
                    var npc = npcSelector.GetSelectedNpc(obj->EntityId);
                    if (npc != null && IsValidCandidate(npc))
                    {
                        lockedTarget = npc;
                        autoCounterSuppressed = false;
                    }
                }

                log.Debug($"Suppressed native hard-target of simulated actor 0x{obj->EntityId:X} (no server sync).");
                return false;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in SetHardTarget detour.");
        }

        return hardTargetHook!.Original(thisPtr, obj, ignoreTargetModes, a4, a5);
    }

    /// <summary>The currently locked enemy, or null.</summary>
    public SimulatedNpc? LockedTarget => lockedTarget;

    /// <summary>
    /// The simulated entity id of the locked target, or 0 when there is no valid
    /// lock. Consumers (UseActionHook, CombatEngine) treat 0 as "do not attack".
    /// </summary>
    public uint LockedTargetEntityId =>
        lockedTarget != null && lockedTarget.IsSpawned && lockedTarget.State.IsAlive
            ? lockedTarget.SimulatedEntityId
            : 0u;

    public void LockTarget(SimulatedNpc npc)
    {
        lockedTarget = IsValidCandidate(npc) ? npc : null;
    }

    public uint TryAcquireTargetForAction()
    {
        if (!ShouldTakeOver())
            return 0;

        if (lockedTarget != null && !IsValidCandidate(lockedTarget))
            lockedTarget = null;

        if (lockedTarget == null)
            AcquireTarget();

        if (lockedTarget == null)
            return 0;

        autoCounterSuppressed = false;
        return LockedTargetEntityId;
    }

    /// <summary>
    /// Called by the combat engine when an enemy's attack lands on the living player.
    /// Queues the attacker for the auto-counter check on the next targeting tick.
    /// </summary>
    public void NotifyPlayerHitBy(uint attackerEntityId)
    {
        pendingAutoCounterAttackerId = attackerEntityId;
    }

    private bool ShouldTakeOver()
        // Action Mode is soft-target only (the reticle + facing cone pick the target); the hard
        // lock/cycle targeting system is disabled so it doesn't fight the action-combat flow.
        // Fighting Mode owns its own 1v1 target and must not be fought either.
        => combatEngine.IsActive && config.EnableCustomTargeting && !config.ActionMode
           && !config.FightingMode
           && npcSelector.SelectedNpcs.Count > 0;

    /// <summary>
    /// Per-frame driver from the framework update: drop the lock if the target died
    /// or vanished, then poll input and run the lock state machine. Reading input
    /// here (rather than inside the keybind hook) guarantees reliable per-frame
    /// edge detection regardless of how often the native function fires.
    /// </summary>
    public void Tick(float deltaTime)
    {
        if (!ShouldTakeOver())
        {
            lockedTarget = null;
            // Each fresh combat starts with auto-counter armed.
            autoCounterSuppressed = false;
            pendingAutoCounterAttackerId = 0;
            prevOk = prevCancel = prevNext = prevPrev = prevLeft = prevRight = false;
            return;
        }

        if (lockedTarget != null && !IsValidCandidate(lockedTarget))
            lockedTarget = null;

        HandleInput();
        TryAutoCounter();
    }

    /// <summary>
    /// Auto-counter: if armed and the player currently has no locked target, lock the
    /// enemy that just hit the player. Runs after input so a same-frame cancel (which
    /// suppresses) takes precedence over an incoming hit.
    /// </summary>
    private void TryAutoCounter()
    {
        uint attackerId = pendingAutoCounterAttackerId;
        pendingAutoCounterAttackerId = 0;

        if (attackerId == 0 || !config.EnableAutoCounter || autoCounterSuppressed || lockedTarget != null)
            return;

        var attacker = npcSelector.GetSelectedNpc(attackerId);
        if (attacker != null && IsValidCandidate(attacker))
        {
            lockedTarget = attacker;
            log.Info($"[AutoCounter] hit by 0x{attackerId:X} '{attacker.Name}' — auto-locked.");
        }
    }

    // The keybind hook only suppresses the native targeting so it doesn't fight our
    // lock; all of our own input handling happens in Tick().
    private void KeybindDetour(TargetSystem* thisPtr)
    {
        try
        {
            if (ShouldTakeOver())
                return; // suppress native targeting while we drive it
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in targeting keybind detour.");
        }

        keybindHook!.Original(thisPtr);
    }

    private void HandleInput()
    {
        var im = InputManager.Instance();
        if (im == null)
            return;

        bool ok = im->GetInputStatus(InputCode.OK);
        bool cancel = im->GetInputStatus(InputCode.CANCEL);
        bool next = im->GetInputStatus(InputCode.TARGET_NEXT);
        bool prev = im->GetInputStatus(InputCode.TARGET_PREV);
        // On gamepad the d-pad left/right map to the LEFT/RIGHT logical codes, not
        // TARGET_NEXT/PREV — accept either so both keyboard target-cycle binds and
        // the controller d-pad switch targets.
        bool left = im->GetInputStatus(InputCode.LEFT);
        bool right = im->GetInputStatus(InputCode.RIGHT);

        bool okEdge = ok && !prevOk;
        bool cancelEdge = cancel && !prevCancel;
        bool nextEdge = (next && !prevNext) || (right && !prevRight);
        bool prevEdge = (prev && !prevPrev) || (left && !prevLeft);

        prevOk = ok;
        prevCancel = cancel;
        prevNext = next;
        prevPrev = prev;
        prevLeft = left;
        prevRight = right;

        if (okEdge || cancelEdge || nextEdge || prevEdge)
            log.Info($"[Targeting] input ok={okEdge} cancel={cancelEdge} next={nextEdge} prev={prevEdge} " +
                     $"(raw OK={ok} CANCEL={cancel} NEXT={next} PREV={prev} LEFT={left} RIGHT={right})");

        // Validate before reacting so a stale pointer never leaks into logic.
        if (lockedTarget != null && !IsValidCandidate(lockedTarget))
            lockedTarget = null;

        if (cancelEdge)
        {
            // Manual cancel: drop the lock and suppress auto-counter until the player
            // confirm-locks again (holds even when no target was locked).
            lockedTarget = null;
            autoCounterSuppressed = true;
            return;
        }

        if (okEdge && lockedTarget == null)
        {
            AcquireTarget();
            // A successful confirm-lock re-arms auto-counter.
            if (lockedTarget != null)
                autoCounterSuppressed = false;
            return;
        }

        if (lockedTarget != null)
        {
            if (nextEdge) Cycle(+1);
            else if (prevEdge) Cycle(-1);
        }
    }

    /// <summary>
    /// Acquire a target with no current lock: prefer the nearest enemy that is
    /// already attacking the player, else the nearest enemy overall.
    /// </summary>
    private void AcquireTarget()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
            return;
        var playerPos = player.Position;

        SimulatedNpc? bestAttacker = null;
        float bestAttackerDist = float.MaxValue;
        SimulatedNpc? bestAny = null;
        float bestAnyDist = float.MaxValue;

        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!IsValidCandidate(npc))
                continue;

            float dist = Vector3.Distance(EnemyWorldPos(npc), playerPos);
            if (dist < bestAnyDist)
            {
                bestAnyDist = dist;
                bestAny = npc;
            }
            if (npc.IsAttackingPlayer && dist < bestAttackerDist)
            {
                bestAttackerDist = dist;
                bestAttacker = npc;
            }
        }

        lockedTarget = bestAttacker ?? bestAny;
    }

    private void Cycle(int direction)
    {
        var list = OrderedCandidates();
        if (list.Count == 0)
        {
            lockedTarget = null;
            return;
        }

        int idx = lockedTarget != null ? list.IndexOf(lockedTarget) : -1;
        if (idx < 0)
        {
            lockedTarget = list[0];
            return;
        }

        idx = (idx + direction % list.Count + list.Count) % list.Count;
        lockedTarget = list[idx];
    }

    /// <summary>
    /// Living, drawn enemies ordered left→right by screen X so Next/Prev follow the
    /// visual layout (matching d-pad left/right feel). Off-screen enemies sort to
    /// the end; ties break on entity id for stable cycling.
    /// </summary>
    private List<SimulatedNpc> OrderedCandidates()
    {
        var candidates = new List<(SimulatedNpc Npc, float ScreenX)>();
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!IsValidCandidate(npc))
                continue;

            var head = EnemyWorldPos(npc);
            head.Y += 1.0f;
            float screenX = gameGui.WorldToScreen(head, out var screen)
                ? screen.X
                : float.MaxValue;
            candidates.Add((npc, screenX));
        }

        candidates.Sort((a, b) =>
        {
            int c = a.ScreenX.CompareTo(b.ScreenX);
            return c != 0 ? c : a.Npc.SimulatedEntityId.CompareTo(b.Npc.SimulatedEntityId);
        });

        var result = new List<SimulatedNpc>(candidates.Count);
        foreach (var c in candidates)
            result.Add(c.Npc);
        return result;
    }

    private static bool IsValidCandidate(SimulatedNpc npc)
        => npc.IsSpawned && npc.BattleChara != null && npc.State.IsAlive;

    private static Vector3 EnemyWorldPos(SimulatedNpc npc)
    {
        if (npc.BattleChara == null)
            return Vector3.Zero;
        var go = (GameObject*)npc.BattleChara;
        return new Vector3(go->Position.X, go->Position.Y, go->Position.Z);
    }

    public void Dispose()
    {
        try
        {
            keybindHook?.Disable();
            keybindHook?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Error disposing targeting keybind hook.");
        }
        keybindHook = null;

        try
        {
            hardTargetHook?.Disable();
            hardTargetHook?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Error disposing SetHardTarget guard hook.");
        }
        hardTargetHook = null;

        lockedTarget = null;
    }
}
