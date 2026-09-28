using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using CombatSimulator.Animation;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
var policy = new WeaponReleaseContacts();
policy.Add(1, 10); policy.Add(2, 10);
for (var i = 0; i < 10000; i++) policy.Observe(1, 10, false);
Check(!policy.Allows(1, 10) && !policy.Allows(10, 1), "overlap timed out or pair order matters");
Check(policy.Allows(1, 11) && policy.Allows(3, 10), "torso or another actor's weapon filtered");
Console.WriteLine("PASS overlapping hand stays excluded; unrelated contacts remain enabled");
policy.Observe(1, 10, true); policy.Observe(1, 10, false); policy.Observe(1, 10, true);
Check(!policy.Allows(1, 10), "unstable separation enabled contact");
policy.Observe(1, 10, true);
Check(policy.Allows(1, 10) && !policy.Allows(2, 10), "weapons not independent");
policy.Observe(1, 10, false);
Check(policy.Allows(1, 10), "later contact disabled again");
Console.WriteLine("PASS stable separation re-enables each weapon independently and permanently");
policy.Remove(10);
Check(policy.Allows(2, 10), "recycled body handle inherited suppression");
Check(WeaponReleaseContacts.IsReleaseArm("j_ude_b_r", "j_te_r") &&
      WeaponReleaseContacts.IsReleaseArm("j_te_l", "j_oya_a_l") &&
      !WeaponReleaseContacts.IsReleaseArm("j_sebo_a", "j_sebo_b") &&
      !WeaponReleaseContacts.IsReleaseArm("j_asi_b_l", "j_asi_c_l"), "incorrect body region");
Console.WriteLine("PASS cleanup and forearm/hand classification");
var half = new Vector3(.02f, 1.5f, .01f);
var rotated = new RigidPose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));
Check(!WeaponReleaseContacts.Separated(rotated, half, new Vector3(1.4f, 0, 0), new Vector3(1.4f, 0, 0), .15f), "long rotated tip falsely separated");
Check(!WeaponReleaseContacts.Separated(rotated, half, new Vector3(-2, 0, 0), new Vector3(2, 0, 0), .15f), "arm sweep missed");
Check(WeaponReleaseContacts.Separated(rotated, half, new Vector3(0, 1, 0), new Vector3(0, 1, 0), .15f), "clear arm never separated");
Console.WriteLine("PASS long rotated weapon, arm sweep and clear separation");
var pool = new BufferPool();
using (var sim = Simulation.Create(pool, new NoContacts(), new Integrator(), new SolveDescription(4, 1)))
{
    var shape = sim.Shapes.Add(new Capsule(.04f, .3f));
    var h = sim.Bodies.Add(BodyDescription.CreateKinematic(new RigidPose(Vector3.Zero), new CollidableDescription(shape), new BodyActivityDescription(.01f)));
    var body = sim.Bodies.GetBodyReference(h);
    var target = new Vector3(.1f, .2f, 0);
    var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .1f);
    WeaponReleaseContacts.MoveKinematic(body, target, q, 1f / 60);
    Check(body.Pose.Position == Vector3.Zero, "pose teleported before integrating");
    sim.Timestep(1f / 60);
    Check(Vector3.Distance(body.Pose.Position, target) < 1e-5f && MathF.Abs(Quaternion.Dot(body.Pose.Orientation, q)) > .99999f, "target overshot");
    WeaponReleaseContacts.MoveKinematic(body, target, q, 1f / 60); sim.Timestep(1f / 60);
    Check(Vector3.Distance(body.Pose.Position, target) < 1e-5f && body.Velocity.Linear.Length() < 1e-5f, "stationary arm drifts");
    Console.WriteLine("PASS BEPU reaches arm position/rotation once and stops at target");
    var parked = new Vector3(0, -9999, 0);
    WeaponReleaseContacts.MoveKinematic(body, parked, Quaternion.Identity, 1f / 60); sim.Timestep(1f / 60);
    Check(body.Pose.Position == parked && body.Velocity.Linear == Vector3.Zero && body.Velocity.Angular == Vector3.Zero, "parking produced launch velocity");
    Console.WriteLine("PASS parking clears velocity");
}
pool.Clear();

// No external forces/contacts: isolates the runtime movement helper against BEPU integration.
struct Integrator : IPoseIntegratorCallbacks
{
    public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public bool AllowSubstepsForUnconstrainedBodies => false;
    public bool IntegrateVelocityForKinematics => false;
    public void Initialize(Simulation simulation) { }
    public void PrepareForIntegration(float dt) { }
    public void IntegrateVelocity(Vector<int> indices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide inertia,
        Vector<int> mask, int worker, Vector<float> dt, ref BodyVelocityWide velocity) { }
}
struct NoContacts : INarrowPhaseCallbacks
{
    public void Initialize(Simulation simulation) { }
    public bool AllowContactGeneration(int worker, CollidableReference a, CollidableReference b, ref float margin) => false;
    public bool AllowContactGeneration(int worker, CollidablePair pair, int a, int b) => false;
    public bool ConfigureContactManifold<T>(int worker, CollidablePair pair, ref T manifold, out PairMaterialProperties material) where T : unmanaged, IContactManifold<T> { material = default; return false; }
    public bool ConfigureContactManifold(int worker, CollidablePair pair, int a, int b, ref ConvexContactManifold manifold) => false;
    public void Dispose() { }
}
