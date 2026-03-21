using System;
using System.Collections.Generic;
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
    private readonly IPluginLog log;

    private readonly List<SimulatedNpc> selectedNpcs = new();

    public IReadOnlyList<SimulatedNpc> SelectedNpcs => selectedNpcs;
    public int MaxTargets => 10;

    public NpcSelector(IObjectTable objectTable, ITargetManager targetManager, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
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

    public void DeselectNpc(SimulatedNpc npc)
    {
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

                // Restore original ObjectKind/SubKind
                var gameObj = (GameObject*)npc.BattleChara;
                gameObj->ObjectKind = (ObjectKind)npc.OriginalObjectKind;
                gameObj->SubKind = npc.OriginalSubKind;
                log.Info($"Restored ObjectKind/SubKind for '{npc.Name}'.");
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
    /// Validate selected NPCs still exist. Called each framework update.
    /// </summary>
    public void Tick()
    {
        for (int i = selectedNpcs.Count - 1; i >= 0; i--)
        {
            var npc = selectedNpcs[i];

            // Verify the game object ref is still valid
            if (npc.GameObjectRef == null)
            {
                log.Warning($"Combat target '{npc.Name}' lost reference.");
                selectedNpcs.RemoveAt(i);
                continue;
            }

            try
            {
                _ = npc.GameObjectRef.EntityId;
            }
            catch
            {
                log.Warning($"Combat target '{npc.Name}' reference invalidated (despawned?).");
                selectedNpcs.RemoveAt(i);
            }
        }
    }

    private int CalculateNpcHp(int level, float multiplier)
    {
        int baseHp = level switch
        {
            <= 10 => 200 + level * 50,
            <= 30 => 500 + level * 150,
            <= 50 => 2000 + level * 500,
            <= 70 => 10000 + level * 1000,
            <= 80 => 30000 + level * 2000,
            <= 90 => 80000 + level * 3000,
            _ => 150000 + level * 5000,
        };
        return (int)(baseHp * multiplier);
    }

    public void Dispose()
    {
        DeselectAll();
    }
}
