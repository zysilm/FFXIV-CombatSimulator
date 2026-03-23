using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace CombatSimulator.Physics;

/// <summary>
/// Orchestrates ragdoll physics for dead entities. Manages lifecycle:
/// death → animation settle → pose capture → ragdoll ready → hit → active → settled → idle.
/// </summary>
public class RagdollController : IDisposable
{
    private readonly BoneManipulator boneManipulator;
    private readonly Configuration config;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    // Per-entity ragdoll state
    private readonly Dictionary<ulong, EntityRagdollState> entityStates = new();

    // Max simultaneous ragdolls (performance guard)
    private const int MaxSimultaneousRagdolls = 4;

    public bool IsAvailable => boneManipulator.IsHooked;

    public RagdollController(
        BoneManipulator boneManipulator,
        Configuration config,
        IClientState clientState,
        IPluginLog log)
    {
        this.boneManipulator = boneManipulator;
        this.config = config;
        this.clientState = clientState;
        this.log = log;
    }

    /// <summary>
    /// Notify that an entity has died. Starts the settle timer before pose capture.
    /// </summary>
    public void OnEntityDeath(ulong entityId, nint characterAddress, bool isPlayer)
    {
        if (!config.EnableRagdoll || !IsAvailable) return;
        if (entityStates.Count >= MaxSimultaneousRagdolls) return;

        if (entityStates.ContainsKey(entityId))
            return; // already tracking

        entityStates[entityId] = new EntityRagdollState
        {
            EntityId = entityId,
            CharacterAddress = characterAddress,
            IsPlayer = isPlayer,
            Phase = RagdollPhase.WaitingForPose,
            SettleTimer = config.DeathPoseSettleTime,
        };

        log.Info($"RagdollController: Tracking entity 0x{entityId:X} for ragdoll (settle time: {config.DeathPoseSettleTime}s).");
    }

    /// <summary>
    /// Notify that a dead entity was hit. Triggers ragdoll physics if pose is captured.
    /// </summary>
    public void OnHitDeadEntity(ulong entityId, Vector3 attackerPosition)
    {
        if (!config.EnableRagdoll || !IsAvailable) return;
        if (!entityStates.TryGetValue(entityId, out var state)) return;
        if (state.Phase != RagdollPhase.Ready && state.Phase != RagdollPhase.Active) return;

        // Calculate hit direction from attacker to target
        var targetPos = GetEntityWorldPosition(state.CharacterAddress);
        var hitDir = targetPos - attackerPosition;
        if (hitDir.LengthSquared() > 0.0001f)
            hitDir = Vector3.Normalize(hitDir);
        else
            hitDir = Vector3.UnitZ;

        state.Simulation!.ApplyHit(hitDir, config.RagdollHitForce);
        state.Phase = RagdollPhase.Active;

        // Ensure animation is frozen when actively simulating
        boneManipulator.FreezeAnimation(state.CharacterAddress);
    }

    /// <summary>
    /// Check if an entity has ragdoll ready (pose captured).
    /// </summary>
    public bool IsReady(ulong entityId)
    {
        return entityStates.TryGetValue(entityId, out var state) &&
               (state.Phase == RagdollPhase.Ready || state.Phase == RagdollPhase.Active);
    }

    /// <summary>
    /// Main update loop. Call every frame.
    /// </summary>
    public void Tick(float deltaTime)
    {
        if (!config.EnableRagdoll || entityStates.Count == 0) return;

        var toRemove = new List<ulong>();

        foreach (var (entityId, state) in entityStates)
        {
            try
            {
                TickEntity(state, deltaTime);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"RagdollController: Error ticking entity 0x{entityId:X}");
                toRemove.Add(entityId);
            }
        }

        foreach (var id in toRemove)
            RemoveEntity(id);
    }

    private void TickEntity(EntityRagdollState state, float dt)
    {
        switch (state.Phase)
        {
            case RagdollPhase.WaitingForPose:
                state.SettleTimer -= dt;
                if (state.SettleTimer <= 0)
                {
                    // Time to capture the death pose
                    var snapshot = boneManipulator.CaptureSnapshot(state.CharacterAddress);
                    if (snapshot != null)
                    {
                        state.PoseCapture = new DeathPoseCapture(snapshot);
                        state.Simulation = new RagdollSimulation(state.PoseCapture, new RagdollParams
                        {
                            HitForce = config.RagdollHitForce,
                            Damping = config.RagdollDamping,
                            Stiffness = config.RagdollStiffness,
                            MaxBoneAngleDeg = config.RagdollMaxBoneAngle,
                            Gravity = config.RagdollGravity,
                        });
                        state.Phase = RagdollPhase.Ready;

                        // Freeze animation so our bone writes persist
                        boneManipulator.FreezeAnimation(state.CharacterAddress);

                        log.Info($"RagdollController: Pose captured for 0x{state.EntityId:X} " +
                                 $"({state.PoseCapture.Snapshot.BoneCount} bones, " +
                                 $"{state.PoseCapture.RagdollBoneIndices.Length} ragdoll bones, " +
                                 $"orientation: {state.PoseCapture.Orientation})");
                    }
                    else
                    {
                        log.Warning($"RagdollController: Failed to capture pose for 0x{state.EntityId:X}");
                        state.Phase = RagdollPhase.Failed;
                    }
                }
                break;

            case RagdollPhase.Active:
                if (state.Simulation == null) break;

                // Update physics params from config (allow live tweaking)
                state.Simulation.Tick(dt);

                // Push bone overrides to BoneManipulator
                var overrides = state.Simulation.GetBoneOverrides();
                if (overrides.Count > 0)
                {
                    var overrideSet = new BoneOverrideSet { Rotations = overrides };
                    boneManipulator.SetBoneOverrides(state.CharacterAddress, overrideSet);
                }
                else
                {
                    boneManipulator.ClearBoneOverrides(state.CharacterAddress);
                }

                // If simulation settled, transition to Ready (idle but still captured)
                if (state.Simulation.IsSettled)
                {
                    state.Phase = RagdollPhase.Ready;
                }
                break;

            case RagdollPhase.Ready:
                // Idle — still maintaining freeze + last bone state. Waiting for next hit.
                break;

            case RagdollPhase.Failed:
                // Nothing to do — entity won't participate in ragdoll.
                break;
        }
    }

    /// <summary>
    /// Remove an entity from ragdoll tracking and restore its state.
    /// </summary>
    public void RemoveEntity(ulong entityId)
    {
        if (entityStates.TryGetValue(entityId, out var state))
        {
            boneManipulator.ClearBoneOverrides(state.CharacterAddress);
            // Don't unfreeze — the entity is dead, let the death animation system handle it
            entityStates.Remove(entityId);
            log.Verbose($"RagdollController: Removed entity 0x{entityId:X}");
        }
    }

    /// <summary>
    /// Stop all ragdoll simulations and clean up.
    /// </summary>
    public void StopAll()
    {
        foreach (var (_, state) in entityStates)
        {
            boneManipulator.ClearBoneOverrides(state.CharacterAddress);
            boneManipulator.UnfreezeAnimation(state.CharacterAddress);
        }
        entityStates.Clear();
        boneManipulator.ClearAllOverrides();
    }

    private unsafe Vector3 GetEntityWorldPosition(nint characterAddress)
    {
        try
        {
            var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)characterAddress;
            return gameObj->Position;
        }
        catch
        {
            return Vector3.Zero;
        }
    }

    public void Dispose()
    {
        StopAll();
    }
}

public enum RagdollPhase
{
    WaitingForPose,
    Ready,
    Active,
    Failed,
}

public class EntityRagdollState
{
    public ulong EntityId;
    public nint CharacterAddress;
    public bool IsPlayer;
    public RagdollPhase Phase;
    public float SettleTimer;
    public DeathPoseCapture? PoseCapture;
    public RagdollSimulation? Simulation;
}
