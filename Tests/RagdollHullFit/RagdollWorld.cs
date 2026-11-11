using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using CombatSimulator.Animation;

/// <summary>
/// A minimal world for laying a jointed body on the ground, with the solver budget, joint springs,
/// friction and settle damping the ragdoll itself runs with. No self-collision.
/// </summary>
internal static class RagdollWorld
{
    /// <param name="noContact">Pairs of body handles that do not collide; null switches self-collision off altogether.</param>
    /// <param name="startOverlap">The ragdoll's allowance for overlap its bodies start out with, or null for plain contacts.</param>
    public static Simulation Create(BufferPool pool, HashSet<(int, int)>? noContact = null, float gravity = 9.81f,
        StartOverlapAllowance? startOverlap = null)
        => Simulation.Create(pool, new Narrow { NoContact = noContact, StartOverlap = startOverlap }, new Integrator { Gravity = gravity },
            new SolveDescription(RagdollTuning.SolverIterations, RagdollTuning.SolverSubsteps));

    public static void Join(Simulation simulation, BodyHandle parent, BodyHandle child, Vector3 anchor, float swing)
    {
        var a = simulation.Bodies.GetBodyReference(parent).Pose;
        var b = simulation.Bodies.GetBodyReference(child).Pose;
        Vector3 Local(RigidPose pose, Vector3 world) => Vector3.Transform(world - pose.Position, Quaternion.Conjugate(pose.Orientation));
        simulation.Solver.Add(parent, child, new BallSocket
        {
            LocalOffsetA = Local(a, anchor), LocalOffsetB = Local(b, anchor), SpringSettings = new SpringSettings(RagdollTuning.JointSpringFrequency, 1.5f),
        });
        simulation.Solver.Add(parent, child, new SwingLimit
        {
            AxisLocalA = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(a.Orientation)),
            AxisLocalB = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(b.Orientation)),
            MaximumSwingAngle = swing, SpringSettings = new SpringSettings(RagdollTuning.LimitSpringFrequency, 1),
        });
        // Limp joints still resist a little, as the real ragdoll joints do.
        simulation.Solver.Add(parent, child, new AngularMotor { Settings = new MotorSettings(2f, 0.01f) });
    }

    private struct Narrow : INarrowPhaseCallbacks
    {
        public HashSet<(int, int)>? NoContact;
        public StartOverlapAllowance? StartOverlap;
        public void Initialize(Simulation simulation) { }
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        {
            if (a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static) return true;
            // Body against body, as the ragdoll has it: everything but the pairs excluded by name.
            if (NoContact == null) return false;
            int lo = Math.Min(a.BodyHandle.Value, b.BodyHandle.Value), hi = Math.Max(a.BodyHandle.Value, b.BodyHandle.Value);
            return !NoContact.Contains((lo, hi));
        }
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
            out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            // The ragdoll's materials: firm against the ground, slow and overdamped body against body.
            var bodies = pair.A.Mobility == CollidableMobility.Dynamic && pair.B.Mobility == CollidableMobility.Dynamic;
            pairMaterial = bodies
                ? new PairMaterialProperties(RagdollTuning.Friction, 0.2f, new SpringSettings(20, 2))
                : new PairMaterialProperties(RagdollTuning.Friction, 2f, new SpringSettings(30, 1));
            if (bodies) StartOverlap?.Apply(pair.A.BodyHandle.Value, pair.B.BodyHandle.Value, ref manifold);
            return true;
        }
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;
        public void Dispose() { }
    }

    private struct Integrator : IPoseIntegratorCallbacks
    {
        public float Gravity;
        private Vector3Wide gravity;
        private Vector<float> damping;
        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public readonly bool AllowSubstepsForUnconstrainedBodies => false;
        public readonly bool IntegrateVelocityForKinematics => false;
        public void Initialize(Simulation simulation) { }
        public void PrepareForIntegration(float dt)
        {
            gravity = Vector3Wide.Broadcast(new Vector3(0, -Gravity * dt, 0));
            damping = new Vector<float>(MathF.Pow(RagdollTuning.Damping, dt * 60f)); // what the ragdoll applies to a settling body
        }
        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia,
            Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            velocity.Linear = (velocity.Linear + gravity) * damping;
            velocity.Angular *= damping;
        }
    }
}
