// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Camera;
using CombatSimulator.Encounters.Definitions;
using CombatSimulator.Npcs;
using CombatSimulator.Recipes;
using CombatSimulator.Safety;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Encounters.Runtime;

/// <summary>Translates declarative encounter operations into the plugin's existing controllers.</summary>
public sealed class EncounterRuntimeAdapter : IEncounterRuntime
{
    private readonly CombatRecipeRunner recipeRunner;
    private readonly NpcSelector npcSelector;
    private readonly NpcSpawner npcSpawner;
    private readonly CombatEngine combatEngine;
    private readonly CameraModeCoordinator cameraCoordinator;
    private readonly UseActionHook useActionHook;
    private readonly IClientState clientState;
    private readonly IChatGui chatGui;

    public EncounterRuntimeAdapter(
        CombatRecipeRunner recipeRunner,
        NpcSelector npcSelector,
        NpcSpawner npcSpawner,
        CombatEngine combatEngine,
        CameraModeCoordinator cameraCoordinator,
        UseActionHook useActionHook,
        IClientState clientState,
        IChatGui chatGui)
    {
        this.recipeRunner = recipeRunner;
        this.npcSelector = npcSelector;
        this.npcSpawner = npcSpawner;
        this.combatEngine = combatEngine;
        this.cameraCoordinator = cameraCoordinator;
        this.useActionHook = useActionHook;
        this.clientState = clientState;
        this.chatGui = chatGui;
    }

    public bool IsSimulationActive => combatEngine.IsActive;
    public bool IsSpawnReady => npcSpawner.PendingCount == 0;
    public bool IsPlayerDead => combatEngine.IsActive && !combatEngine.State.PlayerState.IsAlive;
    public int EnemyCount => npcSelector.SelectedNpcs.Count;

    public int LivingEnemyCount
    {
        get
        {
            var count = 0;
            foreach (var npc in npcSelector.SelectedNpcs)
                if (npc.IsAlive) count++;
            return count;
        }
    }

    public bool CanStart(out string reason)
    {
        if (!clientState.IsLoggedIn || Core.Services.ObjectTable.LocalPlayer == null)
        {
            reason = "You must be logged in and in the world.";
            return false;
        }
        if (clientState.IsPvP || clientState.IsGPosing)
        {
            reason = "Directed encounters are unavailable in PvP and Group Pose.";
            return false;
        }
        if (!useActionHook.IsHealthy)
        {
            reason = "The UseAction safety hook is not healthy.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool StartRecipe(string recipeName) => recipeRunner.TryStart(recipeName);
    public void StopRecipe(bool print) => recipeRunner.Stop(print);
    public int SpawnEnemies(IReadOnlyList<CombatRecipeEnemyGroup> enemies) => recipeRunner.QueueEnemies(enemies);

    public bool HasActor(EncounterActorBinding binding) => ResolveActor(binding) != null;
    public nint GetActorAddress(EncounterActorBinding binding) => ResolveActor(binding)?.Address ?? nint.Zero;

    public float? GetActorHpRatio(EncounterActorBinding binding)
    {
        var actor = ResolveActor(binding);
        if (actor == null || actor.State.MaxHp <= 0) return null;
        return Math.Clamp(actor.State.CurrentHp / (float)actor.State.MaxHp, 0f, 1f);
    }

    public bool IsActorDead(EncounterActorBinding binding)
    {
        var actor = ResolveActor(binding);
        return actor != null && !actor.IsAlive;
    }

    public void ApplyEnemyPressure(EncounterActorBinding binding, EncounterCueDefinition cue)
    {
        var actor = ResolveActor(binding);
        if (actor == null) return;

        actor.Behavior.AutoAttackDelay = Math.Clamp(
            actor.Behavior.AutoAttackDelay * Math.Max(0.1f, cue.AutoAttackDelayMultiplier), 0.25f, 60f);
        actor.Behavior.MoveSpeed = Math.Clamp(
            actor.Behavior.MoveSpeed * Math.Max(0.1f, cue.MoveSpeedMultiplier), 0.5f, 20f);
        actor.State.DamageTakenMultiplier = Math.Clamp(
            actor.State.DamageTakenMultiplier * Math.Max(0.05f, cue.DamageTakenMultiplier), 0.05f, 10f);
    }

    public void PlayPlayerVictory() => combatEngine.TriggerPlayerVictory();

    public void SubmitCamera(EncounterActorBinding binding, EncounterCueDefinition cue)
    {
        var actor = ResolveActor(binding);
        var position = actor?.GameObjectRef?.Position;
        if (!position.HasValue) return;

        cameraCoordinator.Submit(CameraOwner.Encounter, new CameraRequest
        {
            OrbitCenter = position.Value + Vector3.UnitY * cue.HeightOffset,
            Distance = Math.Clamp(cue.Distance, 1.5f, 20f),
            Fov = Math.Clamp(cue.FovDegrees, 20f, 100f) * MathF.PI / 180f,
        });
    }

    public void ReleaseCamera() => cameraCoordinator.Release(CameraOwner.Encounter);
    public void AddCombatLog(string message) => combatEngine.AddLogEntry(message, CombatLogType.Info);
    public void Print(string message) => chatGui.Print($"[Encounter] {message}");
    public void PrintError(string message) => chatGui.PrintError($"[Encounter] {message}");

    private SimulatedNpc? ResolveActor(EncounterActorBinding binding)
    {
        var occurrence = Math.Max(1, binding.Occurrence);
        var found = 0;
        foreach (var npc in npcSelector.SelectedNpcs)
        {
            if (!string.IsNullOrWhiteSpace(binding.Name) &&
                !npc.Name.Contains(binding.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (++found == occurrence)
                return npc;
        }
        return null;
    }
}
