using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using CombatSimulator.Animation.MeshVolume;

static class TerrainRegression
{
    public static void Run()
    {
        foreach (var kind in new[] { 0, 1, 2, 3 })
        {
            var pool = new BufferPool();
            using (var sim = Simulation.Create(pool, new Contacts(), new Gravity(), new SolveDescription(4, 1)))
            {
                float Height(float x, float z) => kind == 1 ? .15f * x + .07f * z + .2f : .12f;
                var a = new Vector3(-1, Height(-1, -1), -1);
                var b = new Vector3(1, Height(1, -1), -1);
                var c = new Vector3(-1, Height(-1, 1), 1);
                var d = new Vector3(1, Height(1, 1), 1);
                if (kind == 2) { a.Y = 0; b.Y = .15f; c.Y = .2f; d.Y = -.05f; }
                Triangle[] source = kind == 3 ? [new(a, b, c)] : [new(a, b, c), new(b, d, c)];
                TerrainCollisionSurface.Add(sim, pool, source);
                foreach (var t in source)
                {
                    var point = t.A * .2f + t.B * .3f + t.C * .5f;
                    var hit = new Hit();
                    sim.RayCast(new Vector3(point.X, 2, point.Z), -Vector3.UnitY, 4, ref hit);
                    if (!hit.Found || MathF.Abs(2 - hit.T - point.Y) > 2e-5f)
                        throw new Exception($"Solid terrain changed sampled surface, kind={kind}, expected={point.Y}, got={2-hit.T}");
                }
                if (kind == 3)
                {
                    var gap = new Hit();
                    sim.RayCast(new Vector3(.8f, 2, .8f), -Vector3.UnitY, 4, ref gap);
                    if (gap.Found) throw new Exception("Terrain merge filled a missing triangle");
                }
            }
            pool.Clear();
        }
        Console.WriteLine("PASS solid terrain preserves flat, sloped, nonplanar and missing-triangle top surfaces.");
    }

    struct Hit : IRayHitHandler
    {
        public bool Found;
        public float T;
        public bool AllowTest(CollidableReference c) => true;
        public bool AllowTest(CollidableReference c, int child) => true;
        public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal, CollidableReference c, int child)
        { Found = true; T = t; maximumT = t; }
    }
}
