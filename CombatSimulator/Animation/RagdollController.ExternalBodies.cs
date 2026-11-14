// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;

namespace CombatSimulator.Animation;

public unsafe partial class RagdollController
{
    public readonly struct ExternalShapePart
    {
        public readonly Vector3 HalfExtents;
        public readonly Vector3 LocalPosition;
        public readonly Quaternion LocalOrientation;

        public ExternalShapePart(Vector3 halfExtents, Vector3 localPosition, Quaternion localOrientation)
        {
            HalfExtents = halfExtents;
            LocalPosition = localPosition;
            LocalOrientation = localOrientation;
        }
    }

    public sealed class ExternalBodyHandle
    {
        internal BodyHandle Body;
        internal TypedIndex[] Shapes = Array.Empty<TypedIndex>();
        internal int Generation;
        internal bool Removed;
    }

    public readonly struct ExternalRigBodySpec
    {
        public readonly string Name;
        public readonly IReadOnlyList<ExternalShapePart> Parts;
        public readonly float Mass;
        public readonly Vector3 Position;
        public readonly Quaternion Orientation;
        public readonly Vector3 LinearVelocity;
        public readonly Vector3 AngularVelocity;

        public ExternalRigBodySpec(
            string name,
            IReadOnlyList<ExternalShapePart> parts,
            float mass,
            Vector3 position,
            Quaternion orientation,
            Vector3 linearVelocity,
            Vector3 angularVelocity)
        {
            Name = name;
            Parts = parts;
            Mass = mass;
            Position = position;
            Orientation = orientation;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
        }
    }

    public enum ExternalRigJointKind
    {
        // BallSocket + optional SwingLimit + AngularMotor + optional pose-guide servo (chain garments/limbs).
        BallSocketSwing = 0,
        // A single DistanceLimit between two anchor points: inextensible above MaxDistance, compressible to
        // MinDistance. The edge primitive of the garment tube (ring-adjacent + ring-to-ring + diagonals).
        DistanceLimit = 1,
    }

    public readonly struct ExternalRigJointSpec
    {
        public readonly ExternalRigJointKind Kind;
        public readonly int ParentIndex;
        public readonly int ChildIndex;
        // Ball-socket fields.
        public readonly Vector3 AnchorWorld;
        public readonly float SwingLimit;
        public readonly float InitialSwingFactor;
        public readonly float PoseGuideMaxForce;
        public readonly float PoseGuideFrequency;
        // Distance-limit fields (Kind == DistanceLimit). Offsets are in each body's local frame.
        public readonly Vector3 LocalOffsetChild;
        public readonly Vector3 LocalOffsetParent;
        public readonly float MinDistance;
        public readonly float MaxDistance;
        public readonly float DistanceFrequency;
        public readonly float DistanceDamping;
        /// <summary>Torque the joint resists bending with. 0 keeps the stiff rig default (0.65).</summary>
        public float JointFriction { get; init; }

        public ExternalRigJointSpec(int parentIndex, int childIndex, Vector3 anchorWorld, float swingLimit,
            float initialSwingFactor = 0.28f, float poseGuideMaxForce = 0f, float poseGuideFrequency = 5f)
        {
            Kind = ExternalRigJointKind.BallSocketSwing;
            ParentIndex = parentIndex;
            ChildIndex = childIndex;
            AnchorWorld = anchorWorld;
            SwingLimit = swingLimit;
            InitialSwingFactor = Math.Clamp(initialSwingFactor, 0.05f, 1f);
            PoseGuideMaxForce = MathF.Max(0f, poseGuideMaxForce);
            PoseGuideFrequency = MathF.Max(0.1f, poseGuideFrequency);
            LocalOffsetChild = Vector3.Zero;
            LocalOffsetParent = Vector3.Zero;
            MinDistance = 0f;
            MaxDistance = 0f;
            DistanceFrequency = 0f;
            DistanceDamping = 0f;
        }

        private ExternalRigJointSpec(int parentIndex, int childIndex,
            Vector3 localOffsetChild, Vector3 localOffsetParent,
            float minDistance, float maxDistance, float frequency, float damping)
        {
            Kind = ExternalRigJointKind.DistanceLimit;
            ParentIndex = parentIndex;
            ChildIndex = childIndex;
            AnchorWorld = Vector3.Zero;
            SwingLimit = 0f;
            InitialSwingFactor = 1f;
            PoseGuideMaxForce = 0f;
            PoseGuideFrequency = 5f;
            LocalOffsetChild = localOffsetChild;
            LocalOffsetParent = localOffsetParent;
            MinDistance = MathF.Max(0f, minDistance);
            MaxDistance = MathF.Max(MinDistance, maxDistance);
            DistanceFrequency = MathF.Max(0.1f, frequency);
            DistanceDamping = Math.Clamp(damping, 0f, 8f);
        }

        /// <summary>A center-to-center (or offset) distance limit edge, e.g. a garment tube seam.</summary>
        public static ExternalRigJointSpec MakeDistanceLimit(int childIndex, int parentIndex,
            float minDistance, float maxDistance, float frequency = 15f, float damping = 1.5f)
            => new(parentIndex, childIndex, Vector3.Zero, Vector3.Zero, minDistance, maxDistance, frequency, damping);
    }

    public sealed class ExternalRigHandle
    {
        internal readonly List<ExternalBodyHandle> Bodies = new();
        internal readonly List<ConstraintHandle> Constraints = new();
        internal readonly List<(int, int)> ConnectedPairs = new();
        // Swing-limit constraints that start tight (hold garment shape at spawn) and relax to their full
        // ROM over the first ~second. Kept separately from Constraints so the target ROM can be re-applied
        // each frame without re-scanning the mixed constraint list.
        internal readonly List<RigSwingConstraint> SwingConstraints = new();
        internal readonly List<RigPoseGuideConstraint> PoseGuideConstraints = new();
        internal int Generation;
        internal bool Removed;
    }

    internal struct RigSwingConstraint
    {
        public ConstraintHandle Handle;
        public Vector3 AxisLocalA;
        public Vector3 AxisLocalB;
        public SpringSettings Spring;
        public float TargetSwing;
    }

    internal struct RigPoseGuideConstraint
    {
        public ConstraintHandle Handle;
        public Quaternion TargetRelativeRotationLocalA;
        public float Frequency;
        public float MaxForce;
    }

    /// <summary>Fraction of a garment joint's full swing ROM used at spawn, before it relaxes to full over
    /// ~1s. Shared with the DismembermentController local-sim rig so both hosts spawn equally stiff.</summary>
    public const float GarmentRigInitialSwingFactor = 0.28f;

    private bool TryCreateExternalDynamicBodyCore(IReadOnlyList<ExternalShapePart> parts, float mass,
        Vector3 position, Quaternion orientation, Vector3 linearVelocity, Vector3 angularVelocity,
        out ExternalBodyHandle? handle)
    {
        handle = null;
        if (!isActive || simulation == null || bufferPool == null || parts.Count == 0)
            return false;

        try
        {
            var shapeIndex = CreateExternalBodyShape(parts, out var shapes, out var inertia, mass);
            var bodyHandle = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose(position, Quaternion.Normalize(orientation)),
                default(BodyVelocity),
                inertia,
                new CollidableDescription(shapeIndex, 0.016f),
                new BodyActivityDescription(0.01f)));

            var body = simulation.Bodies.GetBodyReference(bodyHandle);
            body.Velocity.Linear = linearVelocity;
            body.Velocity.Angular = angularVelocity;
            body.Awake = true;
            externalDynamicBodyHandles.Add(bodyHandle.Value);
            WakeRagdollBodiesForBiomechanicalSettle();

            handle = new ExternalBodyHandle
            {
                Body = bodyHandle,
                Shapes = shapes.ToArray(),
                Generation = externalBodyGeneration,
            };
            externalBodies.Add(handle);
            prevAllAsleep = false;
            BeginBiomechanicalSettle(0.35f);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RagdollController: failed to create external body");
            return false;
        }
    }

    private bool TryCreateExternalRigCore(
        IReadOnlyList<ExternalRigBodySpec> bodySpecs,
        IReadOnlyList<ExternalRigJointSpec> jointSpecs,
        out ExternalRigHandle? handle,
        bool collideWithRagdoll = true)
    {
        handle = null;
        if (!isActive || simulation == null || bufferPool == null || bodySpecs.Count == 0)
            return false;

        var rig = new ExternalRigHandle { Generation = externalBodyGeneration };
        try
        {
            for (var i = 0; i < bodySpecs.Count; i++)
            {
                var spec = bodySpecs[i];
                if (spec.Parts.Count == 0)
                    throw new InvalidOperationException($"External rig body '{spec.Name}' has no shapes.");

                var shapeIndex = CreateExternalBodyShape(spec.Parts, out var shapes, out var inertia, spec.Mass);
                var bodyHandle = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                    new RigidPose(spec.Position, Quaternion.Normalize(spec.Orientation)),
                    default(BodyVelocity),
                    inertia,
                    new CollidableDescription(shapeIndex, 0.016f),
                    new BodyActivityDescription(0.01f)));

                var body = simulation.Bodies.GetBodyReference(bodyHandle);
                body.Velocity.Linear = spec.LinearVelocity;
                body.Velocity.Angular = spec.AngularVelocity;
                body.Awake = true;

                var bodyHandleWrapper = new ExternalBodyHandle
                {
                    Body = bodyHandle,
                    Shapes = shapes.ToArray(),
                    Generation = externalBodyGeneration,
                };
                rig.Bodies.Add(bodyHandleWrapper);
                externalBodies.Add(bodyHandleWrapper);
                externalDynamicBodyHandles.Add(bodyHandle.Value);
                externalRigDynamicBodyHandles.Add(bodyHandle.Value);
                if (!collideWithRagdoll)
                    externalRigNoRagdollContactBodyHandles.Add(bodyHandle.Value);
            }

            foreach (var joint in jointSpecs)
            {
                if (joint.ParentIndex < 0 || joint.ParentIndex >= rig.Bodies.Count ||
                    joint.ChildIndex < 0 || joint.ChildIndex >= rig.Bodies.Count ||
                    joint.ParentIndex == joint.ChildIndex)
                    continue;

                AddExternalRigJoint(rig, joint);
            }

            externalRigs.Add(rig);
            handle = rig;
            prevAllAsleep = false;
            WakeRagdollBodiesForBiomechanicalSettle();
            BeginBiomechanicalSettle(0.45f);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RagdollController: failed to create external rig");
            RemoveExternalRig(rig);
            return false;
        }
    }

    private void AddExternalRigJoint(ExternalRigHandle rig, ExternalRigJointSpec joint)
    {
        if (simulation == null)
            return;

        var parent = rig.Bodies[joint.ParentIndex];
        var child = rig.Bodies[joint.ChildIndex];

        if (joint.Kind == ExternalRigJointKind.DistanceLimit)
        {
            rig.Constraints.Add(simulation.Solver.Add(child.Body, parent.Body, new DistanceLimit
            {
                LocalOffsetA = joint.LocalOffsetChild,
                LocalOffsetB = joint.LocalOffsetParent,
                MinimumDistance = joint.MinDistance,
                MaximumDistance = joint.MaxDistance,
                SpringSettings = new SpringSettings(joint.DistanceFrequency, joint.DistanceDamping),
            }));

            var loD = Math.Min(parent.Body.Value, child.Body.Value);
            var hiD = Math.Max(parent.Body.Value, child.Body.Value);
            var pairD = (loD, hiD);
            rig.ConnectedPairs.Add(pairD);
            externalRigConnectedPairs.Add(pairD);
            return;
        }

        var parentBody = simulation.Bodies.GetBodyReference(parent.Body);
        var childBody = simulation.Bodies.GetBodyReference(child.Body);

        var anchorWorld = joint.AnchorWorld;
        var childLocalAnchor = Vector3.Transform(anchorWorld - childBody.Pose.Position, Quaternion.Inverse(childBody.Pose.Orientation));
        var parentLocalAnchor = Vector3.Transform(anchorWorld - parentBody.Pose.Position, Quaternion.Inverse(parentBody.Pose.Orientation));
        var jointSpring = new SpringSettings(9f, 1.4f);
        var limitSpring = new SpringSettings(5f, 1.6f);

        rig.Constraints.Add(simulation.Solver.Add(child.Body, parent.Body, new BallSocket
        {
            LocalOffsetA = childLocalAnchor,
            LocalOffsetB = parentLocalAnchor,
            SpringSettings = jointSpring,
        }));

        var childAxisWorld = NormalizeOrFallback(Vector3.Transform(Vector3.UnitY, childBody.Pose.Orientation), Vector3.UnitY);
        if (joint.SwingLimit > 0f)
        {
            var axisLocalA = Vector3.Normalize(Vector3.Transform(childAxisWorld, Quaternion.Inverse(childBody.Pose.Orientation)));
            var axisLocalB = Vector3.Normalize(Vector3.Transform(childAxisWorld, Quaternion.Inverse(parentBody.Pose.Orientation)));
            // Spawn at a fraction of the target ROM so the garment holds its worn shape on handoff, then
            // relax to full via RelaxExternalRigSwingLimits over ~1s.
            var target = Math.Clamp(joint.SwingLimit, 0.05f, MathF.PI - 0.05f);
            var handle = simulation.Solver.Add(child.Body, parent.Body, new SwingLimit
            {
                AxisLocalA = axisLocalA,
                AxisLocalB = axisLocalB,
                MaximumSwingAngle = Math.Clamp(target * joint.InitialSwingFactor, 0.05f, MathF.PI - 0.05f),
                SpringSettings = limitSpring,
            });
            rig.Constraints.Add(handle);
            rig.SwingConstraints.Add(new RigSwingConstraint
            {
                Handle = handle,
                AxisLocalA = axisLocalA,
                AxisLocalB = axisLocalB,
                Spring = limitSpring,
                TargetSwing = target,
            });
        }

        rig.Constraints.Add(simulation.Solver.Add(child.Body, parent.Body, new AngularMotor
        {
            TargetVelocityLocalA = Vector3.Zero,
            Settings = new MotorSettings(joint.JointFriction > 0f ? joint.JointFriction : 0.65f, 0.45f),
        }));

        if (joint.PoseGuideMaxForce > 0f)
        {
            var target = Quaternion.Normalize(Quaternion.Inverse(childBody.Pose.Orientation) * parentBody.Pose.Orientation);
            var handle = simulation.Solver.Add(child.Body, parent.Body, new AngularServo
            {
                TargetRelativeRotationLocalA = target,
                SpringSettings = new SpringSettings(joint.PoseGuideFrequency, 1.15f),
                ServoSettings = new ServoSettings(6f, 0f, joint.PoseGuideMaxForce),
            });
            rig.Constraints.Add(handle);
            rig.PoseGuideConstraints.Add(new RigPoseGuideConstraint
            {
                Handle = handle,
                TargetRelativeRotationLocalA = target,
                Frequency = joint.PoseGuideFrequency,
                MaxForce = joint.PoseGuideMaxForce,
            });
        }

        var lo = Math.Min(parent.Body.Value, child.Body.Value);
        var hi = Math.Max(parent.Body.Value, child.Body.Value);
        var pair = (lo, hi);
        rig.ConnectedPairs.Add(pair);
        externalRigConnectedPairs.Add(pair);
    }

    private bool TrySetExternalBodyShapeCore(ExternalBodyHandle? handle,
        IReadOnlyList<ExternalShapePart> parts, float mass)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration ||
            simulation == null || bufferPool == null || parts.Count == 0)
            return false;

        TypedIndex[]? newShapes = null;
        try
        {
            var newShape = CreateExternalBodyShape(parts, out var shapeList, out var inertia, mass);
            newShapes = shapeList.ToArray();
            var oldShapes = handle.Shapes;

            simulation.Bodies.SetShape(handle.Body, newShape);
            simulation.Bodies.SetLocalInertia(handle.Body, in inertia);
            var body = simulation.Bodies.GetBodyReference(handle.Body);
            body.Awake = true;

            handle.Shapes = newShapes;
            foreach (var shape in oldShapes)
                try { simulation.Shapes.RemoveAndDispose(shape, bufferPool); } catch { }

            WakeRagdollBodiesForBiomechanicalSettle();
            prevAllAsleep = false;
            BeginBiomechanicalSettle(0.25f);
            return true;
        }
        catch (Exception ex)
        {
            if (newShapes != null)
                foreach (var shape in newShapes)
                    try { simulation?.Shapes.RemoveAndDispose(shape, bufferPool); } catch { }
            log.Warning(ex, "RagdollController: failed to replace external body shape");
            return false;
        }
    }

    private TypedIndex CreateExternalBodyShape(IReadOnlyList<ExternalShapePart> parts,
        out List<TypedIndex> shapes, out BodyInertia inertia, float mass)
    {
        shapes = new List<TypedIndex>(parts.Count + 1);
        TypedIndex shapeIndex;
        if (parts.Count == 1 &&
            parts[0].LocalPosition.LengthSquared() < 1e-6f &&
            IsIdentity(parts[0].LocalOrientation))
        {
            var h = parts[0].HalfExtents;
            shapeIndex = simulation!.Shapes.Add(new Box(h.X * 2f, h.Y * 2f, h.Z * 2f));
            shapes.Add(shapeIndex);
        }
        else
        {
            bufferPool!.Take<CompoundChild>(parts.Count, out var children);
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                var h = p.HalfExtents;
                var childShape = simulation!.Shapes.Add(new Box(h.X * 2f, h.Y * 2f, h.Z * 2f));
                shapes.Add(childShape);
                children[i] = new CompoundChild
                {
                    ShapeIndex = childShape,
                    LocalPosition = p.LocalPosition,
                    LocalOrientation = p.LocalOrientation,
                };
            }

            shapeIndex = simulation!.Shapes.Add(new Compound(children));
            shapes.Add(shapeIndex);
        }

        var half = ComputeExternalShapeHalf(parts);
        inertia = new Box(half.X * 2f, half.Y * 2f, half.Z * 2f)
            .ComputeInertia(MathF.Max(0.01f, mass));
        return shapeIndex;
    }

    public bool TryGetExternalBodyPose(ExternalBodyHandle? handle, out Vector3 position, out Quaternion orientation,
        out Vector3 linearVelocity, out Vector3 angularVelocity)
    {
        position = Vector3.Zero;
        orientation = Quaternion.Identity;
        linearVelocity = Vector3.Zero;
        angularVelocity = Vector3.Zero;

        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        try
        {
            var body = simulation.Bodies.GetBodyReference(handle.Body);
            position = body.Pose.Position;
            orientation = body.Pose.Orientation;
            linearVelocity = body.Velocity.Linear;
            angularVelocity = body.Velocity.Angular;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryGetExternalRigBodyPose(ExternalRigHandle? handle, int bodyIndex,
        out Vector3 position, out Quaternion orientation, out Vector3 linearVelocity, out Vector3 angularVelocity)
    {
        position = Vector3.Zero;
        orientation = Quaternion.Identity;
        linearVelocity = Vector3.Zero;
        angularVelocity = Vector3.Zero;

        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration ||
            simulation == null || bodyIndex < 0 || bodyIndex >= handle.Bodies.Count)
            return false;

        return TryGetExternalBodyPose(handle.Bodies[bodyIndex], out position, out orientation,
            out linearVelocity, out angularVelocity);
    }

    /// <summary>
    /// Carry one rig body to a pose (a garment segment that stays on the body). It remains a dynamic body
    /// and is simply re-placed every frame, with the velocity that takes it there.
    /// </summary>
    private bool TrySetExternalRigBodyPoseCore(ExternalRigHandle? handle, int bodyIndex,
        Vector3 position, Quaternion orientation, Vector3 linearVelocity, Vector3 angularVelocity)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration ||
            simulation == null || bodyIndex < 0 || bodyIndex >= handle.Bodies.Count ||
            !float.IsFinite(position.LengthSquared()) || !float.IsFinite(orientation.LengthSquared()))
            return false;

        try
        {
            var body = simulation.Bodies.GetBodyReference(handle.Bodies[bodyIndex].Body);
            body.Pose.Position = position;
            body.Pose.Orientation = Quaternion.Normalize(orientation);
            body.Velocity.Linear = linearVelocity;
            body.Velocity.Angular = angularVelocity;
            body.Awake = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Let one rig body start (or stop) colliding with the ragdoll's own bodies.</summary>
    private bool SetExternalRigBodyRagdollContactCore(ExternalRigHandle? handle, int bodyIndex, bool collide)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration ||
            simulation == null || bodyIndex < 0 || bodyIndex >= handle.Bodies.Count)
            return false;

        var body = handle.Bodies[bodyIndex].Body.Value;
        if (collide) externalRigNoRagdollContactBodyHandles.Remove(body);
        else externalRigNoRagdollContactBodyHandles.Add(body);
        return true;
    }

    /// <summary>Replace one rig body's collision shape (a garment segment collapsing flat).</summary>
    private bool TrySetExternalRigBodyShapeCore(ExternalRigHandle? handle, int bodyIndex,
        IReadOnlyList<ExternalShapePart> parts, float mass)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration ||
            simulation == null || bodyIndex < 0 || bodyIndex >= handle.Bodies.Count)
            return false;

        return TrySetExternalBodyShape(handle.Bodies[bodyIndex], parts, mass);
    }

    private bool TryApplyExternalRigVelocityDeltaCore(ExternalRigHandle? handle, Vector3 velocityDelta)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        foreach (var bodyHandle in handle.Bodies)
        {
            try
            {
                var body = simulation.Bodies.GetBodyReference(bodyHandle.Body);
                body.Velocity.Linear += velocityDelta;
                body.Awake = true;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private bool TryDampenExternalRigVelocityCore(ExternalRigHandle? handle,
        float horizontalScale, float verticalScale, float angularScale)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        horizontalScale = Math.Clamp(horizontalScale, 0f, 1f);
        verticalScale = Math.Clamp(verticalScale, 0f, 1f);
        angularScale = Math.Clamp(angularScale, 0f, 1f);

        foreach (var bodyHandle in handle.Bodies)
        {
            try
            {
                var body = simulation.Bodies.GetBodyReference(bodyHandle.Body);
                var lin = body.Velocity.Linear;
                body.Velocity.Linear = new Vector3(lin.X * horizontalScale, lin.Y * verticalScale, lin.Z * horizontalScale);
                body.Velocity.Angular *= angularScale;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Re-apply each garment swing constraint's ROM as <paramref name="factor"/> (0..1) of its
    /// stored target, widening the joints from their tight spawn ROM to full as the garment settles.</summary>
    private bool RelaxExternalRigSwingLimitsCore(ExternalRigHandle? handle, float factor)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        factor = Math.Clamp(factor, 0f, 1f);
        foreach (var s in handle.SwingConstraints)
        {
            try
            {
                simulation.Solver.ApplyDescription(s.Handle, new SwingLimit
                {
                    AxisLocalA = s.AxisLocalA,
                    AxisLocalB = s.AxisLocalB,
                    MaximumSwingAngle = Math.Clamp(s.TargetSwing * factor, 0.05f, MathF.PI - 0.05f),
                    SpringSettings = s.Spring,
                });
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Fade temporary garment pose-guide servos from their captured handoff pose toward zero force.</summary>
    private bool ApplyExternalRigPoseGuidanceCore(ExternalRigHandle? handle, float strength)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        strength = Math.Clamp(strength, 0f, 1f);
        foreach (var s in handle.PoseGuideConstraints)
        {
            try
            {
                simulation.Solver.ApplyDescription(s.Handle, new AngularServo
                {
                    TargetRelativeRotationLocalA = s.TargetRelativeRotationLocalA,
                    SpringSettings = new SpringSettings(s.Frequency, 1.15f),
                    ServoSettings = new ServoSettings(6f, 0f, s.MaxForce * strength),
                });
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private bool TrySetExternalBodyRotationCore(ExternalBodyHandle? handle, Quaternion orientation, float angularVelocityScale)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        try
        {
            var body = simulation.Bodies.GetBodyReference(handle.Body);
            body.Pose.Orientation = Quaternion.Normalize(orientation);
            body.Velocity.Angular *= Math.Clamp(angularVelocityScale, 0f, 1f);
            body.Awake = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryApplyExternalVelocityDeltaCore(ExternalBodyHandle? handle, Vector3 velocityDelta)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        try
        {
            var body = simulation.Bodies.GetBodyReference(handle.Body);
            body.Velocity.Linear += velocityDelta;
            body.Awake = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryDampenExternalBodyVelocityCore(ExternalBodyHandle? handle,
        float horizontalScale, float verticalScale, float angularScale)
    {
        if (handle == null || handle.Removed || handle.Generation != externalBodyGeneration || simulation == null)
            return false;

        try
        {
            horizontalScale = Math.Clamp(horizontalScale, 0f, 1f);
            verticalScale = Math.Clamp(verticalScale, 0f, 1f);
            angularScale = Math.Clamp(angularScale, 0f, 1f);

            var body = simulation.Bodies.GetBodyReference(handle.Body);
            var lin = body.Velocity.Linear;
            body.Velocity.Linear = new Vector3(lin.X * horizontalScale, lin.Y * verticalScale, lin.Z * horizontalScale);
            body.Velocity.Angular *= angularScale;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Remove every constraint still attached to <paramref name="body"/>. BEPU corrupts the solver if a
    /// body is removed while constraints reference it: the constraint keeps a now-dangling body index,
    /// and the next island-sleeper traversal (Solver.EnumerateConnectedBodyReferences) faults on it —
    /// far from the real cause. The debug assert that would catch this is compiled out in release, so
    /// every body removal must run this first. Idempotent: a body whose constraints are already gone
    /// enumerates an empty list.
    /// </summary>
    private void RemoveConstraintsOnBody(BodyHandle body)
    {
        if (simulation == null) return;
        try
        {
            var bodyRef = simulation.Bodies.GetBodyReference(body);
            ref var attached = ref bodyRef.Constraints;
            if (attached.Count == 0) return;

            // Snapshot first — Solver.Remove mutates the body's constraint list as we go.
            var handles = new ConstraintHandle[attached.Count];
            for (var i = 0; i < attached.Count; i++)
                handles[i] = attached[i].ConnectingConstraintHandle;

            foreach (var h in handles)
            {
                try { simulation.Solver.Remove(h); }
                catch (Exception ex)
                {
                    log.Warning(ex, $"RagdollController: failed to remove constraint {h.Value} on body {body.Value}");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"RagdollController: failed to enumerate constraints on body {body.Value}");
        }
    }

    private void RemoveExternalBodyCore(ExternalBodyHandle? handle)
    {
        if (handle == null || handle.Removed)
            return;

        if (simulation != null && bufferPool != null && handle.Generation == externalBodyGeneration)
        {
            RemoveConstraintsOnBody(handle.Body);
            try { simulation.Bodies.Remove(handle.Body); }
            catch (Exception ex) { log.Warning(ex, $"RagdollController: failed to remove body {handle.Body.Value}"); }
            foreach (var shape in handle.Shapes)
                try { simulation.Shapes.RemoveAndDispose(shape, bufferPool); } catch { }
        }

        externalDynamicBodyHandles.Remove(handle.Body.Value);
        handle.Removed = true;
        externalBodies.Remove(handle);
    }

    private void RemoveExternalRigCore(ExternalRigHandle? handle)
    {
        if (handle == null || handle.Removed)
            return;

        if (simulation != null && handle.Generation == externalBodyGeneration)
        {
            // Never swallow this silently: a constraint that survives here is exactly what turns the
            // body removal below into solver corruption. RemoveExternalBody sweeps any leftovers, but
            // a failure here means our bookkeeping is wrong and we want to see it.
            foreach (var constraint in handle.Constraints)
            {
                try { simulation.Solver.Remove(constraint); }
                catch (Exception ex)
                {
                    log.Warning(ex, $"RagdollController: failed to remove rig constraint {constraint.Value}");
                }
            }
        }

        foreach (var pair in handle.ConnectedPairs)
            externalRigConnectedPairs.Remove(pair);
        handle.Constraints.Clear();
        handle.ConnectedPairs.Clear();

        foreach (var bodyHandle in handle.Bodies)
        {
            if (handle.Generation == externalBodyGeneration) DetachWeaponContactBody(bodyHandle.Body);
            externalRigNoRagdollContactBodyHandles.Remove(bodyHandle.Body.Value);
            externalRigDynamicBodyHandles.Remove(bodyHandle.Body.Value);
            RemoveExternalBody(bodyHandle);
        }

        handle.Bodies.Clear();
        handle.Removed = true;
        externalRigs.Remove(handle);
    }

    private bool AnyExternalBodyAwake()
    {
        if (simulation == null || externalBodies.Count == 0)
            return false;

        var anyAwake = false;
        for (int i = externalBodies.Count - 1; i >= 0; i--)
        {
            var handle = externalBodies[i];
            if (handle.Removed || handle.Generation != externalBodyGeneration)
            {
                externalDynamicBodyHandles.Remove(handle.Body.Value);
                externalBodies.RemoveAt(i);
                continue;
            }

            try
            {
                if (simulation.Bodies.GetBodyReference(handle.Body).Awake)
                    anyAwake = true;
            }
            catch
            {
                externalDynamicBodyHandles.Remove(handle.Body.Value);
                handle.Removed = true;
                externalBodies.RemoveAt(i);
            }
        }

        return anyAwake;
    }

    private static bool IsIdentity(Quaternion q)
        => MathF.Abs(q.X) < 1e-5f && MathF.Abs(q.Y) < 1e-5f &&
           MathF.Abs(q.Z) < 1e-5f && MathF.Abs(q.W - 1f) < 1e-5f;

    private static Vector3 ComputeExternalShapeHalf(IReadOnlyList<ExternalShapePart> parts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var p in parts)
        {
            var half = RotatedLocalHalfExtent(p.LocalOrientation, p.HalfExtents);
            min = Vector3.Min(min, p.LocalPosition - half);
            max = Vector3.Max(max, p.LocalPosition + half);
        }

        return new Vector3(
            MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X)),
            MathF.Max(MathF.Abs(min.Y), MathF.Abs(max.Y)),
            MathF.Max(MathF.Abs(min.Z), MathF.Abs(max.Z)));
    }

    private static Vector3 RotatedLocalHalfExtent(Quaternion rot, Vector3 half)
    {
        var x = Vector3.Transform(Vector3.UnitX, rot);
        var y = Vector3.Transform(Vector3.UnitY, rot);
        var z = Vector3.Transform(Vector3.UnitZ, rot);
        return new Vector3(
            MathF.Abs(x.X) * half.X + MathF.Abs(y.X) * half.Y + MathF.Abs(z.X) * half.Z,
            MathF.Abs(x.Y) * half.X + MathF.Abs(y.Y) * half.Y + MathF.Abs(z.Y) * half.Z,
            MathF.Abs(x.Z) * half.X + MathF.Abs(y.Z) * half.Y + MathF.Abs(z.Z) * half.Z);
    }
}
