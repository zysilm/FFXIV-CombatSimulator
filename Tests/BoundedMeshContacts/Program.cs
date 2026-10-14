using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using CombatSimulator.Animation.MeshVolume;

TerrainRegression.Run();
foreach (var yaw in new[] { 0f, .7f }) Run(yaw, false, false);
Run(.4f, true, false);
Run(.2f, false, true);
Console.WriteLine("PASS: dense concave waist, rotated/ordered source pairs, live refit, narrow foot and high-face terrain preserve real support.");

static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
static Triangle[] Box(Vector3 min, Vector3 max)
{
    var v = new[] { new Vector3(min.X,min.Y,min.Z),new Vector3(max.X,min.Y,min.Z),new Vector3(max.X,max.Y,min.Z),new Vector3(min.X,max.Y,min.Z),new Vector3(min.X,min.Y,max.Z),new Vector3(max.X,min.Y,max.Z),new Vector3(max.X,max.Y,max.Z),new Vector3(min.X,max.Y,max.Z) };
    int[] ix = [0,3,2,0,2,1,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5];
    return Enumerable.Range(0,12).Select(i=>new Triangle(v[ix[i*3]],v[ix[i*3+2]],v[ix[i*3+1]])).ToArray();
}
static IEnumerable<Triangle> Split(Triangle t, int depth)
{
    if(depth==0){yield return t;yield break;}
    var ab=(t.A+t.B)*.5f;var bc=(t.B+t.C)*.5f;var ca=(t.C+t.A)*.5f;
    foreach(var child in new[]{new Triangle(t.A,ab,ca),new Triangle(ab,t.B,bc),new Triangle(ca,bc,t.C),new Triangle(ab,bc,ca)})
        foreach(var leaf in Split(child,depth-1)) yield return leaf;
}
static Mesh MeshOf(BufferPool pool, Triangle[] faces, bool twoSided)
{
    pool.Take<Triangle>(faces.Length*(twoSided?2:1),out var buffer);
    for(int i=0;i<faces.Length;i++){buffer[i*(twoSided?2:1)]=faces[i];if(twoSided)buffer[i*2+1]=new(faces[i].A,faces[i].C,faces[i].B);}
    return new Mesh(buffer,Vector3.One,pool);
}
static void Run(float yaw, bool deform, bool narrow)
{
    var pool=new BufferPool();
    using(var sim=Simulation.Create(pool,new Contacts(),new Gravity(),new SolveDescription(8,4)))
    {
        if (!Environment.GetCommandLineArgs().Contains("--original")) CollisionWorkBudget.Register(sim.NarrowPhase.CollisionTaskRegistry);
        var groundFaces=new[]{new Triangle(new(-5,0,-5),new(5,0,-5),new(-5,0,5)),new Triangle(new(5,0,-5),new(5,0,5),new(-5,0,5))}.SelectMany(t=>Split(t,4)).ToArray();
        var floor=MeshOf(pool,groundFaces,false);var floorIndex=sim.Shapes.Add(floor);
        if(Environment.GetCommandLineArgs().Contains("--solid-terrain")||Environment.GetCommandLineArgs().Contains("--triangle-terrain"))TerrainCollisionSurface.Add(sim,pool,groundFaces);
        else sim.Statics.Add(new StaticDescription(Vector3.Zero,floorIndex));
        var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,yaw);
        if(!narrow)
        {
            var pedestalFaces=Box(new(.02f,0,-.2f),new(.32f,.25f,.2f)).SelectMany(t=>Split(t,2)).ToArray();
            var pedestal=MeshOf(pool,pedestalFaces,false);
            sim.Statics.Add(new StaticDescription(Vector3.Zero,rotation,sim.Shapes.Add(pedestal)));
        }
        var original=narrow?Box(new(-.015f,-.2f,-.15f),new(.015f,.15f,.15f)).Concat(Box(new(-.4f,.15f,-.3f),new(.4f,.3f,.3f))):
            Box(new(-.33f,-.2f,-.3f),new(-.13f,.15f,.3f)).Concat(Box(new(.47f,-.2f,-.3f),new(.67f,.15f,.3f))).Concat(Box(new(-.33f,.15f,-.3f),new(.67f,.3f,.3f)));
        var faces=original.SelectMany(t=>Split(t,3)).ToArray();
        var mesh=MeshOf(pool,faces,true);var inertia=mesh.ComputeOpenInertia(1,out var center);
        // Hold the anatomical frame, as neighboring spine joints do in a full
        // ragdoll. This fixture isolates support height from a free arch tipping.
        inertia.InverseInertiaTensor=default;
        var shape=sim.Shapes.Add(mesh);
        var bodyHandle=sim.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(new Vector3(0,.8f,0)+Vector3.Transform(center,rotation),rotation),inertia,new CollidableDescription(shape,.005f),new BodyActivityDescription(.005f)));
        for(int frame=0;frame<600;frame++)
        {
            if(deform&&frame==240)
            {
                var deformation=Environment.GetCommandLineArgs().Contains("--deform-down")?-.08f:.02f;
                var vertices=faces.SelectMany(t=>new[]{t.A,t.B,t.C}).Select(p=>new Vector3(p.X,p.Y<-.19f?p.Y+deformation:p.Y,p.Z)-center).ToArray();
                ref var changing=ref sim.Shapes.GetShape<Mesh>(shape.Index);
                Check(MeshSurfaceRefitter.Refit(ref changing,vertices,Enumerable.Range(0,vertices.Length).ToArray()),"live collision refit rejected");
                var awake=sim.Bodies.GetBodyReference(bodyHandle);awake.Awake=true;
                awake.UpdateBounds();
            }
            sim.Timestep(1/120f);
        }
        var body=sim.Bodies.GetBodyReference(bodyHandle);
        var bone=body.Pose.Position-Vector3.Transform(center,body.Pose.Orientation);
        Console.WriteLine($"dense support yaw={yaw} deform={deform} thin={narrow} bone={bone}, q={body.Pose.Orientation}, speed={body.Velocity.Linear.Length():F5}");
        var target=deform?(Environment.GetCommandLineArgs().Contains("--deform-down")?.28f:.18f):.2f;
        Check(MathF.Abs(bone.Y-target)<.025f,"dense surface invented support in the waist gap or fell through terrain");
        Check(body.Velocity.Linear.Length()<.03f,"dense mesh failed to settle");
        var candidates=new CollisionWorkBudget.FacePair[CollisionWorkBudget.MaximumChildren];
        ref var actual=ref sim.Shapes.GetShape<Mesh>(shape.Index);ref var terrain=ref sim.Shapes.GetShape<Mesh>(floorIndex.Index);
        var forward=new BoundsTestedPair{OrientationA=body.Pose.Orientation,OrientationB=Quaternion.Identity,OffsetB=-body.Pose.Position,SpeculativeMargin=.01f};
        var reverse=new BoundsTestedPair{OrientationA=Quaternion.Identity,OrientationB=body.Pose.Orientation,OffsetB=body.Pose.Position,SpeculativeMargin=.01f};
        Check(CollisionWorkBudget.Collect(ref actual,ref terrain,forward,1/120f,candidates,out _,out _)>0,"body/terrain order lost support candidates");
        Check(CollisionWorkBudget.Collect(ref terrain,ref actual,reverse,1/120f,candidates,out _,out _)>0,"terrain/body order lost support candidates");
        var expanded=forward;expanded.MaximumExpansion=100;expanded.SpeculativeMargin=2;expanded.RelativeLinearVelocityA=new Vector3(16,-16,8);expanded.AngularVelocityA=new Vector3(10,3,4);
        int expandedCount=CollisionWorkBudget.Collect(ref actual,ref terrain,expanded,1/60f,candidates,out _,out _);
        Check(expandedCount<=CollisionWorkBudget.MaximumChildren,"large swept window exceeded preallocation work budget");
        for(int i=0;i<expandedCount;i++)Check((uint)candidates[i].A<actual.Triangles.Length&&(uint)candidates[i].B<terrain.Triangles.Length,"budget substituted a fabricated collision face");
        foreach(var quality in new[]{-100,0,50,100,1000})
        {
            var count=CollisionWorkBudget.Collect(ref actual,ref terrain,expanded,1/60f,candidates,out _,out _,quality);
            Check(count<=CollisionWorkBudget.MaximumChildren,"quality setting exceeded continuation storage");
            for(int i=0;i<count;i++)Check((uint)candidates[i].A<actual.Triangles.Length&&(uint)candidates[i].B<terrain.Triangles.Length,"quality setting replaced an original face");
        }
    }
    pool.Clear();
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
    public AngularIntegrationMode AngularIntegrationMode=>AngularIntegrationMode.Nonconserving;
    public bool AllowSubstepsForUnconstrainedBodies=>false;
    public bool IntegrateVelocityForKinematics=>false;
    public void Initialize(Simulation simulation){}
    public void PrepareForIntegration(float dt){}
    public void IntegrateVelocity(Vector<int> indices,Vector3Wide position,QuaternionWide orientation,BodyInertiaWide inertia,Vector<int> mask,int worker,Vector<float> dt,ref BodyVelocityWide velocity)=>velocity.Linear.Y-=new Vector<float>(9.81f)*dt;
}
