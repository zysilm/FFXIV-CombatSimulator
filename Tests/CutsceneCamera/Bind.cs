using System.Numerics;

namespace CutsceneCamera;

/// <summary>
/// What does the second number of an attach slot ("point") mean? Each candidate mapping turns (actor, point) into a
/// world frame; the attached shots are then evaluated and scored against the cutscene's own actors. The mapping that
/// makes the camera look at people and stay above the ground is the right one — same idea as the Euler-order test.
///
/// Actors: C018 placement in the SAME timeline (latest at or before the shot, else the timeline's first one), else the
/// setup timeline. Body: Hyur midlander male reference pose (c0101) for every actor — no per-actor race is resolved,
/// so heights are off by up to ~±0.6 m for Lalafell/Roegadyn and meaningless for monsters.
/// </summary>
internal static class Bind
{
    public enum Mapping { EidBindPoint, EidBindPointBoneRot, SklbBoneIndex, ActorRoot, EidNoYaw }

    public static Body? DefaultBody;

    public sealed class Body
    {
        public required Skeleton Skel;
        public required Dictionary<int, BindPoint> Eid;
        public Vector3 Head => Skel.Model[Skel.Index("j_kao")].Translation;
    }

    private static Matrix4x4 Euler(Vector3 r) =>
        Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationZ(r.Z);

    /// <summary>All C018 placements: timeline, time, world matrix (rotation XYZ radians + translation).</summary>
    public static Dictionary<int, List<(int Tl, int Time, Matrix4x4 M)>> Placements(CutbFile c)
    {
        var res = new Dictionary<int, List<(int, int, Matrix4x4)>>();
        foreach (var tl in c.Timelines)
            foreach (var it in tl.Items.Where(i => i.Magic == "C018" && i.OwnerActor >= 0))
            {
                var p = new Vector3(it.Float(c.Data, 2), it.Float(c.Data, 3), it.Float(c.Data, 4));
                if (p == Vector3.Zero) continue;
                var r = new Vector3(it.Float(c.Data, 5), it.Float(c.Data, 6), it.Float(c.Data, 7));
                if (!float.IsFinite(r.X) || MathF.Abs(r.X) > 10) r.X = 0;
                if (!float.IsFinite(r.Z) || MathF.Abs(r.Z) > 10) r.Z = 0;
                if (!float.IsFinite(r.Y) || MathF.Abs(r.Y) > 10) r.Y = 0;
                if (!res.TryGetValue(it.OwnerActor, out var l)) res[it.OwnerActor] = l = new();
                l.Add((tl.Index, it.Time, Euler(r) * Matrix4x4.CreateTranslation(p)));
            }
        return res;
    }

    public static Matrix4x4? ActorAt(Dictionary<int, List<(int Tl, int Time, Matrix4x4 M)>> pl, int actor, int tl, int time)
    {
        if (!pl.TryGetValue(actor, out var l)) return null;
        var same = l.Where(x => x.Tl == tl).OrderBy(x => x.Time).ToList();
        if (same.Count > 0) return (same.LastOrDefault(x => x.Time <= time) is { M: var m } hit && hit != default ? m : same[0].M);
        var setup = l.Where(x => x.Tl == 0).ToList();
        return setup.Count > 0 ? setup[^1].M : null;
    }

    public static Matrix4x4 PointFrame(Mapping map, Body b, int point, Matrix4x4 actor)
    {
        Matrix4x4 local;
        switch (map)
        {
            case Mapping.ActorRoot:
                local = Matrix4x4.Identity; break;
            case Mapping.SklbBoneIndex:
                local = point >= 0 && point < b.Skel.Model.Length ? Matrix4x4.CreateTranslation(b.Skel.Model[point].Translation) : Matrix4x4.Identity;
                break;
            default:
                if (!b.Eid.TryGetValue(point, out var bp)) { local = Matrix4x4.Identity; break; }
                var bi = b.Skel.Index(bp.Bone);
                var bone = bi >= 0 ? b.Skel.Model[bi] : Matrix4x4.Identity;
                // Offset is in the bone's own frame (n_root is identity, so the big height offsets are plain +Y).
                var pos = Vector3.Transform(bp.Offset, bone);
                local = map == Mapping.EidBindPointBoneRot
                    ? Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromRotationMatrix(bone)) * Matrix4x4.CreateTranslation(pos)
                    : Matrix4x4.CreateTranslation(pos);
                break;
        }
        if (map == Mapping.EidNoYaw) return local * Matrix4x4.CreateTranslation(actor.Translation);
        return local * actor;
    }

    public static void Run(IEnumerable<CutbFile> files, Body body, int maxShots = 200000)
    {
        var maps = Enum.GetValues<Mapping>();
        var angle = maps.ToDictionary(m => m, _ => new List<float>());
        var below = maps.ToDictionary(m => m, _ => 0);
        var headInFrame = maps.ToDictionary(m => m, _ => 0);
        var distances = maps.ToDictionary(m => m, _ => new List<float>());
        int scored = 0, rootScored = 0, aimScored = 0;
        var front = new int[2, 2];
        int yawWins = 0, noYawWins = 0;
        var perPoint = new Dictionary<int, (int N, float SumAngEid, float SumAngRoot)>();
        foreach (var c in files)
        {
            var pl = Placements(c);
            foreach (var tl in c.Timelines)
            {
                var actorsHere = pl.Keys.Select(a => (a, M: ActorAt(pl, a, tl.Index, 0))).Where(x => x.M != null).ToList();
                if (actorsHere.Count == 0) continue;
                foreach (var s in CameraShot.FromTimeline(c, tl))
                {
                    if (s.Channels == null || (s.Root.Actor == 255 && s.Aim.Actor == 255 && s.Eye.Actor == 255)) continue;
                    // Every attached actor must have a placement, otherwise nothing can be said.
                    var slots = new[] { s.Root, s.Eye, s.Aim }.Where(a => a.Actor != 255).ToList();
                    if (slots.Any(a => ActorAt(pl, a.Actor, tl.Index, s.Start) == null)) continue;
                    if (scored >= maxShots) break;
                    scored++;
                    if (s.Root.Actor != 255) rootScored++;
                    if (s.Aim.Actor != 255) aimScored++;
                    var frame = s.Duration / 2;
                    var heads = actorsHere.Select(x => Vector3.Transform(body.Head, ActorAt(pl, x.a, tl.Index, s.Start + frame)!.Value)).ToList();
                    var feet = actorsHere.Select(x => ActorAt(pl, x.a, tl.Index, s.Start + frame)!.Value.Translation).ToList();
                    var angs = new Dictionary<Mapping, float>();
                    foreach (var m in maps)
                    {
                        var e = s.Evaluate(frame, a => a.Actor == 255 ? Matrix4x4.Identity : PointFrame(m, body, a.Point, ActorAt(pl, a.Actor, tl.Index, s.Start + frame)!.Value));
                        var f = e.Aim - e.Eye;
                        if (f.LengthSquared() < 1e-6f) continue;
                        f = Vector3.Normalize(f);
                        var best = 180f;
                        foreach (var h in heads)
                        {
                            var to = h - e.Eye;
                            if (to.Length() < 0.15f || to.Length() > 40f) continue;
                            best = MathF.Min(best, MathF.Acos(Math.Clamp(Vector3.Dot(f, Vector3.Normalize(to)), -1, 1)) * 180f / MathF.PI);
                        }
                        angle[m].Add(best);
                        angs[m] = best;
                        if (best < e.Fov / 2) headInFrame[m]++;
                        // Ground under the eye: the lowest foot among actors within 6 m horizontally.
                        var near = feet.Where(p => new Vector2(p.X - e.Eye.X, p.Z - e.Eye.Z).Length() < 6f).ToList();
                        if (near.Count > 0 && e.Eye.Y < near.Min(p => p.Y) - 0.15f) below[m]++;
                        distances[m].Add(heads.Min(h => Vector3.Distance(h, e.Eye)));
                    }
                    if (angs.TryGetValue(Mapping.EidBindPoint, out var aw) && angs.TryGetValue(Mapping.EidNoYaw, out var an) && MathF.Abs(aw - an) > 5f) { if (aw < an) yawWins++; else noYawWins++; }
                    // Front test: root-attached shot whose aim lands on the attached actor's own head (±0.6 m):
                    // is the eye in front of that actor (+Z = facing)? Films shoot faces; a wrong yaw makes this ~50%.
                    if (s.Root.Actor != 255 && s.Aim.Actor == 255)
                        foreach (var (m, slot) in new[] { (Mapping.EidBindPoint, 0), (Mapping.EidNoYaw, 1) })
                        {
                            var am = ActorAt(pl, s.Root.Actor, tl.Index, s.Start + frame)!.Value;
                            var e = s.Evaluate(frame, a => a.Actor == 255 ? Matrix4x4.Identity : PointFrame(m, body, a.Point, ActorAt(pl, a.Actor, tl.Index, s.Start + frame)!.Value));
                            var head = Vector3.Transform(body.Head, am);
                            if (Vector3.Distance(e.Aim, head) > 0.6f) continue;
                            var fwd = Vector3.TransformNormal(Vector3.UnitZ, am);
                            var toEye = e.Eye - am.Translation;
                            if (new Vector2(toEye.X, toEye.Z).Length() < 0.3f) continue;
                            front[slot, 1]++;
                            if (fwd.X * toEye.X + fwd.Z * toEye.Z > 0) front[slot, 0]++;
                        }
                    foreach (var p in slots.Select(a => a.Point).Distinct())
                    {
                        var cur = perPoint.GetValueOrDefault(p);
                        perPoint[p] = (cur.N + 1, cur.SumAngEid + angs.GetValueOrDefault(Mapping.EidBindPoint, 90), cur.SumAngRoot + angs.GetValueOrDefault(Mapping.ActorRoot, 90));
                    }
                }
            }
        }
        Console.WriteLine($"attached shots scored {scored} (root attached {rootScored}, aim attached {aimScored}); frame = mid-shot");
        Console.WriteLine("mapping               median angle view→nearest head   within 10°   head inside FOV/2   eye under ground   median eye→head m");
        foreach (var m in maps)
        {
            var a = angle[m].OrderBy(x => x).ToList();
            var d = distances[m].OrderBy(x => x).ToList();
            if (a.Count == 0) continue;
            Console.WriteLine($"  {m,-20} {a[a.Count / 2],10:F1}°  {a.Count(x => x < 10) * 100f / a.Count,20:F1}%  {headInFrame[m] * 100f / a.Count,14:F1}%  {below[m] * 100f / a.Count,14:F1}%  {d[d.Count / 2],12:F2}");
        }
        Console.WriteLine($"actor yaw applied to the attach frame: wins {yawWins} vs without yaw {noYawWins} (shots where the two differ by >5°)");
        Console.WriteLine($"eye in front of the subject it is aimed at (root-attached, aim on own head): with yaw {front[0, 0]}/{front[0, 1]}, without yaw {front[1, 0]}/{front[1, 1]}");
        Console.WriteLine("per attach point (shots, mean angle EID vs actor-root):");
        foreach (var kv in perPoint.OrderByDescending(k => k.Value.N).Take(12))
            Console.WriteLine($"  pt {kv.Key,3} ({(body.Eid.TryGetValue(kv.Key, out var bp) ? $"{bp.Bone}+{bp.Offset.Y:F2}" : "no EID entry")}): {kv.Value.N,5} shots  EID {kv.Value.SumAngEid / kv.Value.N,5:F1}°  root {kv.Value.SumAngRoot / kv.Value.N,5:F1}°");
    }
}
