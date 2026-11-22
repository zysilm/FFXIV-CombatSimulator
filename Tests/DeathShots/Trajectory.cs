using System.Numerics;
using System.Text;
using CombatSimulator.Camera.DeathShots;

namespace DeathShots;

/// <summary>One exported pose trajectory (written by Tests/RagdollReplay/TrajectoryExport.cs; layout there).</summary>
internal sealed class Trajectory
{
    public string Name = "";
    public bool IsPlayer, Humanoid;
    public float Facing, ActivateGroundY, Water;
    public Vector3 ActivatePosition;
    public string[] Bones = Array.Empty<string>();
    public Vector3[] GroundRays = Array.Empty<Vector3>();
    public Vector3[] Triangles = Array.Empty<Vector3>();
    public Frame[] Frames = Array.Empty<Frame>();

    public sealed class Frame
    {
        public float Time, Dt, GroundY;
        public bool PhysicsStarted, PoseFresh;
        public byte PoseSource;
        /// <summary>Bone origins from the recorded pose (SkeletonPose before physics, PoseOut after).</summary>
        public Vector3[] Bone = Array.Empty<Vector3>();
        /// <summary>Rigid body centres (NaN = the bone has no body, or physics has not started). A physics frame
        /// without a fixed step keeps the last values: the bodies did not move. (The bodies are created in the last
        /// frame before the one flagged as physics; their first state is written there.)</summary>
        public Vector3[] Body = Array.Empty<Vector3>();
        /// <summary>What the plugin would read this frame: the body centre once physics runs and the bone has a
        /// body, else the bone.</summary>
        public Vector3[] Runtime = Array.Empty<Vector3>();
        /// <summary>World rotation of the head bone relative to its bind pose in the frame's pose (the last one known
        /// where a frame has none; NaN in a trajectory exported before it was). Not used by the death shots any more:
        /// they do not choose a side.</summary>
        public Quaternion HeadRotation = new(float.NaN, float.NaN, float.NaN, float.NaN);
    }

    public int BoneIndex(string name) => Array.IndexOf(Bones, name);

    public float Seconds => Frames.Length > 0 ? Frames[^1].Time : 0f;

    public static Trajectory Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        var magic = Encoding.ASCII.GetString(r.ReadBytes(8));
        if (magic is not ("DSTRAJ01" or "DSTRAJ02")) throw new InvalidDataException("not a death-shot trajectory: " + path);
        var withHead = magic == "DSTRAJ02";
        var lastHead = new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
        var t = new Trajectory
        {
            Name = r.ReadString(), IsPlayer = r.ReadBoolean(), Humanoid = r.ReadBoolean(), Facing = r.ReadSingle(),
            ActivatePosition = V(r), ActivateGroundY = r.ReadSingle(), Water = r.ReadSingle(),
        };
        t.Bones = new string[r.ReadInt32()];
        for (var i = 0; i < t.Bones.Length; i++) t.Bones[i] = r.ReadString();
        t.GroundRays = new Vector3[r.ReadInt32()];
        for (var i = 0; i < t.GroundRays.Length; i++) t.GroundRays[i] = V(r);
        t.Triangles = new Vector3[r.ReadInt32()];
        for (var i = 0; i < t.Triangles.Length; i++) t.Triangles[i] = V(r);
        t.Frames = new Frame[r.ReadInt32()];
        Vector3[]? lastBody = null, lastBone = null;
        for (var i = 0; i < t.Frames.Length; i++)
        {
            var f = new Frame
            {
                Time = r.ReadSingle(), Dt = r.ReadSingle(), PhysicsStarted = r.ReadBoolean(), PoseSource = r.ReadByte(),
                PoseFresh = r.ReadBoolean(), GroundY = r.ReadSingle(),
                Bone = new Vector3[t.Bones.Length], Body = new Vector3[t.Bones.Length], Runtime = new Vector3[t.Bones.Length],
            };
            for (var b = 0; b < t.Bones.Length; b++) f.Bone[b] = V(r);
            for (var b = 0; b < t.Bones.Length; b++) f.Body[b] = V(r);
            for (var b = 0; b < t.Bones.Length; b++)
            {
                if (float.IsNaN(f.Bone[b].X) && lastBone != null) f.Bone[b] = lastBone[b];
                if (float.IsNaN(f.Body[b].X) && lastBody != null) f.Body[b] = lastBody[b];
                f.Runtime[b] = f.PhysicsStarted && !float.IsNaN(f.Body[b].X) ? f.Body[b] : f.Bone[b];
            }
            if (withHead)
            {
                var q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                if (!float.IsNaN(q.X)) lastHead = q;
                f.HeadRotation = lastHead;
            }
            lastBone = f.Bone;
            if (f.Body.Any(v => !float.IsNaN(v.X))) lastBody = f.Body;
            t.Frames[i] = f;
        }
        return t;
    }

    private static Vector3 V(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    // ---- what the solver is fed ----

    private int[]? core;

    private int[] Core => core ??= new[] { "j_kosi", "j_sebo_c", "j_kao", "j_sako_l", "j_sako_r", "j_asi_d_l", "j_asi_d_r" }.Select(BoneIndex).ToArray();

    public BodyPoints Points(Vector3[] p)
    {
        var c = Core;
        return new BodyPoints(p[c[0]], p[c[1]], p[c[2]], p[c[3]], p[c[4]], p[c[5]], p[c[6]]);
    }

    /// <summary>The same trajectory up to frame <paramref name="index"/>, with that frame still read from the bones and
    /// not flagged as physics: the control for "what did the change of source do to this frame".</summary>
    public Trajectory WithoutSwitchAt(int index)
    {
        var frames = Frames.Take(index + 1).ToArray();
        var f = frames[index];
        frames[index] = new Frame
        {
            Time = f.Time, Dt = f.Dt, GroundY = f.GroundY, PhysicsStarted = false, PoseFresh = f.PoseFresh, PoseSource = f.PoseSource,
            Bone = f.Bone, Body = f.Body, Runtime = f.Bone, HeadRotation = f.HeadRotation,
        };
        return new Trajectory
        {
            Name = Name, IsPlayer = IsPlayer, Humanoid = Humanoid, Facing = Facing, ActivateGroundY = ActivateGroundY, Water = Water,
            ActivatePosition = ActivatePosition, Bones = Bones, GroundRays = GroundRays, Triangles = Triangles, Frames = frames, core = core,
        };
    }

    /// <summary>The trajectory turned about the vertical through its activation point and moved, for the
    /// invariance checks.</summary>
    public Trajectory Transformed(float yaw, Vector3 move)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        Vector3 T(Vector3 v) => float.IsNaN(v.X) ? v : ActivatePosition + Vector3.Transform(v - ActivatePosition, q) + move;
        return new Trajectory
        {
            Name = Name, IsPlayer = IsPlayer, Humanoid = Humanoid, Facing = Facing + yaw, ActivateGroundY = ActivateGroundY + move.Y,
            Water = Water + move.Y, ActivatePosition = ActivatePosition + move, Bones = Bones,
            GroundRays = GroundRays.Select(T).ToArray(), Triangles = Triangles.Select(T).ToArray(),
            Frames = Frames.Select(f => new Frame
            {
                Time = f.Time, Dt = f.Dt, GroundY = f.GroundY + move.Y, PhysicsStarted = f.PhysicsStarted, PoseFresh = f.PoseFresh,
                PoseSource = f.PoseSource, Bone = f.Bone.Select(T).ToArray(), Body = f.Body.Select(T).ToArray(), Runtime = f.Runtime.Select(T).ToArray(),
                HeadRotation = float.IsNaN(f.HeadRotation.X) ? f.HeadRotation : q * f.HeadRotation,
            }).ToArray(),
        };
    }
}

/// <summary>
/// The ground a recording knows, as the solver's world: the ragdoll's own ground ray hits (a 13 × 13 grid around
/// the death spot plus a few single rays) as a height field, the recorded exact ground triangles on top of it
/// (the player's recordings), the recorded water surface. Walls, ceilings and anything outside the patch are not in
/// a recording: outside the grid the nearest sample's height stands in.
/// </summary>
internal sealed class RecordedWorld : IDeathShotWorld
{
    private readonly Vector3[] rays;
    private readonly Vector3[] tris;
    private readonly float fallbackY;
    private readonly float water;
    private readonly float minX, minZ, step;
    private readonly int nx, nz;
    private readonly float[,]? grid;
    public bool HasTriangles => tris.Length >= 3;
    public bool HasGround => rays.Length > 0;

    public RecordedWorld(Trajectory t)
    {
        rays = t.GroundRays;
        tris = t.Triangles;
        fallbackY = t.Frames.Length > 0 ? t.Frames[^1].GroundY : t.ActivateGroundY;
        if (!float.IsFinite(fallbackY)) fallbackY = t.ActivateGroundY;
        water = t.Water;
        if (rays.Length >= 16)
        {
            // Rebuild the regular grid the ragdoll sampled: the most common spacing between neighbouring xs.
            var xs = rays.Select(p => p.X).Distinct().OrderBy(x => x).ToArray();
            var gaps = new List<float>();
            for (var i = 1; i < xs.Length; i++) if (xs[i] - xs[i - 1] > 0.05f) gaps.Add(xs[i] - xs[i - 1]);
            step = gaps.Count > 0 ? gaps.OrderBy(g => g).ElementAt(gaps.Count / 2) : 0.5f;
            minX = rays.Min(p => p.X);
            minZ = rays.Min(p => p.Z);
            nx = (int)MathF.Round((rays.Max(p => p.X) - minX) / step) + 1;
            nz = (int)MathF.Round((rays.Max(p => p.Z) - minZ) / step) + 1;
            if (nx is > 1 and < 200 && nz is > 1 and < 200)
            {
                grid = new float[nx, nz];
                var filled = new bool[nx, nz];
                foreach (var p in rays)
                {
                    var ix = (int)MathF.Round((p.X - minX) / step);
                    var iz = (int)MathF.Round((p.Z - minZ) / step);
                    if (ix < 0 || iz < 0 || ix >= nx || iz >= nz) continue;
                    if (MathF.Abs(minX + ix * step - p.X) > 0.02f || MathF.Abs(minZ + iz * step - p.Z) > 0.02f) continue;
                    grid[ix, iz] = p.Y;
                    filled[ix, iz] = true;
                }
                for (var ix = 0; ix < nx; ix++)
                for (var iz = 0; iz < nz; iz++)
                    if (!filled[ix, iz]) grid[ix, iz] = Nearest(minX + ix * step, minZ + iz * step);
            }
        }
    }

    private float Nearest(float x, float z)
    {
        var best = float.MaxValue;
        var y = fallbackY;
        foreach (var p in rays)
        {
            var d = (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z);
            if (d < best) { best = d; y = p.Y; }
        }
        return y;
    }

    /// <summary>The height field alone (bilinear inside the grid, clamped outside).</summary>
    public float FieldHeight(float x, float z)
    {
        if (grid == null) return rays.Length > 0 ? Nearest(x, z) : fallbackY;
        var fx = Math.Clamp((x - minX) / step, 0f, nx - 1);
        var fz = Math.Clamp((z - minZ) / step, 0f, nz - 1);
        var ix = Math.Min((int)fx, nx - 2);
        var iz = Math.Min((int)fz, nz - 2);
        var tx = fx - ix;
        var tz = fz - iz;
        return (grid[ix, iz] * (1 - tx) + grid[ix + 1, iz] * tx) * (1 - tz) + (grid[ix, iz + 1] * (1 - tx) + grid[ix + 1, iz + 1] * tx) * tz;
    }

    public float? GroundBelow(Vector3 from)
    {
        var y = FieldHeight(from.X, from.Z);
        // Exact triangles: the highest one under the ray start.
        float? best = null;
        for (var i = 0; i + 2 < tris.Length; i += 3)
            if (RayTriangle(from, -Vector3.UnitY, tris[i], tris[i + 1], tris[i + 2], out var d) && (best == null || d < best)) best = d;
        if (best.HasValue) return MathF.Max(from.Y - best.Value, MathF.Min(y, from.Y));
        return y <= from.Y ? y : y; // a start below the field still reports the field: the caller lifts the eye onto it
    }

    public float? WaterBelow(Vector3 from) => float.IsNaN(water) ? null : water;

    public bool Blocked(Vector3 from, Vector3 to, out float distance) => Blocked(from, to, out distance, out _);

    /// <summary>With the normal of what stopped the ray: the triangle's (turned to face up), or straight up for the
    /// sampled height field. Everything recorded is ground; there are no walls in the recordings.</summary>
    public bool Blocked(Vector3 from, Vector3 to, out float distance, out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.UnitY;
        var d = to - from;
        var length = d.Length();
        if (length < 1e-4f) return false;
        d /= length;
        var best = float.MaxValue;
        for (var i = 0; i + 2 < tris.Length; i += 3)
            if (RayTriangle(from, d, tris[i], tris[i + 1], tris[i + 2], out var t) && t < length && t < best)
            {
                best = t;
                var n = Vector3.Cross(tris[i + 1] - tris[i], tris[i + 2] - tris[i]);
                normal = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n.Y < 0f ? -n : n) : Vector3.UnitY;
            }
        // The height field, marched: a ray dips under the sampled ground.
        const float march = 0.05f;
        for (var s = march; s < length && s < best; s += march)
        {
            var p = from + d * s;
            if (p.Y < FieldHeight(p.X, p.Z) - 0.01f) { best = s; normal = Vector3.UnitY; break; }
        }
        if (best == float.MaxValue) return false;
        distance = best;
        return true;
    }

    /// <summary>The exact triangles alone (the "view blocked by ground triangles" metric).</summary>
    public bool BlockedByTriangles(Vector3 from, Vector3 to)
    {
        var d = to - from;
        var length = d.Length();
        if (length < 1e-4f) return false;
        d /= length;
        for (var i = 0; i + 2 < tris.Length; i += 3)
            if (RayTriangle(from, d, tris[i], tris[i + 1], tris[i + 2], out var t) && t > 0.02f && t < length - 0.02f) return true;
        return false;
    }

    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        var inv = 1f / det;
        var s = o - a;
        var u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 1e-4f;
    }
}
