using System;
using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Physics;

/// <summary>
/// Rotation-only ragdoll physics simulation operating on bone quaternions.
/// Each bone tracks angular velocity, with constraints, damping, gravity, and spring return to rest.
/// </summary>
public class RagdollSimulation
{
    private readonly DeathPoseCapture poseCapture;
    private RagdollParams parameters;

    // Per-bone physics state
    private readonly BonePhysicsState[] boneStates;
    private readonly HashSet<int> ragdollBoneSet;

    // Precomputed bone directions (parent→bone in model space) for gravity
    private readonly Vector3[] restBoneDirections;

    // Whether any bone has non-trivial velocity (optimization: skip tick when settled)
    public bool IsSettled { get; private set; }

    public RagdollSimulation(DeathPoseCapture poseCapture, RagdollParams parameters)
    {
        this.poseCapture = poseCapture;
        this.parameters = parameters;

        var snapshot = poseCapture.Snapshot;
        boneStates = new BonePhysicsState[snapshot.BoneCount];
        ragdollBoneSet = new HashSet<int>(poseCapture.RagdollBoneIndices);

        // Precompute bone directions from snapshot model-space positions
        restBoneDirections = new Vector3[snapshot.BoneCount];
        for (int i = 0; i < snapshot.BoneCount; i++)
        {
            var parentIdx = snapshot.ParentIndices[i];
            if (parentIdx >= 0 && parentIdx < snapshot.BoneCount)
            {
                var dir = snapshot.ModelPositions[i] - snapshot.ModelPositions[parentIdx];
                restBoneDirections[i] = dir.LengthSquared() > 0.0001f
                    ? Vector3.Normalize(dir) : Vector3.UnitY;
            }
            else
            {
                restBoneDirections[i] = Vector3.UnitY;
            }
        }

        // Initialize all bones to rest state
        for (int i = 0; i < snapshot.BoneCount; i++)
        {
            boneStates[i] = new BonePhysicsState
            {
                CurrentRotation = snapshot.LocalRotations[i],
                RestRotation = snapshot.LocalRotations[i],
                AngularVelocity = Vector3.Zero,
                Mass = GetBoneMass(snapshot.BoneNames[i]),
                MaxAngleFromRest = GetMaxAngle(snapshot.BoneNames[i], parameters.MaxBoneAngleDeg),
            };
        }

        IsSettled = true;
    }

    /// <summary>
    /// Update physics parameters for live tuning.
    /// </summary>
    public void UpdateParams(RagdollParams newParams)
    {
        parameters = newParams;
    }

    /// <summary>
    /// Apply a hit impulse to a random bone from a given direction.
    /// </summary>
    public void ApplyHit(Vector3 hitDirection, float forceMagnitude)
    {
        if (poseCapture.RagdollBoneIndices.Length == 0) return;

        // Pick a random bone from the ragdoll set
        var rng = Random.Shared;
        int hitBoneIdx = poseCapture.RagdollBoneIndices[rng.Next(poseCapture.RagdollBoneIndices.Length)];

        ApplyHitToBone(hitBoneIdx, hitDirection, forceMagnitude);
        IsSettled = false;
    }

    /// <summary>
    /// Apply hit impulse to a specific bone with propagation to neighbors.
    /// </summary>
    public void ApplyHitToBone(int boneIndex, Vector3 hitDirection, float forceMagnitude)
    {
        if (boneIndex < 0 || boneIndex >= boneStates.Length) return;
        if (!ragdollBoneSet.Contains(boneIndex)) return;

        // Normalize direction
        var dir = hitDirection;
        if (dir.LengthSquared() > 0.0001f)
            dir = Vector3.Normalize(dir);
        else
            dir = new Vector3(0, 0, 1); // fallback

        // Convert linear force to angular impulse: torque = cross(boneDir, force)
        // Use a random perpendicular as the "bone direction" for varied results
        var rng = Random.Shared;
        var boneDir = new Vector3(
            (float)(rng.NextDouble() * 2 - 1),
            (float)(rng.NextDouble() * 2 - 1),
            (float)(rng.NextDouble() * 2 - 1));
        if (boneDir.LengthSquared() > 0.001f)
            boneDir = Vector3.Normalize(boneDir);
        else
            boneDir = Vector3.UnitY;

        var torque = Vector3.Cross(boneDir, dir) * forceMagnitude * parameters.HitForce;

        ref var state = ref boneStates[boneIndex];
        state.AngularVelocity += torque / state.Mass;

        // Propagate to neighbors (parent + children) with attenuation
        PropagateImpulse(boneIndex, torque * 0.5f, maxDepth: 3, currentDepth: 0, fromBone: boneIndex);

        IsSettled = false;
    }

    private void PropagateImpulse(int boneIndex, Vector3 torque, int maxDepth, int currentDepth, int fromBone)
    {
        if (currentDepth >= maxDepth) return;
        if (torque.LengthSquared() < 0.0001f) return;

        var snapshot = poseCapture.Snapshot;

        // Propagate to parent
        var parentIdx = snapshot.ParentIndices[boneIndex];
        if (parentIdx >= 0 && parentIdx != fromBone && ragdollBoneSet.Contains(parentIdx))
        {
            ref var parentState = ref boneStates[parentIdx];
            parentState.AngularVelocity += torque / parentState.Mass;
            PropagateImpulse(parentIdx, torque * 0.5f, maxDepth, currentDepth + 1, boneIndex);
        }

        // Propagate to children
        var children = poseCapture.ChildrenMap[boneIndex];
        foreach (var childIdx in children)
        {
            if (childIdx == fromBone || !ragdollBoneSet.Contains(childIdx)) continue;
            ref var childState = ref boneStates[childIdx];
            childState.AngularVelocity += torque / childState.Mass;
            PropagateImpulse(childIdx, torque * 0.5f, maxDepth, currentDepth + 1, boneIndex);
        }
    }

    /// <summary>
    /// Advance the physics simulation by deltaTime seconds.
    /// </summary>
    public void Tick(float dt)
    {
        if (IsSettled) return;

        float maxVelocity = 0f;
        float damping = parameters.Damping;
        float stiffness = parameters.Stiffness;

        for (int i = 0; i < boneStates.Length; i++)
        {
            if (!ragdollBoneSet.Contains(i)) continue;

            ref var state = ref boneStates[i];

            // 1. Apply damping
            state.AngularVelocity *= MathF.Max(0, 1f - damping * dt);

            // 2. Apply gravity — torque that pulls bone direction toward -Y (down)
            if (parameters.Gravity > 0.001f)
            {
                // Rotate the rest bone direction by the current delta to get where the bone points now
                var deltaRot = state.CurrentRotation * Quaternion.Inverse(state.RestRotation);
                var currentDir = Vector3.Transform(restBoneDirections[i], deltaRot);

                // Gravity torque = cross(currentDir, down) — perpendicular axis that rotates toward down
                var gravityTorque = Vector3.Cross(currentDir, -Vector3.UnitY) * parameters.Gravity;
                state.AngularVelocity += gravityTorque * dt / state.Mass;
            }

            // 3. Apply spring return toward rest pose
            var restInv = Quaternion.Inverse(state.RestRotation);
            var delta = state.CurrentRotation * restInv;

            // Normalize and extract axis-angle
            if (delta.W < 0) delta = Quaternion.Negate(delta);
            delta = Quaternion.Normalize(delta);

            float sinHalfAngle = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
            if (sinHalfAngle > 0.0001f)
            {
                float halfAngle = MathF.Atan2(sinHalfAngle, delta.W);
                float angle = 2f * halfAngle;
                var axis = new Vector3(delta.X, delta.Y, delta.Z) / sinHalfAngle;

                // Spring torque: pull back toward rest
                state.AngularVelocity -= axis * angle * stiffness * dt;
            }

            // 4. Integrate rotation
            var angVelMag = state.AngularVelocity.Length();
            if (angVelMag > 0.0001f)
            {
                var rotAxis = state.AngularVelocity / angVelMag;
                float rotAngle = angVelMag * dt;

                // Create incremental rotation quaternion
                var incrementalRot = Quaternion.CreateFromAxisAngle(rotAxis, rotAngle);
                state.CurrentRotation = Quaternion.Normalize(incrementalRot * state.CurrentRotation);
            }

            // 5. Clamp: limit deviation from rest pose
            float maxAngleRad = state.MaxAngleFromRest * MathF.PI / 180f;
            ClampRotation(ref state.CurrentRotation, state.RestRotation, maxAngleRad);

            // Track max velocity for settled check
            if (angVelMag > maxVelocity)
                maxVelocity = angVelMag;
        }

        // If all bones have negligible velocity, mark as settled
        IsSettled = maxVelocity < 0.01f;
    }

    /// <summary>
    /// Get the current bone rotations to apply as overrides.
    /// Only returns bones that have deviated from rest.
    /// </summary>
    public Dictionary<int, Quaternion> GetBoneOverrides()
    {
        var result = new Dictionary<int, Quaternion>();

        foreach (var boneIdx in poseCapture.RagdollBoneIndices)
        {
            ref var state = ref boneStates[boneIdx];

            // Only include bones that differ from rest
            var dot = MathF.Abs(Quaternion.Dot(state.CurrentRotation, state.RestRotation));
            if (dot < 0.9999f)
            {
                // Output delta rotation (deviation from rest) so BoneManipulator can multiply it
                // onto the current model-space rotation: delta = current * inverse(rest)
                result[boneIdx] = Quaternion.Normalize(
                    state.CurrentRotation * Quaternion.Inverse(state.RestRotation));
            }
        }

        return result;
    }

    private static void ClampRotation(ref Quaternion current, Quaternion rest, float maxAngleRad)
    {
        var diff = current * Quaternion.Inverse(rest);
        if (diff.W < 0) diff = Quaternion.Negate(diff);
        diff = Quaternion.Normalize(diff);

        float sinHalf = MathF.Sqrt(diff.X * diff.X + diff.Y * diff.Y + diff.Z * diff.Z);
        if (sinHalf < 0.0001f) return;

        float halfAngle = MathF.Atan2(sinHalf, diff.W);
        float angle = 2f * halfAngle;

        if (angle > maxAngleRad)
        {
            // Clamp to max angle
            float clampedHalf = maxAngleRad * 0.5f;
            var axis = new Vector3(diff.X, diff.Y, diff.Z) / sinHalf;
            var clamped = Quaternion.CreateFromAxisAngle(axis, maxAngleRad);
            current = Quaternion.Normalize(clamped * rest);
        }
    }

    /// <summary>
    /// Assign relative mass per bone type — heavier bones resist force more.
    /// </summary>
    private static float GetBoneMass(string boneName)
    {
        if (boneName.Contains("kosi") || boneName.Contains("hara"))
            return 5.0f; // pelvis/waist — heaviest
        if (boneName.Contains("sebo"))
            return 3.0f; // spine
        if (boneName.Contains("kao"))
            return 2.0f; // head
        if (boneName.Contains("kubi"))
            return 2.0f; // neck
        if (boneName.Contains("sako"))
            return 2.5f; // clavicle
        if (boneName.Contains("ude_a"))
            return 1.5f; // upper arm
        if (boneName.Contains("ude_b"))
            return 1.2f; // forearm
        if (boneName.Contains("te_"))
            return 0.8f; // hand
        if (boneName.Contains("asi_a"))
            return 3.0f; // thigh
        if (boneName.Contains("asi_b"))
            return 2.0f; // shin
        if (boneName.Contains("asi_c"))
            return 1.0f; // foot

        return 1.5f; // default
    }

    /// <summary>
    /// Per-bone max angle from rest (in degrees). Spine/neck are more constrained, limbs more free.
    /// </summary>
    private static float GetMaxAngle(string boneName, float globalMax)
    {
        if (boneName.Contains("kosi") || boneName.Contains("hara"))
            return globalMax * 0.3f; // pelvis: very constrained
        if (boneName.Contains("sebo"))
            return globalMax * 0.5f; // spine: somewhat constrained
        if (boneName.Contains("kubi"))
            return globalMax * 0.6f; // neck
        if (boneName.Contains("kao"))
            return globalMax * 0.7f; // head

        return globalMax; // limbs: full range
    }
}

/// <summary>
/// Configurable ragdoll physics parameters.
/// </summary>
public class RagdollParams
{
    public float HitForce { get; set; } = 1.0f;
    public float Damping { get; set; } = 3.0f;
    public float Stiffness { get; set; } = 1.0f;
    public float MaxBoneAngleDeg { get; set; } = 30f;
    public float Gravity { get; set; } = 1.0f;
}

public struct BonePhysicsState
{
    public Quaternion CurrentRotation;
    public Quaternion RestRotation;
    public Vector3 AngularVelocity;
    public float Mass;
    public float MaxAngleFromRest; // degrees
}
