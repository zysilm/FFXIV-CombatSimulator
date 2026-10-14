using System.Numerics;
using CombatSimulator.Animation.MeshVolume;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
BodyMeshGeometry Grid(int width, int depth, float stepX, float stepZ, Func<float,float,float> height)
{
    var vertices = new Vector3[(width+1)*(depth+1)]; var indices = new int[width*depth*6];
    var bindings = new MeshSourceBinding[vertices.Length];
    for(int z=0;z<=depth;z++)for(int x=0;x<=width;x++)
    { int i=z*(width+1)+x;float px=(x-width/2f)*stepX,pz=(z-depth/2f)*stepZ;vertices[i]=new(px,height(px,pz),pz);bindings[i]=new(i,i,i,Vector3.UnitX); }
    for(int z=0,i=0;z<depth;z++)for(int x=0;x<width;x++)
    {int a=z*(width+1)+x,b=a+1,c=a+width+1,d=c+1;indices[i++]=a;indices[i++]=c;indices[i++]=b;indices[i++]=b;indices[i++]=c;indices[i++]=d;}
    return new(vertices,indices,bindings);
}
HashSet<int> Boundary(BodyMeshGeometry geometry)
{
    var edges=new Dictionary<(int,int),int>();
    for(int i=0;i<geometry.Indices.Length;i+=3)for(int c=0;c<3;c++)
    {int a=geometry.Indices[i+c],b=geometry.Indices[i+(c+1)%3];if(a>b)(a,b)=(b,a);edges[(a,b)]=edges.GetValueOrDefault((a,b))+1;}
    return edges.Where(e=>e.Value==1).SelectMany(e=>new[]{e.Key.Item1,e.Key.Item2}).ToHashSet();
}
void Verify(string name, BodyMeshGeometry full, MeshCollisionLodResult lod, float maximumRatio)
{
    Console.WriteLine($"{name}: {lod.SourceFaceCount}->{lod.CollisionFaceCount} faces ({(float)lod.CollisionFaceCount/lod.SourceFaceCount:P1}); error={lod.MaximumSampledSurfaceError*1000:F4}mm, endpoint={lod.MaximumOriginalVertexDisplacement*1000:F2}mm, {lod.BuildMilliseconds:F1}ms/{lod.CandidateChecks} checks; {lod.Status}");
    Check(lod.Reduced && lod.CollisionFaceCount <= lod.SourceFaceCount*maximumRatio, name+": insufficient safe reduction");
    Check(full.Vertices.Length>lod.Geometry.Vertices.Length,name+": no vertex reduction");
    var retained=lod.SourceVertexIndices.ToHashSet();
    Check(Boundary(full).All(retained.Contains),name+": source boundary/seam vertices changed");
    for(int i=0;i<lod.SourceVertexIndices.Length;i++)
    {
        Check(lod.Geometry.Vertices[i]==full.Vertices[lod.SourceVertexIndices[i]],name+": introduced non-source vertex");
        Check(lod.Geometry.SourceBindings![i]==full.SourceBindings![lod.SourceVertexIndices[i]],name+": lost live binding");
    }
    for(int i=0;i<lod.Geometry.Indices.Length;i+=3)
    {
        var a=lod.Geometry.Vertices[lod.Geometry.Indices[i]];var b=lod.Geometry.Vertices[lod.Geometry.Indices[i+1]];var c=lod.Geometry.Vertices[lod.Geometry.Indices[i+2]];
        Check(Vector3.Cross(b-a,c-a).Y>0,name+": flipped or degenerate face");
    }
    Check(MeshCollisionLod.ValidateCurrent(full,lod,.00301f,out var error)&&error<=.00301f,name+": initial live patch validation failed");
    // Independent dense original-vertex check against the final full coarse triangle set.
    foreach(var p in full.Vertices)
    {Check(lod.Geometry.Query.Nearest(p,1,1e-16f,out _,out _,out var distance)&&distance<=.00301f*.00301f,name+": source vertex exceeds sampled surface target");}
}
var planar=Grid(32,32,.02f,.02f,(_,_)=>0);
var planarLod=MeshCollisionLod.Build(planar,.003f,.25f,maximumBuildMilliseconds:0);
Verify("planar large edges",planar,planarLod,.4f);
Check(planarLod.MaximumOriginalVertexDisplacement>.003f,"Planar exception was not exercised");
Check(planarLod.MaximumSampledSurfaceError<1e-5f,"Planar simplification changed the surface union beyond floating-point tolerance");
var curved=Grid(48,48,.005f,.005f,(x,z)=>.07f*(MathF.Cos(x*7)+MathF.Cos(z*7)));
var curvedLod=MeshCollisionLod.Build(curved,.003f,.35f,maximumBuildMilliseconds:0);
Verify("smooth curved surface",curved,curvedLod,.6f);
var waist=Grid(40,40,.007f,.007f,(x,z)=>-.05f*MathF.Exp(-(x*x+z*z)/.003f));
var waistLod=MeshCollisionLod.Build(waist,.003f,.35f,maximumBuildMilliseconds:0);
Verify("concave waist",waist,waistLod,.6f);
Check(waistLod.Geometry.Vertices.Min(v=>v.Y)<-.047f,"Concave waist was filled into an air cushion");
var foot=Grid(8,48,.001f,.005f,(_,_)=>0);
var footLod=MeshCollisionLod.Build(foot,.003f,.35f,maximumBuildMilliseconds:0);
Verify("thin foot",foot,footLod,.6f);
Check(footLod.Geometry.Vertices.Min(v=>v.X)==foot.Vertices.Min(v=>v.X)&&footLod.Geometry.Vertices.Max(v=>v.X)==foot.Vertices.Max(v=>v.X),"Thin foot outline shrank");
var crease=Grid(24,24,.01f,.01f,(x,_)=>MathF.Abs(x)*.8f);
var creaseLod=MeshCollisionLod.Build(crease,.003f,.4f,maximumBuildMilliseconds:0);
Verify("sharp concave crease",crease,creaseLod,.6f);
var creaseRetained=creaseLod.SourceVertexIndices.ToHashSet();
Check(Enumerable.Range(0,crease.Vertices.Length).Where(i=>MathF.Abs(crease.Vertices[i].X)<1e-7f).All(creaseRetained.Contains),"Sharp concave feature vertices were removed");
// Arbitrary live movement copies only retained endpoints, never blends new weights.
for(int i=0;i<curved.Vertices.Length;i++)curved.Vertices[i]+=new Vector3(1,2,3);
for(int i=0;i<curvedLod.SourceVertexIndices.Length;i++)curvedLod.Geometry.Vertices[i]=curved.Vertices[curvedLod.SourceVertexIndices[i]];
Check(MeshCollisionLod.ValidateCurrent(curved,curvedLod,.00301f,out _),"Rigid live refit changed approximation error");
// A future high-frequency deformation can invalidate a planar simplification. Detect it;
// initial-pose error is deliberately not advertised as an animated error guarantee.
var planarRetained=planarLod.SourceVertexIndices.ToHashSet();
var validation=planarLod.ValidationSamples.First(s=>s.A==s.B&&!planarRetained.Contains(s.A));
planar.Vertices[validation.A].Y+=.1f;
for(int i=0;i<planarLod.SourceVertexIndices.Length;i++)planarLod.Geometry.Vertices[i]=planar.Vertices[planarLod.SourceVertexIndices[i]];
Check(!MeshCollisionLod.ValidateCurrent(planar,planarLod,.003f,out _),"Live error check missed invalidating surface deformation");
planarLod.Geometry.MarkChanged();
Check(!MeshCollisionLod.ValidateCurrentNearest(planar,planarLod,.003f,out _),"Nearest live validation missed an actual deformation gap");
// A tangentially moving fan center changes the nearest triangle while preserving the
// square's planar surface. Static patch assignment would produce a false rejection.
var fanVertices=new[]{new Vector3(-1,0,-1),new Vector3(1,0,-1),new Vector3(1,0,1),new Vector3(-1,0,1),Vector3.Zero};
int[] fanIndices={4,1,0,4,2,1,4,3,2,4,0,3};
var fan=new BodyMeshGeometry(fanVertices,fanIndices);
var fanFullVertices=fanVertices.Append(new Vector3(.1f,0,-.1f)).ToArray();
var fanFull=new BodyMeshGeometry(fanFullVertices,(int[])fanIndices.Clone());
var fanLod=new MeshCollisionLodResult{Geometry=fan,SourceVertexIndices=Enumerable.Range(0,5).ToArray(),
    ValidationSamples=new[]{new MeshLodValidationSample(5,5,5,Vector3.UnitX,0)},SourceFaceCount=8,Status="test"};
Check(MeshCollisionLod.ValidateCurrent(fanFull,fanLod,.003f,out _),"Initial fan patch assignment wrong");
fanVertices[4]=fanFullVertices[4]=new(.7f,0,-.5f);fan.MarkChanged();
Check(!MeshCollisionLod.ValidateCurrent(fanFull,fanLod,.003f,out _),"Fan fixture did not move nearest triangle");
Check(MeshCollisionLod.ValidateCurrentNearest(fanFull,fanLod,.003f,out _),"Changing nearest triangle caused false positive fallback");
fanFullVertices[5].Y=.05f;
Check(!MeshCollisionLod.ValidateCurrentNearest(fanFull,fanLod,.003f,out _),"Nearest validation missed real current surface error");
var unchanged=MeshCollisionLod.Build(foot,targetFaceRatio:1);
Check(ReferenceEquals(unchanged.Geometry,foot)&&!unchanged.Reduced,"Disabled LOD did not return full geometry");
// Background builds own their inputs; a live skin mutation must not race with simplification.
var queuedSource=Grid(20,20,.01f,.01f,(_,_)=>0);
using(var queue=new MeshLodBuildQueue())
{
    queue.Enqueue(17,queuedSource,.003f);
    queue.Enqueue(18,queuedSource,.05f);
    Array.Fill(queuedSource.Vertices,new Vector3(float.NaN));
    await queue.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
    Check(queue.TryDequeue(out var first)&&first.BodyId==17&&first.Error==null&&first.Lod is {Reduced:true},"Queued build failed or read mutable skin");
    Check(queue.TryDequeue(out var second)&&second.BodyId==18&&second.Error==null&&second.Lod is {Reduced:true},"Queued build lost serial order or 50mm tolerance support");
    Check(first.Lod!.Geometry.Vertices.All(v=>float.IsFinite(v.X)),"Worker shared live vertex storage");
    Check(!queue.TryDequeue(out _),"Queue repeated completion");
}
var cancelledQueue=new MeshLodBuildQueue();
for(int i=0;i<12;i++)cancelledQueue.Enqueue(i,foot,.003f);
cancelledQueue.Dispose();
cancelledQueue.Dispose();
await cancelledQueue.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
Check(!cancelledQueue.TryDequeue(out _),"Disposed generation published stale collider");
// Cache geometry is bone-local and remains independent of live skin, active simulations,
// world movement, COM recentering, body-axis choice and later source/model mutations.
var capturedGrid=Grid(24,24,.01f,.015f,(_,_)=>0);
var capturedOrigin=new Vector3(4,2,-3);
var capturedRotation=Quaternion.CreateFromYawPitchRoll(.8f,.2f,-.3f);
var originalLocal=(Vector3[])capturedGrid.Vertices.Clone();
for(int i=0;i<capturedGrid.Vertices.Length;i++)capturedGrid.Vertices[i]=capturedOrigin+Vector3.Transform(originalLocal[i],capturedRotation);
using(var cache=new FrozenMeshGeometryCache())
{
    cache.Add("waist",capturedGrid,capturedOrigin,capturedRotation);
    cache.ConfigureLod(true,.003f);
    await cache.WhenIdle.WaitAsync(TimeSpan.FromSeconds(10));
    while(cache.Poll(out var error))Check(error==null,"Frozen cache background build failed");
    Array.Fill(capturedGrid.Vertices,new Vector3(99)); // No automatic geometry replacement.
    for(int activation=0;activation<8;activation++)
    {
        var origin=new Vector3(-2+activation,.6f,activation*.2f);
        var rotation=Quaternion.CreateFromYawPitchRoll(activation*.3f,-.4f,.7f);
        Check(cache.TryCreateWorld("waist",origin,rotation,out var world),"Frozen cache missed repeat activation");
        for(int i=0;i<world.Vertices.Length;i++)
            Check(Vector3.Distance(world.Vertices[i],origin+Vector3.Transform(originalLocal[i],rotation))<2e-6f,"Frozen bone frame drifted/reloaded source geometry");
        var bodyRotation=Quaternion.CreateFromYawPitchRoll(.2f,activation*.15f,-.8f);
        var center=origin+new Vector3(.1f,-.02f,.08f);
        var bodyVertices=world.Vertices.Select(p=>Vector3.Transform(p-center,Quaternion.Inverse(bodyRotation))).ToArray();
        var bodyLocal=new BodyMeshGeometry(bodyVertices,world.Indices);
        Check(cache.TryCreateLod("waist",bodyLocal,out var lod),"Frozen cache repeated simplification or lost LOD");
        Check(MeshCollisionLod.ValidateCurrentNearest(bodyLocal,lod,.00301f,out _),"Cached topology lost surface/COM alignment");
        Array.Fill(lod.Geometry.Vertices,new Vector3(float.NaN));
        Array.Fill(world.Indices,0); // This active simulation must not mutate the cache.
    }
    cache.ConfigureLod(false,.003f);
    Check(cache.TryCreateWorld("waist",Vector3.Zero,Quaternion.Identity,out var nextWorld),"Cache lost full geometry after LOD disable");
    Check(!cache.TryCreateLod("waist",nextWorld,out _),"Disabling LOD retained stale topology");
}
// Explicit refresh creates a new cache from the changed surface, replacing the old capture.
using(var refreshed=new FrozenMeshGeometryCache())
{
    var replacement=Grid(8,8,.01f,.01f,(_,_)=>.05f);
    refreshed.Add("waist",replacement,Vector3.Zero,Quaternion.Identity);
    Check(refreshed.TryCreateWorld("waist",Vector3.Zero,Quaternion.Identity,out var refreshedWorld)&&MathF.Abs(refreshedWorld.Vertices[0].Y-.05f)<1e-6f,"Manual cache replacement lost new geometry");
}
Console.WriteLine($"PASS {checks} checks; mesh LOD, background lifecycle and frozen cache repeated activation/COM frames/input isolation/manual replacement.");
