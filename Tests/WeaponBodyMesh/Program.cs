using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using CombatSimulator.Animation;
using CombatSimulator.Animation.MeshVolume;

// A dropped weapon resting on a kinematic two-sided body surface, stepped the way the weapon drop
// simulation steps mesh contacts (2 x 1/120 s, 4 substeps, stiff mesh material).
static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

// Open half-cylinder shell along X (a back/thigh-like surface), bone-local, top at y = radius.
static BodyMeshGeometry Shell(float radius, float halfLength, int around, int along)
{
    var vertices = new List<Vector3>();
    for (int j = 0; j <= along; j++)
        for (int i = 0; i <= around; i++)
        {
            var angle = MathF.PI * i / around;
            vertices.Add(new Vector3(-halfLength + 2 * halfLength * j / along, MathF.Sin(angle) * radius, MathF.Cos(angle) * radius));
        }
    var indices = new List<int>();
    for (int j = 0; j < along; j++)
        for (int i = 0; i < around; i++)
        {
            int a = j * (around + 1) + i, b = a + 1, c = a + around + 1, d = c + 1;
            indices.AddRange(new[] { a, c, b, b, c, d });
        }
    return new BodyMeshGeometry(vertices.ToArray(), indices.ToArray());
}

static (float Settled, float MaxVertical, float Lowest) Run(BodyMeshGeometry surface, Func<float, (Vector3, Quaternion)> pose, bool flipWinding)
{
    var pool = new BufferPool();
    var meshBodies = new HashSet<int>();
    var meshColliders = new HashSet<int>();
    var sim = Simulation.Create(pool, new Callbacks { MeshBodies = meshBodies, MeshColliders = meshColliders },
        new Integrator(new Vector3(0, -9.8f, 0), .99f, .85f), new SolveDescription(4, 1));
    try
    {
        var geometry = flipWinding ? new BodyMeshGeometry(surface.Vertices, Flip(surface.Indices)) : surface;
        var shape = sim.Shapes.Add(TwoSidedMesh.Create(geometry, pool));
        var (p0, q0) = pose(0);
        var kinematic = sim.Bodies.Add(BodyDescription.CreateKinematic(new RigidPose(p0, q0), default,
            new CollidableDescription(shape, .04f), new BodyActivityDescription(.01f)));
        meshColliders.Add(kinematic.Value);
        var box = new Box(.05f, .8f, .02f);
        var weapon = sim.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(p0 + new Vector3(0, .35f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, .3f)),
            default, box.ComputeInertia(15), new CollidableDescription(sim.Shapes.Add(box), .04f), new BodyActivityDescription(.01f)));
        float settled = 0, maxVertical = 0, lowest = float.MaxValue;
        var body = sim.Bodies.GetBodyReference(weapon);
        var surfaceBody = sim.Bodies.GetBodyReference(kinematic);
        for (int frame = 0; frame < 360; frame++)
        {
            var (target, rotation) = pose((frame + 1) / 60f);
            WeaponReleaseContacts.MoveKinematic(surfaceBody, target, rotation, 1f / 60);
            sim.Solver.SubstepCount = 4;
            sim.Timestep(1f / 120); sim.Timestep(1f / 120);
            // Weapon centre relative to the moving surface: positive is above the shell's top line.
            var local = Vector3.Transform(body.Pose.Position - surfaceBody.Pose.Position, Quaternion.Inverse(surfaceBody.Pose.Orientation));
            if (frame >= 120)
            {
                var relative = body.Velocity.Linear - surfaceBody.Velocity.Linear - Vector3.Cross(surfaceBody.Velocity.Angular, body.Pose.Position - surfaceBody.Pose.Position);
                maxVertical = MathF.Max(maxVertical, MathF.Abs(relative.Y));
                lowest = MathF.Min(lowest, local.Y);
                settled = relative.Length();
            }
        }
        return (settled, maxVertical, lowest);
    }
    finally { sim.Dispose(); pool.Clear(); }
}

static int[] Flip(int[] indices)
{
    var result = (int[])indices.Clone();
    for (int i = 0; i < result.Length; i += 3) (result[i + 1], result[i + 2]) = (result[i + 2], result[i + 1]);
    return result;
}

var shell = Shell(.15f, .3f, 16, 8);
var still = Run(shell, _ => (Vector3.Zero, Quaternion.Identity), false);
Console.WriteLine($"still surface: residual {still.Settled:F4} m/s, max vertical {still.MaxVertical:F4} m/s, lowest centre {still.Lowest:F4} m");
Check(still.Settled < .05f && still.MaxVertical < .1f, "weapon keeps bouncing on a still body surface");
Check(still.Lowest > .15f - .03f, "weapon sank through the body surface");
Console.WriteLine("PASS weapon settles on a still kinematic body surface");

var flipped = Run(shell, _ => (Vector3.Zero, Quaternion.Identity), true);
Check(flipped.Settled < .05f && flipped.Lowest > .15f - .03f, "reflected winding lost contact");
Console.WriteLine("PASS reflected winding gives the same support");

// The corpse slides and turns slowly (settling ragdoll); the twin follows via kinematic velocity.
var moving = Run(shell, t => (new Vector3(.2f * t, .02f * MathF.Sin(t * 2), 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .15f * t)), false);
Console.WriteLine($"moving surface: residual {moving.Settled:F4} m/s, max vertical {moving.MaxVertical:F4} m/s, lowest centre {moving.Lowest:F4} m");
Check(moving.MaxVertical < .15f, "weapon jumps on a moving body surface");
Check(moving.Lowest > .15f - .03f, "weapon sank through a moving body surface");
Console.WriteLine("PASS weapon rides a moving kinematic body surface without jumping");

// Mirrors WeaponDropNarrowPhaseCallbacks: dynamic weapons vs passive colliders, stiff mesh material.
struct Callbacks : INarrowPhaseCallbacks
{
    public HashSet<int> MeshBodies, MeshColliders;
    public void Initialize(Simulation simulation) { }
    public bool AllowContactGeneration(int worker, CollidableReference a, CollidableReference b, ref float margin)
        => a.Mobility == CollidableMobility.Dynamic ^ b.Mobility == CollidableMobility.Dynamic;
    public bool AllowContactGeneration(int worker, CollidablePair pair, int a, int b) => true;
    public bool ConfigureContactManifold<T>(int worker, CollidablePair pair, ref T manifold, out PairMaterialProperties material) where T : unmanaged, IContactManifold<T>
    {
        material.FrictionCoefficient = .6f;
        material.MaximumRecoveryVelocity = 1.5f;
        var mesh = pair.A.Mobility == CollidableMobility.Kinematic && MeshColliders.Contains(pair.A.BodyHandle.Value) ||
                   pair.B.Mobility == CollidableMobility.Kinematic && MeshColliders.Contains(pair.B.BodyHandle.Value);
        material.SpringSettings = mesh ? new SpringSettings(120, 4) : new SpringSettings(30, 1);
        return true;
    }
    public bool ConfigureContactManifold(int worker, CollidablePair pair, int a, int b, ref ConvexContactManifold manifold) => true;
    public void Dispose() { }
}

// Mirrors WeaponDropPoseIntegratorCallbacks.
struct Integrator(Vector3 gravity, float linearDamping, float angularDamping) : IPoseIntegratorCallbacks
{
    private Vector3Wide gravityDt;
    private Vector<float> linearDampingDt, angularDampingDt;
    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;
    public void Initialize(Simulation simulation) { }
    public void PrepareForIntegration(float dt)
    {
        gravityDt = Vector3Wide.Broadcast(gravity * dt);
        linearDampingDt = new Vector<float>(MathF.Pow(linearDamping, dt * 60f));
        angularDampingDt = new Vector<float>(MathF.Pow(angularDamping, dt * 60f));
    }
    public void IntegrateVelocity(Vector<int> indices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide inertia,
        Vector<int> mask, int worker, Vector<float> dt, ref BodyVelocityWide velocity)
    {
        velocity.Linear = (velocity.Linear + gravityDt) * linearDampingDt;
        velocity.Angular *= angularDampingDt;
    }
}
