using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using CombatSimulator.Animation.MeshVolume;

static void Check(bool condition,string reason){if(!condition)throw new Exception(reason);}
long generation=MeshPhysicsDispatcher.Generation;
Check(MeshPhysicsDispatcher.ActiveLeaseCount==0,"Leaked leases before test");
var first=MeshPhysicsDispatcher.Acquire(2);
var second=MeshPhysicsDispatcher.Acquire(4);
Check(MeshPhysicsDispatcher.ActiveLeaseCount==2,"Acquire did not share lifetime");
Check(first.WorkerCount==second.WorkerCount&&first.WorkerCount==MeshPhysicsDispatcher.ClampWorkerCount(2),"A later corpse created a separate worker pool");
Check(MeshPhysicsDispatcher.Generation==generation+1,"Multiple live leases created multiple dispatchers");
first.Dispose();first.Dispose();
Check(MeshPhysicsDispatcher.ActiveLeaseCount==1,"Double disposal changed lease count");
using(var fixture=new Fixture())
{
    bool rejected=false;try{first.Run(fixture.Simulation,1/60f);}catch(ObjectDisposedException){rejected=true;}
    Check(rejected,"Disposed lease was still usable");
    second.Run(fixture.Simulation,1/60f);
}
second.Dispose();
Check(MeshPhysicsDispatcher.ActiveLeaseCount==0&&MeshPhysicsDispatcher.ActiveWorkerCount==0,"Final release retained worker pools");
using(var recreated=MeshPhysicsDispatcher.Acquire(2))
{
    Check(MeshPhysicsDispatcher.Generation==generation+2,"Dispatcher was not recreated after final release");
    using(var fixture=new Fixture())recreated.Run(fixture.Simulation,1/60f);
}

// Independent controller callers may run concurrently, but native dispatch never overlaps.
using(var a=MeshPhysicsDispatcher.Acquire(2))
using(var b=MeshPhysicsDispatcher.Acquire(2))
using(var fa=new Fixture())
using(var fb=new Fixture())
{
    await Task.WhenAll(Task.Run(()=>{for(int i=0;i<120;i++)a.Run(fa.Simulation,1/120f);}),
        Task.Run(()=>{for(int i=0;i<120;i++)b.Run(fb.Simulation,1/120f);})).WaitAsync(TimeSpan.FromSeconds(15));
    fa.Validate();fb.Validate();
}

// A release from a different host waits for the entire native step to finish.
var retained=MeshPhysicsDispatcher.Acquire(2);
using(var entered=new ManualResetEventSlim())
using(var release=new ManualResetEventSlim())
using(var fixture=new Fixture())
{
    fixture.Hooks.BeforeIntegration=()=>{entered.Set();Check(release.Wait(TimeSpan.FromSeconds(5)),"Release test timed out");};
    var running=Task.Run(()=>retained.Run(fixture.Simulation,1/60f));
    Check(entered.Wait(TimeSpan.FromSeconds(5)),"Step never entered integration");
    var disposing=Task.Run(retained.Dispose);
    await Task.Delay(50);
    Check(!disposing.IsCompleted,"Last lease disposed worker pools during a native step");
    release.Set();
    await Task.WhenAll(running,disposing).WaitAsync(TimeSpan.FromSeconds(10));
}
Check(MeshPhysicsDispatcher.ActiveWorkerCount==0,"Joined release failed to dispose workers");

// Host and dedicated worker callbacks must fail before recursively entering the shared gate.
using(var lease=MeshPhysicsDispatcher.Acquire(2))
using(var fixture=new Fixture(512))
{
    int nested=0;int mask=0;
    fixture.Hooks.OnVelocity=worker=>
    {
        _=MeshPhysicsDispatcher.ActiveLeaseCount; // diagnostics are safe on dedicated workers
        Interlocked.Or(ref mask,1<<worker);
        try{lease.Run(fixture.Simulation,1/60f);throw new Exception("Recursive worker dispatch was accepted");}
        catch(InvalidOperationException){Interlocked.Increment(ref nested);}
        try{using var invalid=MeshPhysicsDispatcher.Acquire();throw new Exception("Worker acquired a nested lease");}
        catch(InvalidOperationException){Interlocked.Increment(ref nested);}
        try{lease.Dispose();throw new Exception("Worker released an in-flight lease");}
        catch(InvalidOperationException){Interlocked.Increment(ref nested);}
    };
    await Task.Run(()=>lease.Run(fixture.Simulation,1/60f)).WaitAsync(TimeSpan.FromSeconds(10));
    Check(nested>0,"Reentry fixture never exercised physics callbacks");
    Console.WriteLine($"Reentry rejected on worker mask {mask}; active shared workers={lease.WorkerCount}.");
}
Check(MeshPhysicsDispatcher.ActiveLeaseCount==0&&MeshPhysicsDispatcher.ActiveWorkerCount==0,"Tests leaked shared dispatcher state");
Console.WriteLine("PASS: shared reference counts, idempotent release, recreation, concurrent simulations, in-flight disposal and worker/host reentry rejection.");

sealed class Hooks
{
    public Action? BeforeIntegration;
    public Action<int>? OnVelocity;
}
sealed class Fixture:IDisposable
{
    public readonly BufferPool Pool=new();
    public readonly Hooks Hooks=new();
    public readonly Simulation Simulation;
    private readonly List<BodyHandle> bodies=new();
    public Fixture(int count=24)
    {
        Simulation=Simulation.Create(Pool,new Contacts(),new Gravity(Hooks),new SolveDescription(4,1));
        var sphere=new Sphere(.2f);var shape=Simulation.Shapes.Add(sphere);
        for(int i=0;i<count;i++)bodies.Add(Simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(new Vector3(i*.5f,2,0)),sphere.ComputeInertia(1),new CollidableDescription(shape,.01f),new BodyActivityDescription(.01f))));
        var floor=Simulation.Shapes.Add(new Box(count*.5f+5,1,5));
        Simulation.Statics.Add(new StaticDescription(new Vector3(count*.25f,-.5f,0),floor));
    }
    public void Validate(){foreach(var body in bodies){var pose=Simulation.Bodies.GetBodyReference(body).Pose;CheckFinite(pose.Position);if(pose.Position.Y<.17f)throw new Exception("Concurrent native simulation lost floor collision");}}
    private static void CheckFinite(Vector3 p){if(!float.IsFinite(p.X+p.Y+p.Z))throw new Exception("Nonfinite simulated pose");}
    public void Dispose(){Simulation.Dispose();Pool.Clear();}
}
struct Contacts:INarrowPhaseCallbacks
{
    public void Initialize(Simulation simulation){}
    public bool AllowContactGeneration(int worker,CollidableReference a,CollidableReference b,ref float margin)=>a.Mobility==CollidableMobility.Dynamic||b.Mobility==CollidableMobility.Dynamic;
    public bool AllowContactGeneration(int worker,CollidablePair pair,int a,int b)=>true;
    public bool ConfigureContactManifold<T>(int worker,CollidablePair pair,ref T manifold,out PairMaterialProperties properties)where T:unmanaged,IContactManifold<T>{properties=new(.8f,2,new SpringSettings(30,1));return true;}
    public bool ConfigureContactManifold(int worker,CollidablePair pair,int a,int b,ref ConvexContactManifold manifold)=>true;
    public void Dispose(){}
}
struct Gravity:IPoseIntegratorCallbacks
{
    private Hooks hooks;
    public Gravity(Hooks hooks){this.hooks=hooks;}
    public AngularIntegrationMode AngularIntegrationMode=>AngularIntegrationMode.Nonconserving;
    public bool AllowSubstepsForUnconstrainedBodies=>false;
    public bool IntegrateVelocityForKinematics=>false;
    public void Initialize(Simulation simulation){}
    public void PrepareForIntegration(float dt)=>hooks.BeforeIntegration?.Invoke();
    public void IntegrateVelocity(Vector<int> indices,Vector3Wide position,QuaternionWide orientation,BodyInertiaWide inertia,Vector<int> mask,int worker,Vector<float> dt,ref BodyVelocityWide velocity)
    {hooks.OnVelocity?.Invoke(worker);velocity.Linear.Y-=new Vector<float>(9.81f)*dt;}
}
