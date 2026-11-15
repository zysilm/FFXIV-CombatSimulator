// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Safety;

/// <summary>
/// Per-frame upkeep that keeps the simulation's own actors (client-only enemies and allies, entity id at/above
/// <see cref="SimulatedEntityIdFloor"/>) out of the game's real targeting, whatever route the game or another
/// plugin takes:
/// <list type="bullet">
/// <item>The game's target slots (hard target, soft target, focus target) never hold one of them. The
/// SetHardTarget guard (<see cref="Targeting.PlayerTargetController"/>) refuses them and routes a click into the
/// simulation's own lock instead; anything else that leaves one there (a direct write, a path that does not go
/// through SetHardTarget) is cleared here the next frame.</item>
/// <item>They are targetable only while a simulation runs with every safety barrier up. A Spawn-mode enemy left
/// standing after the simulation stops (it stays until a zone change), or any of them while a barrier is down,
/// cannot be targeted or attacked.</item>
/// </list>
/// Log: one <c>FakeActorGuard:</c> line when a target slot is cleared, and one when the targetable state of the
/// simulation's actors flips.
/// </summary>
public sealed unsafe class FakeActorGuard
{
    private const uint SimulatedEntityIdFloor = 0xF0000000;

    private readonly CombatEngine combatEngine;
    private readonly NpcSpawner npcSpawner;
    private readonly CombatCompanionManager companionManager;
    private readonly SafetyBarriers barriers;
    private readonly IPluginLog log;

    // Actors this guard made untargetable; only these get their flag back.
    private readonly HashSet<nint> madeUntargetable = new();
    private readonly HashSet<nint> seen = new();
    private bool? lastAllowed;

    public FakeActorGuard(CombatEngine combatEngine, NpcSpawner npcSpawner, CombatCompanionManager companionManager,
        SafetyBarriers barriers, IPluginLog log)
    {
        this.combatEngine = combatEngine;
        this.npcSpawner = npcSpawner;
        this.companionManager = companionManager;
        this.barriers = barriers;
        this.log = log;
    }

    public void Tick()
    {
        try
        {
            ClearFakeTargets();
            EnforceTargetable();
        }
        catch (Exception ex)
        {
            log.Error(ex, "FakeActorGuard: upkeep failed this frame.");
        }
    }

    private void ClearFakeTargets()
    {
        var ts = TargetSystem.Instance();
        if (ts == null)
            return;

        // The game's own setters, so it updates what hangs off each slot. SetHardTarget(null) goes through the
        // SetHardTarget guard hook, which lets null through.
        if (IsFake(ts->Target))
        {
            NoteCleared(0, "hard", ts->Target->EntityId);
            ts->SetHardTarget(null);
        }
        if (IsFake(ts->SoftTarget))
        {
            NoteCleared(1, "soft", ts->SoftTarget->EntityId);
            ts->SetSoftTarget(null);
        }
        if (IsFake(ts->FocusTarget))
        {
            NoteCleared(2, "focus", ts->FocusTarget->EntityId);
            ts->SetFocusTargetByObjectId(0xE0000000);
        }
    }

    // A gamepad's soft target can land on the same simulated enemy every frame: one line when the cleared actor
    // changes, else at most one per second per slot.
    private readonly uint[] lastClearedId = new uint[3];
    private readonly long[] lastClearedLogMs = new long[3];

    private void NoteCleared(int slot, string which, uint entityId)
    {
        var now = Environment.TickCount64;
        if (entityId == lastClearedId[slot] && now - lastClearedLogMs[slot] < 1000)
            return;
        lastClearedId[slot] = entityId;
        lastClearedLogMs[slot] = now;
        log.Info($"FakeActorGuard: cleared the {which} target (simulated 0x{entityId:X}).");
    }

    private static bool IsFake(GameObject* obj) => obj != null && obj->EntityId >= SimulatedEntityIdFloor;

    private void EnforceTargetable()
    {
        var allowed = combatEngine.IsActive && barriers.AllHealthy;
        if (lastAllowed != allowed)
        {
            log.Info($"FakeActorGuard: simulated actors {(allowed ? "targetable" : "not targetable")} " +
                     $"(simulation {(combatEngine.IsActive ? "active" : "inactive")}, barriers {(barriers.AllHealthy ? "up" : $"down: {barriers.FirstDown}")}).");
            lastAllowed = allowed;
        }

        seen.Clear();
        foreach (var npc in npcSpawner.SpawnedNpcs)
            Apply(npc, allowed);
        foreach (var companion in companionManager.Companions)
            Apply(companion, allowed);
        // Forget actors that are gone (their address may be reused by a new actor).
        madeUntargetable.RemoveWhere(address => !seen.Contains(address));
    }

    private void Apply(Combatant actor, bool allowed)
    {
        if (!actor.IsSpawned || actor.BattleChara == null)
            return;
        var obj = (GameObject*)actor.BattleChara;
        if (obj->EntityId < SimulatedEntityIdFloor)
            return;

        var address = (nint)obj;
        seen.Add(address);
        if (!allowed)
        {
            if ((obj->TargetableStatus & ObjectTargetableFlags.IsTargetable) != 0)
            {
                obj->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
                madeUntargetable.Add(address);
            }
        }
        else if (madeUntargetable.Remove(address))
        {
            obj->TargetableStatus |= ObjectTargetableFlags.IsTargetable;
        }
    }
}
