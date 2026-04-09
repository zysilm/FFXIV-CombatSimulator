using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Simulation;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace CombatSimulator.Npcs;

public unsafe class NpcSelector : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private readonly List<SimulatedNpc> selectedNpcs = new();

    public IReadOnlyList<SimulatedNpc> SelectedNpcs => selectedNpcs;
    public int MaxTargets => config.MaxTargets;

    public NpcSelector(IObjectTable objectTable, ITargetManager targetManager, Configuration config, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.config = config;
        this.log = log;
    }

    /// <summary>
    /// Select the player's current target as a combat target.
    /// Returns null with a reason string if it fails.
    /// </summary>
    public (SimulatedNpc? Npc, string? Error) SelectCurrentTarget(
        int level, float hpMultiplier, NpcBehaviorType behaviorType)
    {
        if (selectedNpcs.Count >= MaxTargets)
            return (null, "Maximum target limit reached.");

        var target = targetManager.Target;
        if (target == null)
            return (null, "No target selected. Target an NPC first.");

        // Check if already selected
        foreach (var existing in selectedNpcs)
        {
            if (existing.SimulatedEntityId == target.EntityId)
                return (null, $"'{target.Name}' is already a combat target.");
        }

        var battleChara = (BattleChara*)target.Address;
        var character = (Character*)battleChara;
        var gameObj = (GameObject*)battleChara;

        // Read original model for potential restore
        int originalModelCharaId = character->ModelContainer.ModelCharaId;

        // Save original ObjectKind/SubKind before modifying
        byte originalObjectKind = (byte)gameObj->ObjectKind;
        byte originalSubKind = gameObj->SubKind;

        // Calculate HP
        int maxHp = CalculateNpcHp(level, hpMultiplier);

        var npc = new SimulatedNpc
        {
            SimulatedEntityId = target.EntityId,
            ObjectIndex = -1,
            Name = target.Name.TextValue,
            BattleChara = battleChara,
            GameObjectRef = target,
            SpawnPosition = target.Position,
            Behavior = NpcBehavior.Create(behaviorType),
            IsSpawned = true,
            IsClientControlled = false,
            OriginalModelCharaId = originalModelCharaId,
            OriginalObjectKind = originalObjectKind,
            OriginalSubKind = originalSubKind,
            State = new SimulatedEntityState
            {
                EntityId = target.EntityId,
                Name = target.Name.TextValue,
                IsPlayer = false,
                Level = level,
                MaxHp = maxHp,
                CurrentHp = maxHp,
                MaxMp = 10000,
                CurrentMp = 10000,
                MainStat = 100 + level * 10,
                Defense = 100 + level * 5,
                MagicDefense = 100 + level * 5,
            },
        };

        // Make the NPC attackable by setting it as a BattleNpc Enemy
        gameObj->ObjectKind = ObjectKind.BattleNpc;
        gameObj->SubKind = (byte)BattleNpcSubKind.Combatant;
        log.Info($"Changed ObjectKind from {originalObjectKind} to BattleNpc, SubKind from {originalSubKind} to Combatant(5).");

        selectedNpcs.Add(npc);
        log.Info($"Selected '{npc.Name}' (EntityId=0x{target.EntityId:X}) as combat target. HP={maxHp}");
        return (npc, null);
    }

    /// <summary>
    /// Register a client-spawned NPC into the active targets list.
    /// Called from NpcSpawner.OnNpcSpawnComplete.
    /// </summary>
    public bool RegisterSpawnedNpc(SimulatedNpc npc)
    {
        if (selectedNpcs.Count >= MaxTargets)
            return false;

        foreach (var existing in selectedNpcs)
        {
            if (existing.SimulatedEntityId == npc.SimulatedEntityId)
                return false;
        }

        npc.IsClientControlled = true;
        selectedNpcs.Add(npc);
        log.Info($"Registered spawned NPC '{npc.Name}' (EntityId=0x{npc.SimulatedEntityId:X}).");
        return true;
    }

    /// <summary>
    /// Remove a client-spawned NPC from the active targets list without restoring game state.
    /// </summary>
    public void UnregisterSpawnedNpc(SimulatedNpc npc)
    {
        selectedNpcs.Remove(npc);
        log.Info($"Unregistered spawned NPC '{npc.Name}'.");
    }

    public void DeselectNpc(SimulatedNpc npc)
    {
        if (npc.IsClientControlled)
        {
            // Client-spawned NPC: just remove from list, don't restore game state
            selectedNpcs.Remove(npc);
            return;
        }

        if (npc.BattleChara != null)
        {
            try
            {
                // Restore model if changed
                if (npc.ModelChanged)
                {
                    var character = (Character*)npc.BattleChara;
                    character->ModelContainer.ModelCharaId = npc.OriginalModelCharaId;
                }

                var gameObj = (GameObject*)npc.BattleChara;

                // Restore position to where the NPC was before we moved it
                gameObj->Position = npc.SpawnPosition;

                // Clear NPC's target (may have been set to player during combat)
                var character2 = (Character*)npc.BattleChara;
                character2->TargetId = default;

                // Restore original ObjectKind/SubKind
                gameObj->ObjectKind = (ObjectKind)npc.OriginalObjectKind;
                gameObj->SubKind = npc.OriginalSubKind;
                log.Info($"Restored ObjectKind/SubKind and position for '{npc.Name}'.");
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"Failed to restore state for '{npc.Name}'.");
            }
        }

        selectedNpcs.Remove(npc);
        npc.IsSpawned = false;
        log.Info($"Deselected '{npc.Name}'.");
    }

    public void DeselectAll()
    {
        var toRemove = new List<SimulatedNpc>(selectedNpcs);
        foreach (var npc in toRemove)
            DeselectNpc(npc);
    }

    public void ChangeModel(SimulatedNpc npc, int modelCharaId)
    {
        if (npc.BattleChara == null) return;

        try
        {
            var character = (Character*)npc.BattleChara;
            character->ModelContainer.ModelCharaId = modelCharaId;
            npc.ModelChanged = true;
            log.Info($"Changed model of '{npc.Name}' to ModelCharaId={modelCharaId}.");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to change model for '{npc.Name}'.");
        }
    }

    public void RestoreModel(SimulatedNpc npc)
    {
        if (npc.BattleChara == null || !npc.ModelChanged) return;

        try
        {
            var character = (Character*)npc.BattleChara;
            character->ModelContainer.ModelCharaId = npc.OriginalModelCharaId;
            npc.ModelChanged = false;
            log.Info($"Restored model of '{npc.Name}'.");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to restore model for '{npc.Name}'.");
        }
    }

    /// <summary>
    /// Scan the object table for BattleNpc objects near the given position
    /// and auto-register them as combat targets (aggro propagation).
    /// Returns the list of newly added NPCs.
    /// </summary>
    public List<SimulatedNpc> SelectNearbyBattleNpcs(
        Vector3 position, float range, int level, float hpMultiplier, NpcBehaviorType behaviorType)
    {
        var added = new List<SimulatedNpc>();

        foreach (var obj in objectTable)
        {
            if (selectedNpcs.Count >= MaxTargets)
                break;

            // Only consider BattleNpc objects (use Dalamud's managed ObjectKind enum)
            if ((byte)obj.ObjectKind != (byte)ObjectKind.BattleNpc)
                continue;

            // Skip if already selected
            bool alreadySelected = false;
            foreach (var existing in selectedNpcs)
            {
                if (existing.SimulatedEntityId == obj.EntityId)
                {
                    alreadySelected = true;
                    break;
                }
            }
            if (alreadySelected)
                continue;

            // Check distance
            float dist = Vector3.Distance(obj.Position, position);
            if (dist > range)
                continue;

            // Try to add this NPC as a target
            var battleChara = (BattleChara*)obj.Address;
            var character = (Character*)battleChara;
            var gameObj = (GameObject*)battleChara;

            int originalModelCharaId = character->ModelContainer.ModelCharaId;
            byte originalObjectKind = (byte)gameObj->ObjectKind;
            byte originalSubKind = gameObj->SubKind;

            int maxHp = CalculateNpcHp(level, hpMultiplier);

            var npc = new SimulatedNpc
            {
                SimulatedEntityId = obj.EntityId,
                ObjectIndex = -1,
                Name = obj.Name.TextValue,
                BattleChara = battleChara,
                GameObjectRef = obj,
                SpawnPosition = obj.Position,
                Behavior = NpcBehavior.Create(behaviorType),
                IsSpawned = true,
                IsClientControlled = false,
                OriginalModelCharaId = originalModelCharaId,
                OriginalObjectKind = originalObjectKind,
                OriginalSubKind = originalSubKind,
                State = new SimulatedEntityState
                {
                    EntityId = obj.EntityId,
                    Name = obj.Name.TextValue,
                    IsPlayer = false,
                    Level = level,
                    MaxHp = maxHp,
                    CurrentHp = maxHp,
                    MaxMp = 10000,
                    CurrentMp = 10000,
                    MainStat = 100 + level * 10,
                    Defense = 100 + level * 5,
                    MagicDefense = 100 + level * 5,
                },
            };

            // Make the NPC attackable
            gameObj->ObjectKind = ObjectKind.BattleNpc;
            gameObj->SubKind = (byte)BattleNpcSubKind.Combatant;

            selectedNpcs.Add(npc);
            added.Add(npc);
            log.Info($"Aggro: Auto-selected '{npc.Name}' (EntityId=0x{obj.EntityId:X}) as combat target. HP={maxHp}");
        }

        return added;
    }

    /// <summary>
    /// Validate selected NPCs still exist. Called each framework update.
    /// AccessViolationException from reading native memory on a freed object is
    /// uncatchable — we must validate the pointer before touching any property.
    /// </summary>
    public void Tick()
    {
        for (int i = selectedNpcs.Count - 1; i >= 0; i--)
        {
            var npc = selectedNpcs[i];

            // Client-spawned NPCs: validate via spawner state, not object table
            if (npc.IsClientControlled)
            {
                if (npc.BattleChara == null || !npc.IsSpawned)
                {
                    log.Warning($"Spawned NPC '{npc.Name}' lost reference.");
                    selectedNpcs.RemoveAt(i);
                }
                continue;
            }

            if (npc.GameObjectRef == null || npc.GameObjectRef.Address == nint.Zero)
            {
                log.Warning($"Combat target '{npc.Name}' lost reference.");
                selectedNpcs.RemoveAt(i);
                continue;
            }

            // Verify the game object is still in the object table before reading native fields.
            bool found = false;
            foreach (var obj in objectTable)
            {
                if (obj.Address == npc.GameObjectRef.Address)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                log.Warning($"Combat target '{npc.Name}' no longer in object table.");
                selectedNpcs.RemoveAt(i);
            }
        }
    }

    private int CalculateNpcHp(int level, float multiplier)
    {
        // Tuned so a same-level DPS player wins 1v1 with ~55% HP remaining,
        // 1v2 is a toss-up, and 1v3 is near-certain loss.
        int baseHp = level switch
        {
            <= 10 => 100 + level * 30,
            <= 30 => 200 + level * 80,
            <= 50 => 500 + level * 200,
            <= 70 => 2000 + level * 550,
            <= 80 => 5000 + level * 1000,
            <= 90 => 15000 + level * 1500,
            _ => 40000 + level * 2500,
        };
        return (int)(baseHp * multiplier);
    }

    public void Dispose()
    {
        DeselectAll();
    }
}
