using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuUtilities.Memory;
using CombatSimulator.Animation.MeshVolume;

static class LiveMeshPhysics
{
    static void Check(bool condition,string message) { if(!condition)throw new Exception(message); }
    public static void Run()
    {
        var pool=new BufferPool();
        using(var simulation=Simulation.Create(pool,new ContactCallbacks{OnlyStatics=true},new GravityCallbacks(),new SolveDescription(12,4))) {
            Mesh Make(Triangle[] source) {
                pool.Take<Triangle>(source.Length,out var triangles);
                for(var i=0;i<source.Length;i++)triangles[i]=source[i];
                return new Mesh(triangles,Vector3.One,pool);
            }
            var floor=Make([new(new(-5,0,-5),new(5,0,-5),new(-5,0,5)),new(new(5,0,-5),new(5,0,5),new(-5,0,5))]);
            simulation.Statics.Add(new StaticDescription(Vector3.Zero,simulation.Shapes.Add(floor)));
            Vector3[] origins=[new(-.4f,1.2f,0),new(.4f,1.2f,0)];
            var sourcePositions=new List<Vector3>();var indices=new List<int>();
            foreach(var x in new[]{-.8f,-.3f,0,.3f,.8f}) foreach(var yz in new[]{new Vector2(-.2f,-.25f),new Vector2(.2f,-.25f),new Vector2(.2f,.25f),new Vector2(-.2f,.25f)})
                sourcePositions.Add(new(x,1.2f+yz.X,yz.Y));
            for(var section=0;section<4;section++)for(var edge=0;edge<4;edge++) {
                var a=section*4+edge;var b=section*4+(edge+1)%4;var c=b+4;var d=a+4;
                indices.AddRange([a,b,c,a,c,d]);
            }
            indices.AddRange([0,2,1,0,3,2,16,17,18,16,18,19]);
            var sources=new MeshSkinInfluence[sourcePositions.Count][];
            var weighted=new MeshWeightedVertex[sourcePositions.Count];
            for(var i=0;i<sources.Length;i++) {
                var p=sourcePositions[i];var right=Math.Clamp((p.X+.3f)/.6f,0,1);
                sources[i]=[new(0,1-right,p-origins[0]),new(1,right,p-origins[1])];
                weighted[i]=new(p,[new(0,1-right),new(1,right)]);
            }
            var parts=BodyMeshPartitioner.Partition(weighted,indices.ToArray());
            var handles=new BodyHandle[2];var centers=new Vector3[2];var shapes=new TypedIndex[2];
            var local=new Vector3[2][];var transforms=new Matrix4x4[2];var world=new Vector3[sources.Length];
            for(var owner=0;owner<2;owner++) {
                var part=parts[owner];var triangles=new Triangle[part.Indices.Length/3*2];
                for(var t=0;t<part.Indices.Length;t+=3) {
                    var a=part.Vertices[part.Indices[t]]-origins[owner];var b=part.Vertices[part.Indices[t+1]]-origins[owner];var c=part.Vertices[part.Indices[t+2]]-origins[owner];
                    triangles[t/3*2]=new(a,b,c);triangles[t/3*2+1]=new(a,c,b);
                }
                var mesh=Make(triangles);var inertia=mesh.ComputeOpenInertia(1,out centers[owner]);
                shapes[owner]=simulation.Shapes.Add(mesh);
                handles[owner]=simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(origins[owner]+centers[owner]),inertia,new CollidableDescription(shapes[owner],.01f),new BodyActivityDescription(.005f)));
                local[owner]=new Vector3[part.Vertices.Length];
            }
            var joint=new Vector3(0,1.2f,0);
            var offsetA=joint-origins[0]-centers[0];var offsetB=joint-origins[1]-centers[1];
            simulation.Solver.Add(handles[0],handles[1],new BallSocket{LocalOffsetA=offsetA,LocalOffsetB=offsetB,SpringSettings=new SpringSettings(30,1)});
            simulation.Bodies.GetBodyReference(handles[1]).Velocity.Angular.Z=2.5f;
            void Refit() {
                for(var owner=0;owner<2;owner++) {
                    var body=simulation.Bodies.GetBodyReference(handles[owner]);
                    var boneOrigin=BodyMeshGeometry.BoneOrigin(body.Pose.Position,body.Pose.Orientation,centers[owner]);
                    transforms[owner]=Matrix4x4.CreateFromQuaternion(body.Pose.Orientation)*Matrix4x4.CreateTranslation(boneOrigin);
                }
                Check(MeshSkinDeformer.Evaluate(sources,transforms,world),"source deformation failed");
                for(var owner=0;owner<2;owner++) {
                    var body=simulation.Bodies.GetBodyReference(handles[owner]);var part=parts[owner];
                    Check(MeshSkinDeformer.Interpolate(world,part.SourceBindings!,body.Pose.Position,body.Pose.Orientation,local[owner]),"source interpolation failed");
                    ref var mesh=ref simulation.Shapes.GetShape<Mesh>(shapes[owner].Index);
                    Check(MeshSurfaceRefitter.Refit(ref mesh,local[owner],part.Indices),"live refit failed");body.UpdateBounds();
                }
            }
            Refit();
            var initialLocal=new[]{(Vector3[])local[0].Clone(),(Vector3[])local[1].Clone()};
            float maxJointError=0,maxVelocity=0,maxSeamError=0,maxDeformation=0;
            for(var step=0;step<2400;step++) {
                Refit();simulation.Timestep(1/120f);Refit();
                var a=simulation.Bodies.GetBodyReference(handles[0]);var b=simulation.Bodies.GetBodyReference(handles[1]);
                var anchorA=a.Pose.Position+Vector3.Transform(offsetA,a.Pose.Orientation);var anchorB=b.Pose.Position+Vector3.Transform(offsetB,b.Pose.Orientation);
                maxJointError=Math.Max(maxJointError,Vector3.Distance(anchorA,anchorB));maxVelocity=Math.Max(maxVelocity,Math.Max(a.Velocity.Linear.Length(),b.Velocity.Linear.Length()));
                Check(float.IsFinite(maxVelocity)&&float.IsFinite(a.Pose.Position.Y)&&float.IsFinite(b.Pose.Position.Y),"nonfinite live body state");
                for(var owner=0;owner<2;owner++)for(var vertex=0;vertex<local[owner].Length;vertex++)
                    maxDeformation=Math.Max(maxDeformation,Vector3.Distance(local[owner][vertex],initialLocal[owner][vertex]));
                // Identical source boundary bindings must remain identical even when both bones rotate.
                for(var ia=0;ia<parts[0].Vertices.Length;ia++)for(var ib=0;ib<parts[1].Vertices.Length;ib++) {
                    if(Vector3.DistanceSquared(parts[0].Vertices[ia],parts[1].Vertices[ib])>1e-12f)continue;
                    var pa=a.Pose.Position+Vector3.Transform(local[0][ia],a.Pose.Orientation);var pb=b.Pose.Position+Vector3.Transform(local[1][ib],b.Pose.Orientation);
                    maxSeamError=Math.Max(maxSeamError,Vector3.Distance(pa,pb));
                }
            }
            var endA=simulation.Bodies.GetBodyReference(handles[0]);var endB=simulation.Bodies.GetBodyReference(handles[1]);
            Check(maxJointError<.025f,$"live contact joint error {maxJointError}");Check(maxSeamError<1e-5f,$"live surface tear {maxSeamError}");
            Check(maxDeformation>.05f,"live case never significantly deformed beyond rigid transport");
            Check(endA.Pose.Position.Y>0&&endB.Pose.Position.Y>0,"live mesh fell through floor");
            Check(endA.Velocity.Linear.Length()<.03f&&endB.Velocity.Linear.Length()<.03f,$"live collision feedback never settles {endA.Velocity.Linear}/{endB.Velocity.Linear}");
            var before=GC.GetAllocatedBytesForCurrentThread();for(var i=0;i<100;i++)Refit();var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Check(allocated==0,$"live refit allocated {allocated} bytes");
            Console.WriteLine($"  live skin max joint error={maxJointError:F6}, seam error={maxSeamError:E2}, deformation={maxDeformation:F4}, peak speed={maxVelocity:F3}; final y={endA.Pose.Position.Y:F4}/{endB.Pose.Position.Y:F4}; warmed refit allocations={allocated}");
        }
        pool.Clear();
    }
}
