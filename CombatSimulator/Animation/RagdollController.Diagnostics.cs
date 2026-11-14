// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Numerics;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    /// <summary>Debug draw data for a single ragdoll capsule body.</summary>
    public struct DebugCapsule
    {
        public Vector3 Position;      // capsule center (world space)
        public Quaternion Orientation; // capsule rotation
        public float Radius;
        public float HalfLength;      // half of the segment length (capsule extends along Y)
        public RagdollColliderShape ColliderShape;
        public Vector3 BoxHalfExtents;
        /// <summary>Edges of the fitted hull the body really collides with (pairs of points in its frame), if any.</summary>
        public Vector3[]? HullEdges;
        public string Name;
        public JointType Joint;
        public float SwingLimit;
    }

    /// <summary>
    /// Get current capsule positions/sizes for debug rendering.
    /// Returns empty if ragdoll is not active.
    /// </summary>
    public List<DebugCapsule> GetDebugCapsules()
    {
        var result = new List<DebugCapsule>();
        if (!isActive || simulation == null) return result;

        var BoneDefs = GetBoneDefs();
        foreach (var rb in ragdollBones)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);

            // Find matching bone def for capsule dimensions
            RagdollBoneDef boneDef = default;
            foreach (var def in BoneDefs)
                if (def.Name == rb.Name) { boneDef = def; break; }

            result.Add(new DebugCapsule
            {
                Position = bodyRef.Pose.Position,
                Orientation = bodyRef.Pose.Orientation,
                Radius = rb.CapsuleRadius,
                HalfLength = rb.CapsuleHalfLength,
                ColliderShape = rb.ColliderShape,
                BoxHalfExtents = rb.BoxHalfExtents,
                HullEdges = rb.HullEdges,
                Name = rb.Name,
                Joint = boneDef.Joint,
                SwingLimit = boneDef.SwingLimit,
            });
        }
        return result;
    }

    /// <summary>
    /// Get capsule positions from the live skeleton pose (for overlay when ragdoll is OFF).
    /// Reads bone world positions from the character's current animation pose.
    /// </summary>
    public List<DebugCapsule> GetDebugCapsulesFromSkeleton(nint characterAddress)
    {
        var result = new List<DebugCapsule>();
        var skel = skeletons.TryGetSkeleton(characterAddress);
        if (skel == null) return result;

        if (!skel.HasRoot) return result;
        var root = skel.Root;
        var skelPos = root.Position;
        var skelRot = root.Rotation;

        var BoneDefs = GetBoneDefs();
        var pose = skel.ModelPose;

        foreach (var def in BoneDefs)
        {
            var idx = ResolveBoneIndex(skel, def.Name);
            if (idx < 0 || idx >= skel.BoneCount) continue;

            ref var mt = ref pose[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);

            // Convert to world space
            var worldPos = skelPos + Vector3.Transform(modelPos, skelRot);
            var worldRot = Quaternion.Normalize(skelRot * modelRot);

            result.Add(new DebugCapsule
            {
                Position = worldPos,
                Orientation = worldRot,
                Radius = def.CapsuleRadius,
                HalfLength = ResolveBodyHalfLength(def),
                ColliderShape = def.ColliderShape,
                BoxHalfExtents = ResolveBoxHalfExtents(def, ResolveBodyHalfLength(def)),
                Name = def.Name,
                Joint = def.Joint,
                SwingLimit = def.SwingLimit,
            });
        }
        return result;
    }

    /// <summary>
    /// Time spent by every ragdoll since the last <see cref="TakeFrameCost"/>: in all (render-frame
    /// work), tracking living characters' collision, and stepping physics; and how many ragdoll
    /// frames were stepped (not resting) and how many character collision sets were tracked.
    /// </summary>
    public readonly record struct FrameCost(int Frames, int SteppedFrames, long TrackedColliders,
        double TotalMs, double CollidersMs, double PhysicsMs);

    private static long costFrames, costStepped, costTracked, costTotalTicks, costColliderTicks, costPhysicsTicks;

    public static FrameCost TakeFrameCost()
    {
        static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var cost = new FrameCost((int)costFrames, (int)costStepped, costTracked,
            Ms(costTotalTicks), Ms(costColliderTicks), Ms(costPhysicsTicks));
        costFrames = costStepped = costTracked = costTotalTicks = costColliderTicks = costPhysicsTicks = 0;
        return cost;
    }
}
