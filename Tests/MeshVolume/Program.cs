using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using CombatSimulator.Animation.MeshVolume;
using CombatSimulator.Animation.SurfaceProfiles;

var failed = 0;
foreach (var (name, run) in new (string, Action)[] {
    ("mesh inertia recenters asymmetric geometry", Inertia),
    ("bone frame roundtrip preserves baked nonuniform scale", FrameRoundtrip),
    ("dynamic mesh lands on mesh terrain", ()=>Contact(false)),
    ("concave offset waist clears raised support without convex air cushion", ()=>Concavity(false)),
    ("articulated dynamic mesh bodies retain joints on mesh terrain", ()=>Articulation(false)),
    ("two sided mesh lands on mesh terrain", ()=>Contact(true)),
    ("two sided mesh preserves concavity", ()=>Concavity(true)),
    ("two sided articulated mesh settles", ()=>Articulation(true)),
    ("production partition preserves area, owner, winding, and ties", Partition),
    ("production sphere support preserves slopes, edges, and gaps", Support),
    ("runtime queries retain actual mesh and pose", SurfaceRuntime),
    ("runtime sleep cache refreshes mutable mesh at unchanged pose", SurfaceRevision),
    ("live mixed skin mesh collision settles and allocates no refit memory", LiveMeshPhysics.Run),
}) {
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch(Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e}"); }
}
if(args.Contains("--benchmark")) Benchmark();
return failed == 0 ? 0 : 1;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Benchmark() {
    const int count=20000;
    var vertices=new MeshWeightedVertex[count*3];var indices=new int[count*3];
    for(var i=0;i<count;i++) for(var j=0;j<3;j++) {
        var position=new Vector3(i%200+(j==1?1:0),0,i/200+(j==2?1:0));
        vertices[i*3+j]=new(position,[new(0,j==0?.8f:.1f),new(1,j==1?.8f:.1f),new(2,j==2?.8f:.1f)]);indices[i*3+j]=i*3+j;
    }
    BodyMeshPartitioner.Partition(vertices.Take(30).ToArray(),indices.Take(30).ToArray());
    var before=GC.GetAllocatedBytesForCurrentThread();var timer=System.Diagnostics.Stopwatch.StartNew();
    var parts=BodyMeshPartitioner.Partition(vertices,indices);timer.Stop();
    var bytes=GC.GetAllocatedBytesForCurrentThread()-before;
    Console.WriteLine($"BENCH partition {count} faces -> {parts.Sum(p=>p.Value.Indices.Length/3)} faces: {timer.Elapsed.TotalMilliseconds:F1} ms, {bytes/1048576d:F1} MiB allocated; excludes native Mesh trees and query world arrays");
}
static Mesh MakeMesh(BufferPool pool, Triangle[] triangles) {
    pool.Take<Triangle>(triangles.Length, out var buffer);
    for(var i=0;i<triangles.Length;i++) buffer[i]=triangles[i];
    return new Mesh(buffer, Vector3.One, pool);
}
static Triangle[] TwoSided(Triangle[] source)=>source.Concat(source.Select(t=>new Triangle(t.A,t.C,t.B))).ToArray();
// Clockwise from outside, matching BEPU's one sided surface convention.
static Triangle[] Box(Vector3 min, Vector3 max) {
    var v = new[]{new Vector3(min.X,min.Y,min.Z),new Vector3(max.X,min.Y,min.Z),new Vector3(max.X,max.Y,min.Z),new Vector3(min.X,max.Y,min.Z),new Vector3(min.X,min.Y,max.Z),new Vector3(max.X,min.Y,max.Z),new Vector3(max.X,max.Y,max.Z),new Vector3(min.X,max.Y,max.Z)};
    int[] outwardCounterClockwise = [0,3,2,0,2,1,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5];
    return Enumerable.Range(0,12).Select(i=>new Triangle(v[outwardCounterClockwise[i*3]],v[outwardCounterClockwise[i*3+2]],v[outwardCounterClockwise[i*3+1]])).ToArray();
}
static void Inertia() {
    var pool=new BufferPool();
    var mesh=MakeMesh(pool,Box(new(0.1f,-0.2f,-0.3f),new(0.7f,0.4f,0.3f)));
    var closed=mesh.ComputeClosedInertia(2,out var center);
    Assert(Vector3.Distance(center,new(.4f,.1f,0))<1e-5f,$"closed center {center}");
    Assert(MathF.Abs(closed.InverseInertiaTensor.XX-8.333333f)<1e-3f,$"cube inertia {closed.InverseInertiaTensor.XX}");
    mesh.Dispose(pool);
    mesh=MakeMesh(pool,Box(new(0.1f,-0.2f,-0.3f),new(0.7f,0.4f,0.3f)).Take(10).ToArray());
    var open=mesh.ComputeOpenInertia(2,out center);
    Assert(float.IsFinite(open.InverseInertiaTensor.XX)&&open.InverseInertiaTensor.XX>0,"open inertia invalid");
    Assert(center.X>0.1f&&center.X<0.7f,$"open center {center}");
    Console.WriteLine($"  open surface center={center}; inertia inverse={open.InverseInertiaTensor.XX}");
    mesh.Dispose(pool); pool.Clear();
}
static void FrameRoundtrip() {
    var boneOrientation=Quaternion.CreateFromYawPitchRoll(.43f,-.8f,.27f);
    var bonePosition=new Vector3(5,3,-2);
    var bodyInBone=Quaternion.CreateFromYawPitchRoll(-.31f,.1f,.7f);
    var centerInBone=new Vector3(.19f,-.07f,.11f);
    var bodyOrientation=Quaternion.Normalize(boneOrientation*bodyInBone);
    var bodyPosition=bonePosition+Vector3.Transform(centerInBone,boneOrientation);
    var recoveredOrientation=Quaternion.Normalize(bodyOrientation*Quaternion.Conjugate(bodyInBone));
    var recoveredPosition=BodyMeshGeometry.BoneOrigin(bodyPosition,bodyOrientation,Vector3.Transform(centerInBone,Quaternion.Conjugate(bodyInBone)));
    Assert(Vector3.Distance(recoveredPosition,bonePosition)<1e-5f,"bone translation lost");
    Assert(MathF.Abs(Quaternion.Dot(recoveredOrientation,boneOrientation))>.99999f,"bone rotation lost");
    var nonuniformScale=new Vector3(1.3f,.7f,1.8f);
    foreach(var v in new[]{new Vector3(-.3f,.2f,.1f),new Vector3(.8f,-.1f,-.4f)}) {
        var world=bonePosition+Vector3.Transform(v*nonuniformScale,boneOrientation);
        var bodyLocal=Vector3.Transform(world-bodyPosition,Quaternion.Conjugate(bodyOrientation));
        var reconstructed=bodyPosition+Vector3.Transform(bodyLocal,bodyOrientation);
        Assert(Vector3.Distance(world,reconstructed)<1e-5f,"baked scale shifted geometry");
    }
}
static void Partition() {
    var random=new Random(7819);
    for(var trial=0;trial<100;trial++) {
        var positions=new[]{Vector3.Zero,new Vector3(2,0,0),new Vector3(0,0,2)};
        var weights=Enumerable.Range(0,3).Select(_=>Enumerable.Range(0,3).Select(b=>new MeshBodyWeight(b,(float)random.NextDouble())).ToArray()).ToArray();
        var parts=BodyMeshPartitioner.Partition(Enumerable.Range(0,3).Select(i=>new MeshWeightedVertex(positions[i],weights[i])).ToArray(),[0,1,2]);
        var totalArea=0f;
        foreach(var (owner,part) in parts) {
          Assert(part.SourceBindings?.Length==part.Vertices.Length,"partition lost source deformation bindings");
          for(var vi=0;vi<part.Vertices.Length;vi++) {
            var binding=part.SourceBindings![vi];var bary=binding.Barycentric;
            Assert(MathF.Abs(bary.X+bary.Y+bary.Z-1)<1e-5f&&bary.X>=-1e-6f&&bary.Y>=-1e-6f&&bary.Z>=-1e-6f,"invalid barycentric source binding");
            var reconstructed=positions[binding.A]*bary.X+positions[binding.B]*bary.Y+positions[binding.C]*bary.Z;
            Assert(Vector3.Distance(reconstructed,part.Vertices[vi])<1e-5f,"source binding changed captured surface");
          }
          for(var i=0;i<part.Indices.Length;i+=3) {
            var a=part.Vertices[part.Indices[i]];var b=part.Vertices[part.Indices[i+1]];var c=part.Vertices[part.Indices[i+2]];
            var normal=Vector3.Cross(b-a,c-a);totalArea+=normal.Length()*.5f;
            Assert(normal.Y<0,"partition winding reversed");
            var centroid=(a+b+c)/3;
            float Weight(int body)=>weights[0][body].Weight*(1-centroid.X/2-centroid.Z/2)+weights[1][body].Weight*centroid.X/2+weights[2][body].Weight*centroid.Z/2;
            for(var other=0;other<3;other++) Assert(Weight(owner)>=Weight(other)-1e-5f,"partition overlaps a stronger owner");
          }
        }
        Assert(MathF.Abs(totalArea-2)<1e-5f,$"surface area changed: {totalArea}");
    }
    var tied=new[]{new MeshWeightedVertex(Vector3.Zero,[new(8,.5f),new(2,.5f)]),new MeshWeightedVertex(Vector3.UnitX,[new(8,.5f),new(2,.5f)]),new MeshWeightedVertex(Vector3.UnitZ,[new(8,.5f),new(2,.5f)])};
    var tie=BodyMeshPartitioner.Partition(tied,[0,1,2]);
    Assert(tie.Count==1&&tie.ContainsKey(2),"equal weights duplicate or lose source surface");
    var empty=BodyMeshPartitioner.Partition(tied.Select(v=>new MeshWeightedVertex(v.Position,[new(0,0),new(1,float.NaN)])).ToArray(),[0,1,2]);
    Assert(empty.Count==0,"zero/nonfinite influences fabricated geometry");
    var split=new[]{new MeshWeightedVertex(Vector3.Zero,[new(0,1)]),new MeshWeightedVertex(Vector3.UnitX,[new(1,1)]),new MeshWeightedVertex(Vector3.UnitZ,[new(2,1)])};
    var budgetRejected=false;
    try { BodyMeshPartitioner.Partition(split,[0,1,2],maximumOutputTriangles:5); }
    catch(InvalidOperationException) { budgetRejected=true; }
    Assert(budgetRejected,"expanded face budget did not reject excessive output");
    Assert(BodyMeshPartitioner.Partition(split,[0,1,2],maximumOutputTriangles:6).Sum(p=>p.Value.Indices.Length/3)==6,"budget rejected exact boundary or silently decimated output");
}
static void Support() {
    Assert(BodyMeshGeometry.TrySphereSupport(Vector3.Zero,1,new(-10,-5,-10),new(10,5,-10),new(0,0,10),out var slope),"slope missing");
    Assert(MathF.Abs(slope-MathF.Sqrt(1.25f))<1e-5f,$"slope support {slope}");
    Assert(BodyMeshGeometry.TrySphereSupport(new(1,100,-.3f),.5f,Vector3.Zero,new(2,0,0),new(0,0,2),out var edge),"edge missing");
    Assert(MathF.Abs(edge-.4f)<1e-5f,$"edge support {edge}");
    Assert(!BodyMeshGeometry.TrySphereSupport(new(1,0,-.6f),.5f,Vector3.Zero,new(2,0,0),new(0,0,2),out _),"invented edge outside sphere");
    Assert(!BodyMeshGeometry.TrySphereSupport(Vector3.Zero,.1f,new(-1,0,-1),new(-.3f,0,-1),new(-.3f,0,1),out _),"left surface filled concavity");
    Assert(!BodyMeshGeometry.TrySphereSupport(Vector3.Zero,.1f,new(.3f,0,-1),new(1,0,-1),new(.3f,0,1),out _),"right surface filled concavity");
    Assert(BodyMeshGeometry.TrySphereSupport(new(-.3f,0,-.4f),1,Vector3.Zero,new(2,0,0),new(0,0,2),out var corner),"vertex support missing");
    Assert(MathF.Abs(corner-MathF.Sqrt(.75f))<1e-5f,$"vertex support {corner}");
    var random=new Random(31415);
    for(var trial=0;trial<40;trial++) {
        var a=new Vector3(0,(float)random.NextDouble()*.5f,0);
        var b=new Vector3(2,(float)random.NextDouble()*.5f,0);
        var c=new Vector3(0,(float)random.NextDouble()*.5f,2);
        var center=new Vector3((float)random.NextDouble()*2.5f-.25f,0,(float)random.NextDouble()*2.5f-.25f);
        const float radius=.6f;
        var sampled=float.MinValue;
        for(var i=0;i<=180;i++) for(var j=0;j<=180-i;j++) {
            var p=a+(b-a)*(i/180f)+(c-a)*(j/180f);
            var dx=p.X-center.X;var dz=p.Z-center.Z;
            var riseSq=radius*radius-dx*dx-dz*dz;
            if(riseSq>=radius*radius*.28f*.28f) sampled=MathF.Max(sampled,p.Y+MathF.Sqrt(riseSq));
        }
        var found=BodyMeshGeometry.TrySphereSupport(center,radius,a,b,c,out var analytic);
        if(sampled!=float.MinValue) {
            Assert(found,"analytic sphere query missed sampled support");
            Assert(analytic>=sampled-1e-5f,$"analytic support under sample {analytic}/{sampled}");
            Assert(analytic<sampled+.02f,$"analytic support invented height {analytic}/{sampled}");
        }
    }
}
static void SurfaceRuntime() {
    var local=new[]{new Vector3(.2f,0,-1),new Vector3(1.2f,.5f,-1),new Vector3(.7f,.25f,1)};
    var mesh=new BodyMeshGeometry(local,[0,1,2]);
    var profile=new CharacterSurfaceProfile();
    var runtime=new CharacterSurfaceRuntime(profile,default,[new("waist","torso",4,4,4,Mesh:mesh)]);
    var position=new Vector3(2,3,-4);var orientation=Quaternion.CreateFromYawPitchRoll(.4f,.3f,-.7f);
    runtime.UpdateBonePose("waist",position,orientation);
    foreach(var usage in Enum.GetValues<CharacterSurfaceUsage>()) {
        var debug=runtime.GetDebugBones(usage).Single();
        Assert(debug.Indices.Span.SequenceEqual(mesh.Indices),"runtime replaced source mesh topology");
        for(var i=0;i<local.Length;i++) Assert(Vector3.Distance(debug.Vertices.Span[i],position+Vector3.Transform(local[i],orientation))<1e-5f,$"{usage} debug surface differs from actual collision mesh");
    }
    var localPoint=(local[0]+local[1]+local[2])/3;
    var point=position+Vector3.Transform(localPoint,orientation);
    var normal=Vector3.Normalize(Vector3.Transform(Vector3.Cross(local[1]-local[0],local[2]-local[0]),orientation));
    Assert(runtime.TryGetNearestSurface("waist",point+normal*.1f,CharacterSurfaceUsage.Grab,out var hit),"nearest actual mesh missing");
    Assert(Vector3.Distance(point,hit.Point)<1e-5f,"nearest surface offset");
    Assert(Vector3.Distance(localPoint,hit.BodyLocalPoint)<1e-5f,"surface local point frame wrong");
    runtime.UpdateBonePose("waist",Vector3.Zero,Quaternion.Identity);
    Assert(runtime.TryGetVerticalSupport(new(.7f,10,0),.1f,"waist",out var y,out var owner),"actual slope support missing");
    Assert(MathF.Abs(y-(.25f+.1f*MathF.Sqrt(1.25f)))<1e-5f,$"runtime slope support differs from actual mesh {y}");
    Assert(owner=="waist","support owner lost");
    var fineMesh=new BodyMeshGeometry([Vector3.Zero,new(.001f,0,0),new(0,0,.001f)],[0,1,2]);
    var fine=new CharacterSurfaceRuntime(profile,default,[new("finger","finger",1,1,1,Mesh:fineMesh)]);
    fine.UpdateBonePose("finger",Vector3.Zero,Quaternion.Identity);
    Assert(fine.TryGetNearestSurface("finger",new(.0002f,.001f,.0002f),CharacterSurfaceUsage.Grab,out var fineHit),"valid millimetre mesh face was treated as degenerate");
    Assert(Vector3.Distance(fineHit.Point,new(.0002f,0,.0002f))<1e-6f,"fine mesh nearest point inaccurate");
}
static void SurfaceRevision() {
    var mesh=new BodyMeshGeometry([Vector3.Zero,new(2,0,0),new(0,0,2)],[0,1,2]);
    var runtime=new CharacterSurfaceRuntime(new CharacterSurfaceProfile(),default,[new("waist","torso",1,1,1,Mesh:mesh)]);
    var position=new Vector3(2,3,-1);var rotation=Quaternion.Identity;
    runtime.UpdateBonePose("waist",position,rotation);
    for(var i=0;i<100;i++)runtime.UpdateBonePose("waist",position,rotation);
    var before=GC.GetAllocatedBytesForCurrentThread();
    for(var i=0;i<100;i++)runtime.UpdateBonePose("waist",position,rotation);
    Assert(GC.GetAllocatedBytesForCurrentThread()==before,"unchanged surface pose cache allocated");
    for(var i=0;i<mesh.Vertices.Length;i++)mesh.Vertices[i]+=new Vector3(4,.4f,0);
    mesh.MarkChanged();
    runtime.UpdateBonePose("waist",position,rotation);
    foreach(var usage in Enum.GetValues<CharacterSurfaceUsage>()) {
        var debug=runtime.GetDebugBones(usage).Single();
        for(var i=0;i<mesh.Vertices.Length;i++) Assert(Vector3.Distance(debug.Vertices.Span[i],position+mesh.Vertices[i])<1e-6f,$"revision did not invalidate {usage} world vertices");
    }
    var expected=position+new Vector3(4.5f,.4f,.5f);
    Assert(runtime.TryGetNearestSurface(expected+Vector3.UnitY*.1f,CharacterSurfaceUsage.Grab,.2f,out var hit),"revision did not refresh nearest-surface bounds");
    Assert(Vector3.Distance(hit.Point,expected)<1e-6f,"nearest surface retained old cached mesh");
    Assert(runtime.TryGetVerticalSupport(expected,.2f,"waist",out var y,out _),"revision did not refresh support bounds");
    Assert(MathF.Abs(y-3.6f)<1e-5f,$"support retained old cached mesh {y}");
}
static void Contact(bool doubleSided) {
    var pool=new BufferPool();
    using(var simulation=Simulation.Create(pool,new ContactCallbacks(),new GravityCallbacks(),new SolveDescription(8,4))) {
        var terrain=MakeMesh(pool,[new(new(-5,0,-5),new(5,0,-5),new(-5,0,5)),new(new(5,0,-5),new(5,0,5),new(-5,0,5))]);
        var terrainShape=simulation.Shapes.Add(terrain);
        simulation.Statics.Add(new StaticDescription(Vector3.Zero,terrainShape));
        var triangles=Box(new(-.3f,-.2f,-.3f),new(.3f,.2f,.3f));
        var mesh=MakeMesh(pool,doubleSided?TwoSided(triangles):triangles);
        var inertia=doubleSided?mesh.ComputeOpenInertia(1,out var center):mesh.ComputeClosedInertia(1,out center);
        var shape=simulation.Shapes.Add(mesh);
        var handle=simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(new Vector3(0,1,0)+center),inertia,new CollidableDescription(shape,.01f),new BodyActivityDescription(.01f)));
        for(var i=0;i<600;i++) simulation.Timestep(1/120f);
        var body=simulation.Bodies.GetBodyReference(handle);
        Console.WriteLine($"  final mesh body y={body.Pose.Position.Y}; velocity={body.Velocity.Linear}");
        Assert(body.Pose.Position.Y>.17f&&body.Pose.Position.Y<.24f,"mesh terrain contact failed");
    }
    pool.Clear();
}
static void Concavity(bool doubleSided) {
    var pool=new BufferPool();
    using(var simulation=Simulation.Create(pool,new ContactCallbacks(),new GravityCallbacks(),new SolveDescription(12,4))) {
        var floor=MakeMesh(pool,[new(new(-5,0,-5),new(5,0,-5),new(-5,0,5)),new(new(5,0,-5),new(5,0,5),new(-5,0,5))]);
        simulation.Statics.Add(new StaticDescription(Vector3.Zero,simulation.Shapes.Add(floor)));
        var pedestal=MakeMesh(pool,Box(new(.02f,0,-.2f),new(.32f,.25f,.2f)));
        simulation.Statics.Add(new StaticDescription(Vector3.Zero,simulation.Shapes.Add(pedestal)));
        // Three solid surface sections leave a 0.6 m wide recess below the waist.
        // A convex proxy fills this recess and rests on the pedestal 0.25 m too high.
        var geometry=Box(new(-.33f,-.2f,-.3f),new(-.13f,.15f,.3f))
            .Concat(Box(new(.47f,-.2f,-.3f),new(.67f,.15f,.3f)))
            .Concat(Box(new(-.33f,.15f,-.3f),new(.67f,.3f,.3f))).ToArray();
        var mesh=MakeMesh(pool,doubleSided?TwoSided(geometry):geometry);
        var inertia=mesh.ComputeOpenInertia(1,out var center);
        var shape=simulation.Shapes.Add(mesh);
        var handle=simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(new Vector3(0,.8f,0)+center),inertia,new CollidableDescription(shape,.005f),new BodyActivityDescription(.005f)));
        for(var i=0;i<1200;i++) simulation.Timestep(1/120f);
        var body=simulation.Bodies.GetBodyReference(handle);
        var recoveredBone=body.Pose.Position-Vector3.Transform(center,body.Pose.Orientation);
        Console.WriteLine($"  concave body bone={recoveredBone}; center={center}; orientation={body.Pose.Orientation}");
        Assert(MathF.Abs(recoveredBone.Y-.2f)<.025f,$"false support under concave waist: bone y={recoveredBone.Y}");
        Assert(MathF.Abs(recoveredBone.X)<.03f,"offset center moved bone frame");
        Assert(body.Velocity.Linear.Length()<.01f,"concave body does not settle");
    }
    pool.Clear();
}
static void Articulation(bool doubleSided) {
    var pool=new BufferPool();
    using(var simulation=Simulation.Create(pool,new ContactCallbacks(),new GravityCallbacks(),new SolveDescription(12,4))) {
        var floor=MakeMesh(pool,[new(new(-5,0,-5),new(5,0,-5),new(-5,0,5)),new(new(5,0,-5),new(5,0,5),new(-5,0,5))]);
        simulation.Statics.Add(new StaticDescription(Vector3.Zero,simulation.Shapes.Add(floor)));
        BodyHandle Add(Vector3 position) {
            var triangles=Box(new(-.3f,-.2f,-.25f),new(.3f,.2f,.25f));
            var mesh=MakeMesh(pool,doubleSided?TwoSided(triangles):triangles);
            var inertia=mesh.ComputeOpenInertia(1,out var center);
            return simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(position+center),inertia,new CollidableDescription(simulation.Shapes.Add(mesh),.005f),new BodyActivityDescription(.005f)));
        }
        var a=Add(new(-.4f,1.4f,0)); var b=Add(new(.4f,1.6f,0));
        var offsetA=new Vector3(.4f,.1f,0); var offsetB=new Vector3(-.4f,-.1f,0);
        simulation.Solver.Add(a,b,new BallSocket {LocalOffsetA=offsetA,LocalOffsetB=offsetB,SpringSettings=new SpringSettings(30,1)});
        for(var i=0;i<1200;i++) simulation.Timestep(1/120f);
        var ar=simulation.Bodies.GetBodyReference(a); var br=simulation.Bodies.GetBodyReference(b);
        var anchorA=ar.Pose.Position+Vector3.Transform(offsetA,ar.Pose.Orientation);
        var anchorB=br.Pose.Position+Vector3.Transform(offsetB,br.Pose.Orientation);
        Assert(Vector3.Distance(anchorA,anchorB)<.01f,$"joint separation {Vector3.Distance(anchorA,anchorB)}");
        Assert(ar.Pose.Position.Y>0&&br.Pose.Position.Y>0,"articulated bodies fell through mesh terrain");
        Assert(ar.Velocity.Linear.Length()<.03f&&br.Velocity.Linear.Length()<.03f,"articulated body failed to settle");
        Console.WriteLine($"  articulated joint error={Vector3.Distance(anchorA,anchorB)}; y={ar.Pose.Position.Y}/{br.Pose.Position.Y}");
    }
    pool.Clear();
}
struct ContactCallbacks:INarrowPhaseCallbacks {
    public bool OnlyStatics;
    public void Initialize(Simulation simulation){}
    public bool AllowContactGeneration(int workerIndex,CollidableReference a,CollidableReference b,ref float speculativeMargin)=>(a.Mobility==CollidableMobility.Dynamic||b.Mobility==CollidableMobility.Dynamic)&&(!OnlyStatics||a.Mobility==CollidableMobility.Static||b.Mobility==CollidableMobility.Static);
    public bool AllowContactGeneration(int workerIndex,CollidablePair pair,int childIndexA,int childIndexB)=>true;
    public bool ConfigureContactManifold<T>(int workerIndex,CollidablePair pair,ref T manifold,out PairMaterialProperties material) where T:unmanaged,IContactManifold<T> { material=new PairMaterialProperties(0.8f,2,new SpringSettings(30,1));return true; }
    public bool ConfigureContactManifold(int workerIndex,CollidablePair pair,int childIndexA,int childIndexB,ref ConvexContactManifold manifold)=>true;
    public void Dispose(){}
}
struct GravityCallbacks:IPoseIntegratorCallbacks {
    public AngularIntegrationMode AngularIntegrationMode=>AngularIntegrationMode.Nonconserving;
    public bool AllowSubstepsForUnconstrainedBodies=>false;
    public bool IntegrateVelocityForKinematics=>false;
    public void Initialize(Simulation simulation){}
    public void PrepareForIntegration(float dt){}
    public void IntegrateVelocity(Vector<int> bodyIndices,Vector3Wide position,QuaternionWide orientation,BodyInertiaWide localInertia,Vector<int> integrationMask,int workerIndex,Vector<float> dt,ref BodyVelocityWide velocity) { velocity.Linear.Y-=new Vector<float>(9.81f)*dt; }
}
