using System.Numerics;
using System.Text.Json;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using CombatSimulator.Animation.MeshVolume;

static class ConcavityRegression
{
    public static int Run()
    {
        var pool=new BufferPool();
        using(var simulation=Simulation.Create(pool,new Contacts{Watch=new Watch()},new Gravity(),new SolveDescription(8,16))) {
            CollisionWorkBudget.Register(simulation.NarrowPhase.CollisionTaskRegistry);
            Mesh Make(Triangle[] source){pool.Take<Triangle>(source.Length,out var data);for(var i=0;i<source.Length;i++)data[i]=source[i];return new Mesh(data,Vector3.One,pool);}
            simulation.Statics.Add(new StaticDescription(Vector3.Zero,simulation.Shapes.Add(Make([new(new(-5,0,-5),new(5,0,-5),new(-5,0,5)),new(new(5,0,-5),new(5,0,5),new(-5,0,5))]))));
            simulation.Statics.Add(new StaticDescription(Vector3.Zero,simulation.Shapes.Add(Make(Box(new(.02f,0,-.2f),new(.32f,.25f,.2f))))));
            var faces=Box(new(-.33f,-.2f,-.3f),new(-.13f,.15f,.3f)).Concat(Box(new(.47f,-.2f,-.3f),new(.67f,.15f,.3f))).Concat(Box(new(-.33f,.15f,-.3f),new(.67f,.3f,.3f))).ToArray();
            faces=faces.Concat(faces.Select(t=>new Triangle(t.A,t.C,t.B))).ToArray();
            var mesh=Make(faces);var inertia=mesh.ComputeOpenInertia(1,out var center);
            var handle=simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(new Vector3(0,.8f,0)+center),inertia,new CollidableDescription(simulation.Shapes.Add(mesh),.005f),new BodyActivityDescription(.005f)));
            for(var i=0;i<600;i++)simulation.Timestep(1/120f);
            var body=simulation.Bodies.GetBodyReference(handle);var bone=BodyMeshGeometry.BoneOrigin(body.Pose.Position,body.Pose.Orientation,center);
            var ok=MathF.Abs(bone.Y-.2f)<.025f&&MathF.Abs(bone.X)<.03f&&body.Velocity.Linear.Length()<.01f;
            Console.WriteLine(JsonSerializer.Serialize(new{kind="concavity",ok,boneY=bone.Y,boneX=bone.X,speed=body.Velocity.Linear.Length(),CollisionWorkBudget.PeakChildren,CollisionWorkBudget.SampledPairs,poolMiB=pool.GetTotalAllocatedByteCount()/1048576d}));
            if(!ok)return 1;
        }
        pool.Clear();return 0;
    }
    static Triangle[] Box(Vector3 min,Vector3 max){
        Vector3[] v=[new(min.X,min.Y,min.Z),new(max.X,min.Y,min.Z),new(max.X,max.Y,min.Z),new(min.X,max.Y,min.Z),new(min.X,min.Y,max.Z),new(max.X,min.Y,max.Z),new(max.X,max.Y,max.Z),new(min.X,max.Y,max.Z)];
        int[] ix=[0,3,2,0,2,1,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5];
        return Enumerable.Range(0,12).Select(i=>new Triangle(v[ix[i*3]],v[ix[i*3+2]],v[ix[i*3+1]])).ToArray();
    }
}
