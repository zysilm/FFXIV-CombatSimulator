// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using CombatSimulator.Camera;
using CombatSimulator.Encounters.Definitions;
using CombatSimulator.Integration;
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
    private readonly Configuration config;
    private readonly NpcSelector npcSelector;
    private readonly CombatEngine combatEngine;
    private readonly CameraModeCoordinator cameraCoordinator;
    private readonly UseActionHook useActionHook;
    private readonly IClientState clientState;
    private readonly IChatGui chatGui;
    private readonly VNavmeshIpc vnavmesh;
    private readonly IPluginLog log;
    private float? originalDamageMultiplier;
    private float? originalPlayerDamageTakenMultiplier;
    private bool? originalEnableTargetApproach;
    private bool? originalUseVNavmeshTargetApproach;

    public EncounterRuntimeAdapter(
        CombatRecipeRunner recipeRunner,
        Configuration config,
        NpcSelector npcSelector,
        CombatEngine combatEngine,
        CameraModeCoordinator cameraCoordinator,
        UseActionHook useActionHook,
        IClientState clientState,
        IChatGui chatGui,
        VNavmeshIpc vnavmesh,
        IPluginLog log)
    {
        this.recipeRunner = recipeRunner;
        this.config = config;
        this.npcSelector = npcSelector;
        this.combatEngine = combatEngine;
        this.cameraCoordinator = cameraCoordinator;
        this.useActionHook = useActionHook;
        this.clientState = clientState;
        this.chatGui = chatGui;
        this.vnavmesh = vnavmesh;
        this.log = log;
    }

    public bool IsSimulationActive => combatEngine.IsActive;
    public bool IsSpawnReady => recipeRunner.IsSpawnReady;
    public bool IsPlayerDead => combatEngine.IsActive && !combatEngine.State.PlayerState.IsAlive;
    public float? PlayerHpRatio => combatEngine.State.PlayerState.MaxHp > 0
        ? Math.Clamp(
            combatEngine.State.PlayerState.CurrentHp / (float)combatEngine.State.PlayerState.MaxHp,
            0f,
            1f)
        : null;
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
    public int SpawnEnemies(EncounterCueDefinition cue, EncounterActorBinding? approachAnchor)
    {
        if (approachAnchor == null)
            return recipeRunner.QueueEnemies(cue.Enemies);

        var anchor = ResolveActor(approachAnchor);
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (anchor?.GameObjectRef == null || player == null)
        {
            log.Warning("Encounter reinforcement anchor or player was unavailable; using normal placement.");
            return recipeRunner.QueueEnemies(cue.Enemies);
        }

        vnavmesh.RefreshStatus(force: true);
        if (!vnavmesh.CanPathfind)
        {
            PrintError("vnavmesh is not ready; reinforcements will use normal placement.");
            return recipeRunner.QueueEnemies(cue.Enemies);
        }

        originalEnableTargetApproach ??= config.EnableTargetApproach;
        originalUseVNavmeshTargetApproach ??= config.UseVNavmeshTargetApproach;
        config.EnableTargetApproach = true;
        config.UseVNavmeshTargetApproach = true;

        var anchorPosition = anchor.GameObjectRef.Position;
        var playerPosition = player.Position;
        var away = new Vector3(
            anchorPosition.X - playerPosition.X,
            0f,
            anchorPosition.Z - playerPosition.Z);
        if (away.LengthSquared() < 0.01f)
        {
            var yaw = anchor.GameObjectRef.Rotation + MathF.PI;
            away = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        }
        else
        {
            away = Vector3.Normalize(away);
        }

        return recipeRunner.QueueEnemies(
            cue.Enemies,
            _ => FindReinforcementSpawnPoint(anchorPosition, away, cue));
    }
    public int SpawnCompanions(IReadOnlyList<CombatRecipeCompanionGroup> companions) =>
        recipeRunner.QueueCompanions(companions);

    public bool HasActor(EncounterActorBinding binding) => ResolveActor(binding) != null;
    public nint GetActorAddress(EncounterActorBinding binding) =>
        string.Equals(binding.Name, "$player", StringComparison.OrdinalIgnoreCase)
            ? Core.Services.ObjectTable.LocalPlayer?.Address ?? nint.Zero
            : ResolveActor(binding)?.Address ?? nint.Zero;

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

    public void ApplyPartyPower(EncounterCueDefinition cue)
    {
        originalDamageMultiplier ??= combatEngine.DamageMultiplier;
        originalPlayerDamageTakenMultiplier ??= combatEngine.State.PlayerState.DamageTakenMultiplier;

        combatEngine.DamageMultiplier = Math.Clamp(
            combatEngine.DamageMultiplier * Math.Max(0.05f, cue.OutgoingDamageMultiplier),
            0.05f,
            20f);
        var player = combatEngine.State.PlayerState;
        player.DamageTakenMultiplier = Math.Clamp(
            player.DamageTakenMultiplier * Math.Max(0.05f, cue.PlayerDamageTakenMultiplier),
            0.05f,
            10f);
        if (cue.HealPlayerRatio > 0f && player.MaxHp > 0)
        {
            var healing = (int)MathF.Round(player.MaxHp * Math.Clamp(cue.HealPlayerRatio, 0f, 1f));
            player.CurrentHp = Math.Min(player.MaxHp, player.CurrentHp + healing);
        }
    }

    public void ResetEncounterModifiers()
    {
        if (originalDamageMultiplier.HasValue)
            combatEngine.DamageMultiplier = originalDamageMultiplier.Value;
        if (originalPlayerDamageTakenMultiplier.HasValue)
            combatEngine.State.PlayerState.DamageTakenMultiplier = originalPlayerDamageTakenMultiplier.Value;
        if (originalEnableTargetApproach.HasValue)
            config.EnableTargetApproach = originalEnableTargetApproach.Value;
        if (originalUseVNavmeshTargetApproach.HasValue)
            config.UseVNavmeshTargetApproach = originalUseVNavmeshTargetApproach.Value;
        originalDamageMultiplier = null;
        originalPlayerDamageTakenMultiplier = null;
        originalEnableTargetApproach = null;
        originalUseVNavmeshTargetApproach = null;
    }

    public void SubmitCamera(EncounterActorBinding binding, EncounterCueDefinition cue)
    {
        Vector3? position = null;
        if (string.Equals(binding.Name, "$player", StringComparison.OrdinalIgnoreCase))
            position = Core.Services.ObjectTable.LocalPlayer?.Position;
        else
            position = ResolveActor(binding)?.GameObjectRef?.Position;
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

    private Vector3? FindReinforcementSpawnPoint(
        Vector3 anchor,
        Vector3 away,
        EncounterCueDefinition cue)
    {
        var minDistance = Math.Clamp(cue.ApproachSpawnMinDistance, 5f, 100f);
        var maxDistance = Math.Clamp(cue.ApproachSpawnMaxDistance, minDistance, 100f);
        var halfArc = Math.Clamp(cue.ApproachSpawnArcDegrees, 0f, 120f) * MathF.PI / 180f;
        var baseYaw = MathF.Atan2(away.X, away.Z);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var distance = minDistance + Random.Shared.NextSingle() * (maxDistance - minDistance);
            var yaw = baseYaw + (Random.Shared.NextSingle() * 2f - 1f) * halfArc;
            var direction = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
            var candidate = anchor + direction * distance;
            candidate.Y = anchor.Y + 8f;

            try
            {
                var snapped = vnavmesh.PointOnFloor(candidate, false, 4f)
                              ?? vnavmesh.NearestPointReachable(candidate, 4f, 12f);
                if (snapped.HasValue && FlatDistance(anchor, snapped.Value) >= minDistance * 0.7f)
                    return snapped.Value;
            }
            catch (Exception ex)
            {
                log.Verbose($"Encounter reinforcement navmesh placement failed: {ex.Message}");
            }
        }

        log.Warning("No distant navmesh point was found behind the encounter anchor; using normal placement.");
        return null;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

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
