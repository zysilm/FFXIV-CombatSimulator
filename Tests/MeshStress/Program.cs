using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using CombatSimulator.Animation.MeshVolume;

if (!args.Contains("--child")) return await Supervise();
return RunChild();

int Number(string key,int fallback) { var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?int.Parse(args[i+1]):fallback; }
string Option(string key,string fallback) { var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback; }
void Emit(object value)=>Console.WriteLine(JsonSerializer.Serialize(value));
async Task<int> Supervise() {
    var limit=(long)Number("--rss-mib",512)*1048576;var deadline=Number("--seconds",30);
    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
    // dotnet <dll> is also supported when no apphost was used.
    if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)!.Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(Watch).Assembly.Location);
    foreach(var arg in args)start.ArgumentList.Add(arg);start.ArgumentList.Add("--child");
    using var child=Process.Start(start)!;
    using var memoryLimit=OperatingSystem.IsWindows()?ChildMemoryLimit.Attach(child,limit):null;
    var output=Task.Run(async()=>{while(await child.StandardOutput.ReadLineAsync() is {} line)Console.WriteLine(line);});
    var error=Task.Run(async()=>{while(await child.StandardError.ReadLineAsync() is {} line)Console.Error.WriteLine(line);});
    var timer=Stopwatch.StartNew();long peak=0;string? stopped=null;
    while(!child.HasExited) {
        child.Refresh();peak=Math.Max(peak,child.WorkingSet64);
        if(child.WorkingSet64>limit){stopped="rss_limit";child.Kill(true);break;}
        if(timer.Elapsed.TotalSeconds>deadline){stopped="wall_timeout";child.Kill(true);break;}
        await Task.Delay(25);
    }
    await child.WaitForExitAsync();await Task.WhenAll(output,error);
    Emit(new{kind="supervisor",exit=child.ExitCode,stopped,peakRssMiB=peak/1048576d,elapsedSeconds=timer.Elapsed.TotalSeconds,limitMiB=limit/1048576});
    return stopped==null?child.ExitCode:3;
}
int RunChild() {
    if(Option("--fixture","stress")=="concave")return ConcavityRegression.Run();
    var policy=Option("--policy","all");
    var pool=new BufferPool();var watch=new Watch{PairBudget=long.Parse(Option("--pair-budget",policy.StartsWith("bounded")?"30000000":"5000000")),FrameBudget=long.Parse(Option("--frame-budget",policy.StartsWith("bounded")?"20000000000":"100000000")),GroundOnly=policy.EndsWith("ground-only")};
    var frames=Number("--steps",120);var build=Stopwatch.StartNew();
    try {
        var iterations=Number("--iterations",4);var substeps=Number("--substeps",16);
        var threadCount=Number("--threads",1);
        var sharedDispatcher=args.Contains("--shared-dispatcher");
        using var sharedLease=sharedDispatcher&&threadCount>1?MeshPhysicsDispatcher.Acquire(threadCount):null;
        using var threadDispatcher=!sharedDispatcher&&threadCount>1?new ThreadDispatcher(threadCount):null;
        var timestepper=new DefaultTimestepper();long stageStart=0;
        timestepper.BeforeCollisionDetection+=(dt,dispatcher)=>stageStart=Stopwatch.GetTimestamp();
        timestepper.CollisionsDetected+=(dt,dispatcher)=>{var now=Stopwatch.GetTimestamp();watch.CollisionMs=(now-stageStart)*1000d/Stopwatch.Frequency;stageStart=now;};
        timestepper.ConstraintsSolved+=(dt,dispatcher)=>watch.SolverMs=(Stopwatch.GetTimestamp()-stageStart)*1000d/Stopwatch.Frequency;
        using var simulation=Simulation.Create(pool,new Contacts{Watch=watch},new Gravity(),new SolveDescription(iterations,substeps),timestepper);
        var contactQuality=Number("--quality",50);
        if(policy.StartsWith("bounded"))CollisionWorkBudget.Register(simulation.NarrowPhase.CollisionTaskRegistry,()=>contactQuality);
        Mesh Make(Triangle[] source){pool.Take<Triangle>(source.Length,out var data);for(var i=0;i<source.Length;i++)data[i]=source[i];return new Mesh(data,Vector3.One,pool);}
        Mesh MakeGeometry(BodyMeshGeometry geometry){var triangles=new Triangle[geometry.Indices.Length/3*2];for(int i=0;i<geometry.Indices.Length;i+=3){var a=geometry.Vertices[geometry.Indices[i]];var b=geometry.Vertices[geometry.Indices[i+1]];var c=geometry.Vertices[geometry.Indices[i+2]];triangles[i/3*2]=new(a,b,c);triangles[i/3*2+1]=new(a,c,b);}return Make(triangles);}
        var terrainGrid=Number("--terrain-grid",0);
        var terrainFaces=new List<Triangle>();
        if(terrainGrid>0)for(var z=0;z<terrainGrid;z++)for(var x=0;x<terrainGrid;x++){
            var a=new Vector3(x-terrainGrid*.5f,0,z-terrainGrid*.5f);var b=a+Vector3.UnitX;var c=a+Vector3.UnitZ;var d=b+Vector3.UnitZ;
            terrainFaces.Add(new(a,b,c));terrainFaces.Add(new(b,d,c));
        }
        else terrainFaces.AddRange([new(new(-5,0,-5),new(5,0,-5),new(-5,0,5)),new(new(5,0,-5),new(5,0,5),new(-5,0,5))]);
        var solidTerrain=args.Contains("--solid-terrain")||args.Contains("--triangle-terrain");
        if(solidTerrain)TerrainCollisionSurface.Add(simulation,pool,terrainFaces.ToArray());
        else{var ground=Make(terrainFaces.ToArray());simulation.Statics.Add(new StaticDescription(Vector3.Zero,simulation.Shapes.Add(ground)));}
        string[] names=["pelvis","spine","chest","neck","head","jaw","shoulderL","upperArmL","forearmL","wristL","palmL","fingerL","shoulderR","upperArmR","forearmR","wristR","palmR","fingerR","thighL","shinL","ankleL","footL","toeL","thighR","shinR","ankleR","footR","toeR","breastL","breastR","buttockL","buttockR"];
        int[] parents=[-1,0,1,2,3,4,2,6,7,8,9,10,2,12,13,14,15,16,0,18,19,20,21,0,23,24,25,26,2,2,0,0];
        var origins=new Vector3[32];var dense=Option("--pose","dense")=="dense";
        for(var body=0;body<32;body++)origins[body]=dense?new Vector3((body%4-1.5f)*.1f,1.2f+(body/16)*.1f,((body/4)%4-1.5f)*.08f):new Vector3((body%4-1.5f)*.32f,1.2f+(body/4)*.35f,0);
        if(Array.IndexOf(args,"--start-penetration-mm")>=0){var shift=1f+Number("--start-penetration-mm",0)*.001f;for(var body=0;body<32;body++)origins[body]-=Vector3.UnitY*shift;}
        if(args.Contains("--recover-start")){var lift=MathF.Max(0,.005f-(origins.Min(p=>p.Y)-.2f));for(var body=0;body<32;body++)origins[body]+=Vector3.UnitY*lift;}
        var source=new List<MeshWeightedVertex>();var influences=new List<MeshSkinInfluence[]>();var sourceIndices=new List<int>();
        for(var body=0;body<32;body++) {
            var count=73475/32+(body<73475%32?1:0);var triangles=BoxGrid(new(.14f,.2f,.11f),14).Take(count);
            var parent=parents[body];
            var shared=new Dictionary<Vector3,int>();
            foreach(var triangle in triangles)foreach(var vertex in new[]{triangle.A,triangle.B,triangle.C}) {
                if(shared.TryGetValue(vertex,out var existing)){sourceIndices.Add(existing);continue;}
                var world=vertex+origins[body];sourceIndices.Add(source.Count);shared.Add(vertex,source.Count);
                source.Add(new(world,parent<0?[new(body,1)]:[new(body,.9f),new(parent,.1f)]));
                influences.Add(parent<0?[new(body,1,vertex)]:[new(body,.9f,vertex),new(parent,.1f,world-origins[parent])]);
            }
        }
        var parts=BodyMeshPartitioner.Partition(source.ToArray(),sourceIndices.ToArray());
        var skin=influences.ToArray();var worldScratch=new Vector3[skin.Length];var transforms=new Matrix4x4[32];var previousTransforms=new Matrix4x4[32];var captured=false;
        var handles=new BodyHandle[32];var shapes=new TypedIndex[32];var centers=new Vector3[32];var scratch=new Vector3[32][];var accepted=new Vector3[32][];
        var collisionLods=new MeshCollisionLodResult?[32];var collisionScratch=new Vector3[32][];
        using var backgroundLod=args.Contains("--background-lod")?new MeshLodBuildQueue():null;
        int backgroundApplied=0,backgroundRejected=0;long lastBackgroundApply=0;
        for(var body=0;body<32;body++) {
            var part=parts[body];var triangles=new Triangle[part.Indices.Length/3*2];scratch[body]=new Vector3[part.Vertices.Length];
            for(var i=0;i<part.Indices.Length;i+=3){var a=part.Vertices[part.Indices[i]]-origins[body];var b=part.Vertices[part.Indices[i+1]]-origins[body];var c=part.Vertices[part.Indices[i+2]]-origins[body];triangles[i/3*2]=new(a,b,c);triangles[i/3*2+1]=new(a,c,b);}
            var mesh=Make(triangles);var inertia=mesh.ComputeOpenInertia(1,out centers[body]);
            accepted[body]=part.Vertices.Select(p=>p-origins[body]-centers[body]).ToArray();
            accepted[body].CopyTo(part.Vertices,0);part.MarkChanged();
            if(args.Contains("--lod")&&backgroundLod==null){
                var lod=MeshCollisionLod.Build(part,Number("--lod-mm",3)*.001f,targetFaceRatio:Number("--lod-percent",15)*.01f);
                if(lod.Reduced){var reduced=MakeGeometry(lod.Geometry);mesh.Dispose(pool);mesh=reduced;collisionLods[body]=lod;collisionScratch[body]=new Vector3[lod.Geometry.Vertices.Length];}
            }
            shapes[body]=simulation.Shapes.Add(mesh);
            handles[body]=simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(origins[body]+centers[body]),inertia,new CollidableDescription(shapes[body],.04f),new BodyActivityDescription(.005f)));
            backgroundLod?.Enqueue(body,part,Number("--lod-mm",3)*.001f);
            watch.FaceCounts[handles[body].Value]=mesh.Triangles.Length;
            if(parents[body]>=0){var parent=parents[body];var anchor=(origins[body]+origins[parent])*.5f;simulation.Solver.Add(handles[parent],handles[body],new BallSocket{LocalOffsetA=anchor-origins[parent]-centers[parent],LocalOffsetB=anchor-origins[body]-centers[body],SpringSettings=new SpringSettings(30,1)});watch.Connected.Add((handles[parent].Value,handles[body].Value));}
        }
        for(var soft=0;soft<59;soft++) {
            var owner=soft%32;var capsule=new Capsule(.025f,.06f);var shape=simulation.Shapes.Add(capsule);var offset=new Vector3((soft%3-1)*.025f,.02f,0);
            var handle=simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(origins[owner]+offset),capsule.ComputeInertia(.03f),new CollidableDescription(shape,.02f),new BodyActivityDescription(.005f)));
            watch.Soft.Add(handle.Value);simulation.Solver.Add(handles[owner],handle,new BallSocket{LocalOffsetA=offset-centers[owner],LocalOffsetB=Vector3.Zero,SpringSettings=new SpringSettings(20,1)});
        }
        void Refit(){
            if(args.Contains("--frozen-mesh"))return;
            for(var body=0;body<32;body++){var r=simulation.Bodies.GetBodyReference(handles[body]);transforms[body]=Matrix4x4.CreateFromQuaternion(r.Pose.Orientation)*Matrix4x4.CreateTranslation(BodyMeshGeometry.BoneOrigin(r.Pose.Position,r.Pose.Orientation,centers[body]));}
            var poseChanged=!captured;for(var body=0;body<32&&!poseChanged;body++)poseChanged=transforms[body]!=previousTransforms[body];
            if(!poseChanged){watch.CachedPosePasses++;return;}
            transforms.CopyTo(previousTransforms,0);captured=true;
            if(!MeshSkinDeformer.Evaluate(skin,transforms,worldScratch))throw new Exception("Skin evaluation failed");
            for(var body=0;body<32;body++){
                var r=simulation.Bodies.GetBodyReference(handles[body]);var part=parts[body];
                if(!MeshSkinDeformer.Interpolate(worldScratch,part.SourceBindings!,r.Pose.Position,r.Pose.Orientation,scratch[body]))throw new Exception("Skin interpolation failed");
                var changed=false;for(var vertex=0;vertex<scratch[body].Length;vertex++)if(Vector3.DistanceSquared(scratch[body][vertex],accepted[body][vertex])>1e-10f){changed=true;break;}
                if(!changed){watch.SkippedRefits++;continue;}
                var lod=collisionLods[body];var collisionGeometry=lod?.Geometry??part;var updated=scratch[body];
                if(lod!=null){updated=collisionScratch[body];for(int i=0;i<updated.Length;i++)updated[i]=scratch[body][lod.SourceVertexIndices[i]];}
                ref var mesh=ref simulation.Shapes.GetShape<Mesh>(shapes[body].Index);if(!MeshSurfaceRefitter.Refit(ref mesh,updated,collisionGeometry.Indices))throw new Exception("Tree refit failed");
                scratch[body].CopyTo(accepted[body],0);scratch[body].CopyTo(part.Vertices,0);part.MarkChanged();
                if(lod!=null){updated.CopyTo(lod.Geometry.Vertices,0);lod.Geometry.MarkChanged();if(!MeshCollisionLod.ValidateCurrent(part,lod,lod.RequestedTolerance+1e-5f,out var error)&&!MeshCollisionLod.ValidateCurrentNearest(part,lod,lod.RequestedTolerance+1e-5f,out error)){
                    var previous=shapes[body];shapes[body]=simulation.Shapes.Add(MakeGeometry(part));r.SetShape(shapes[body]);simulation.Shapes.RemoveAndDispose(previous,pool);collisionLods[body]=null;watch.LodFallbacks++;watch.FaceCounts[handles[body].Value]=part.Indices.Length/3*2;
                }}
                r.Awake=true;r.UpdateBounds();watch.NativeRefits++;
            }
        }
        void ApplyBackground(){
            if(backgroundLod==null||(lastBackgroundApply!=0&&Stopwatch.GetElapsedTime(lastBackgroundApply).TotalMilliseconds<16.7)||!backgroundLod.TryDequeue(out var done))return;
            lastBackgroundApply=Stopwatch.GetTimestamp();
            if(done.Error!=null)throw done.Error;
            if(done.Lod is not {Reduced:true} lod)return;
            int body=done.BodyId;var part=parts[body];
            for(int i=0;i<lod.SourceVertexIndices.Length;i++)lod.Geometry.Vertices[i]=part.Vertices[lod.SourceVertexIndices[i]];
            lod.Geometry.MarkChanged();
            if(!MeshCollisionLod.ValidateCurrent(part,lod,lod.RequestedTolerance+1e-5f,out _)&&!MeshCollisionLod.ValidateCurrentNearest(part,lod,lod.RequestedTolerance+1e-5f,out _)){backgroundRejected++;return;}
            var r=simulation.Bodies.GetBodyReference(handles[body]);var previous=shapes[body];shapes[body]=simulation.Shapes.Add(MakeGeometry(lod.Geometry));
            r.SetShape(shapes[body]);simulation.Shapes.RemoveAndDispose(previous,pool);r.Awake=true;r.UpdateBounds();
            collisionLods[body]=lod;collisionScratch[body]=new Vector3[lod.Geometry.Vertices.Length];watch.FaceCounts[handles[body].Value]=lod.CollisionFaceCount*2;backgroundApplied++;
        }
        Emit(new{kind="built",sourceFaces=73475,sourceVertices=source.Count,partitionVertices=parts.Values.Sum(p=>p.Vertices.Length),bodyRegions=32,softBodies=59,nativeMeshFaces=watch.FaceCounts.Values.Sum(),lodRegions=collisionLods.Count(l=>l!=null),sampledInitialErrorMm=collisionLods.Max(l=>l?.MaximumSampledSurfaceError??0)*1000,terrainFaces=terrainFaces.Count,solidTerrain,contactQuality,startPenetrationMm=Number("--start-penetration-mm",0),iterations,substeps,threadCount,sharedDispatcher,pose=dense?"dense":"spread",policy,buildMs=build.Elapsed.TotalMilliseconds,poolMiB=pool.GetTotalAllocatedByteCount()/1048576d,rssMiB=Process.GetCurrentProcess().WorkingSet64/1048576d});
        double peakStep=0,peakRefit=0;ulong peakPool=0;float peakSpeed=0,peakJointError=0;var elapsed=new Stopwatch();
        var warmPhysics=new List<double>(frames);var awakePhysics=new List<double>(frames);var warmCollision=new List<double>(frames);var warmSolver=new List<double>(frames);var warmRefit=new List<double>(frames);var warmSelection=new List<double>(frames);var warmQuery=new List<double>(frames);var warmDispatch=new List<double>(frames);
        for(var frame=0;frame<frames;frame++) {
            watch.Reset();elapsed.Restart();Refit();ApplyBackground();var beforeRefit=elapsed.Elapsed.TotalMilliseconds;
            var selectionBefore=CollisionWorkBudget.SelectionMilliseconds;var queryBefore=CollisionWorkBudget.ChildQueryMilliseconds;var dispatchBefore=CollisionWorkBudget.DispatchMilliseconds;
            elapsed.Restart();if(sharedLease!=null)sharedLease.Run(simulation,1/60f);else simulation.Timestep(1/60f,threadDispatcher);var stepMs=elapsed.Elapsed.TotalMilliseconds;
            var selectionMs=CollisionWorkBudget.SelectionMilliseconds-selectionBefore;var childQueryMs=CollisionWorkBudget.ChildQueryMilliseconds-queryBefore;var dispatchMs=CollisionWorkBudget.DispatchMilliseconds-dispatchBefore;
            elapsed.Restart();Refit();var refitMs=beforeRefit+elapsed.Elapsed.TotalMilliseconds;
            peakStep=Math.Max(peakStep,stepMs);peakRefit=Math.Max(peakRefit,refitMs);peakPool=Math.Max(peakPool,pool.GetTotalAllocatedByteCount());
            if(frame>=10){warmPhysics.Add(stepMs+refitMs);warmCollision.Add(watch.CollisionMs);warmSolver.Add(watch.SolverMs);warmRefit.Add(refitMs);warmSelection.Add(selectionMs);warmQuery.Add(childQueryMs);warmDispatch.Add(dispatchMs);}
            if(frame>=10&&(watch.MeshPairs>0||watch.NativeRefits>0||watch.SkippedRefits>0))awakePhysics.Add(stepMs+refitMs);
            for(var body=0;body<32;body++){
                var r=simulation.Bodies.GetBodyReference(handles[body]);var speed=r.Velocity.Linear.Length();
                if(!float.IsFinite(speed)||!float.IsFinite(r.Pose.Position.Y))throw new Exception("Nonfinite stress body state");peakSpeed=Math.Max(peakSpeed,speed);
                if(parents[body]>=0){var parent=parents[body];var p=simulation.Bodies.GetBodyReference(handles[parent]);var anchor=(origins[body]+origins[parent])*.5f;var a=p.Pose.Position+Vector3.Transform(anchor-origins[parent]-centers[parent],p.Pose.Orientation);var b=r.Pose.Position+Vector3.Transform(anchor-origins[body]-centers[body],r.Pose.Orientation);peakJointError=Math.Max(peakJointError,Vector3.Distance(a,b));}
            }
            if(frame<5||frame%30==0||frame==frames-1)Emit(new{kind="step",frame,stepMs,watch.CollisionMs,watch.SolverMs,selectionMs,childQueryMs,dispatchMs,refitMs,watch.MeshPairs,watch.MeshTriangleProducts,watch.ChildPairs,watch.NativeRefits,watch.SkippedRefits,watch.CachedPosePasses,boundedProcessed=CollisionWorkBudget.ProcessedPairs,boundedSampled=CollisionWorkBudget.SampledPairs,boundedLimited=CollisionWorkBudget.LimitedQueries,boundedPeakChildren=CollisionWorkBudget.PeakChildren,poolMiB=pool.GetTotalAllocatedByteCount()/1048576d,rssMiB=Process.GetCurrentProcess().WorkingSet64/1048576d});
        }
        var lowestSurface=args.Contains("--frozen-mesh")?Enumerable.Range(0,32).Min(body=>{
            var r=simulation.Bodies.GetBodyReference(handles[body]);
            return parts[body].Vertices.Min(v=>(r.Pose.Position+Vector3.Transform(v,r.Pose.Orientation)).Y);
        }):worldScratch.Min(v=>v.Y);
        var finalMaxSpeed=handles.Max(h=>simulation.Bodies.GetBodyReference(h).Velocity.Linear.Length());
        Emit(new{kind="complete",peakStepMs=peakStep,peakRefitMs=peakRefit,peakPoolMiB=peakPool/1048576d,peakSpeed,finalMaxSpeed,peakJointError,lowestSurface,watch.LodFallbacks,backgroundApplied,backgroundRejected,finalNativeMeshFaces=watch.FaceCounts.Values.Sum(),warmupSteps=10,warmMeasured=warmPhysics.Count,warmPhysicsMs=Summary(warmPhysics),awakeMeasured=awakePhysics.Count,awakePhysicsMs=Summary(awakePhysics),warmCollisionMs=Summary(warmCollision),warmSolverMs=Summary(warmSolver),warmRefitMs=Summary(warmRefit),warmSelectionMs=Summary(warmSelection),warmQueryMs=Summary(warmQuery),warmDispatchMs=Summary(warmDispatch)});
        if(backgroundLod!=null&&backgroundApplied==0)throw new Exception("Background LOD did not exercise native installation");
        if(lowestSurface<-.05f)throw new Exception($"Stress skin penetrated terrain: {lowestSurface}m");return 0;
    }catch(PairBudgetException error){Emit(new{kind="guarded_blowup",error.Message,watch.MeshPairs,watch.MeshTriangleProducts,poolMiB=pool.GetTotalAllocatedByteCount()/1048576d,rssMiB=Process.GetCurrentProcess().WorkingSet64/1048576d});return 2;}
    finally{pool.Clear();}
}
static TimingStats Summary(List<double> values){if(values.Count==0)return default;values.Sort();return new(values.Average(),values[values.Count/2],values[Math.Min(values.Count-1,(int)Math.Ceiling(values.Count*.95)-1)]);}
static IEnumerable<Triangle> BoxGrid(Vector3 half,int n){
    for(var axis=0;axis<3;axis++)foreach(var sign in new[]{-1,1})for(var u=0;u<n;u++)for(var v=0;v<n;v++){
        Vector3 Point(int x,int y){var p=new Vector3();p[axis]=half[axis]*sign;p[(axis+1)%3]=half[(axis+1)%3]*(2f*x/n-1);p[(axis+2)%3]=half[(axis+2)%3]*(2f*y/n-1);return p;}
        var a=Point(u,v);var b=Point(u+1,v);var c=Point(u+1,v+1);var d=Point(u,v+1);yield return new(a,b,c);yield return new(a,c,d);
    }
}
sealed class PairBudgetException(string message):Exception(message);
readonly record struct TimingStats(double Mean,double Median,double P95);
sealed class Watch {
    public long PairBudget,FrameBudget,MeshPairs,MeshTriangleProducts,ChildPairs,NativeRefits,SkippedRefits,CachedPosePasses,LodFallbacks;public bool GroundOnly;
    public double CollisionMs,SolverMs;
    public readonly Dictionary<int,int> FaceCounts=new();public readonly HashSet<(int,int)> Connected=new();public readonly HashSet<int> Soft=new();
    public void Reset(){MeshPairs=MeshTriangleProducts=ChildPairs=NativeRefits=SkippedRefits=CachedPosePasses=0;}
}
struct Contacts:INarrowPhaseCallbacks {
    public Watch Watch;
    public void Initialize(Simulation simulation){}
    public bool AllowContactGeneration(int worker,CollidableReference a,CollidableReference b,ref float margin){
        var aStatic=a.Mobility==CollidableMobility.Static;var bStatic=b.Mobility==CollidableMobility.Static;
        if((!aStatic&&Watch.Soft.Contains(a.BodyHandle.Value))||(!bStatic&&Watch.Soft.Contains(b.BodyHandle.Value)))return false;
        if(aStatic||bStatic)return true;
        var ai=a.BodyHandle.Value;var bi=b.BodyHandle.Value;
        if(Watch.Connected.Contains((ai,bi))||Watch.Connected.Contains((bi,ai)))return false;
        if(Watch.GroundOnly)return false;
        if(Watch.FaceCounts.TryGetValue(ai,out var ac)&&Watch.FaceCounts.TryGetValue(bi,out var bc)){
            Interlocked.Increment(ref Watch.MeshPairs);var product=(long)ac*bc;var frameProduct=Interlocked.Add(ref Watch.MeshTriangleProducts,product);
            if(product>Watch.PairBudget||frameProduct>Watch.FrameBudget)throw new PairBudgetException($"Mesh pair {ai}/{bi}: {ac}x{bc}={product} potential triangle products exceeds protected dispatch budget");
        }
        return true;
    }
    public bool AllowContactGeneration(int worker,CollidablePair pair,int a,int b){Interlocked.Increment(ref Watch.ChildPairs);return true;}
    public bool ConfigureContactManifold<T>(int worker,CollidablePair pair,ref T manifold,out PairMaterialProperties material)where T:unmanaged,IContactManifold<T>{material=new(.8f,2,new SpringSettings(30,1));return true;}
    public bool ConfigureContactManifold(int worker,CollidablePair pair,int a,int b,ref ConvexContactManifold manifold)=>true;
    public void Dispose(){}
}
struct Gravity:IPoseIntegratorCallbacks {
    public AngularIntegrationMode AngularIntegrationMode=>AngularIntegrationMode.Nonconserving;public bool AllowSubstepsForUnconstrainedBodies=>false;public bool IntegrateVelocityForKinematics=>false;
    public void Initialize(Simulation simulation){}public void PrepareForIntegration(float dt){}
    public void IntegrateVelocity(Vector<int> indices,Vector3Wide position,QuaternionWide rotation,BodyInertiaWide inertia,Vector<int> mask,int worker,Vector<float> dt,ref BodyVelocityWide velocity){velocity.Linear.Y-=new Vector<float>(9.81f)*dt;}
}
