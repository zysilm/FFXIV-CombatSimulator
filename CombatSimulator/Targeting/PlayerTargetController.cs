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
/// Design (see综合提升 discussion):
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

    private SimulatedNpc? lockedTarget;

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

    private bool ShouldTakeOver()
        => combatEngine.IsActive && config.EnableCustomTargeting && npcSelector.SelectedNpcs.Count > 0;

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
            prevOk = prevCancel = prevNext = prevPrev = prevLeft = prevRight = false;
            return;
        }

        if (lockedTarget != null && !IsValidCandidate(lockedTarget))
            lockedTarget = null;

        HandleInput();
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

        if (cancelEdge && lockedTarget != null)
        {
            lockedTarget = null;
            return;
        }

        if (okEdge && lockedTarget == null)
        {
            AcquireTarget();
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
        lockedTarget = null;
    }
}
