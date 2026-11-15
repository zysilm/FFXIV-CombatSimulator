using System.Numerics;
using CombatSimulator.Animation;
using CombatSimulator.Npcs;
using Lumina;

/// <summary>
/// Anchored grab across races (docs/ENEMY_CONTROL_GRAB.md §3), on installed game data, bind pose: points are picked on
/// one race's naked body with the production picking math (nearest triangle, owning bone, bone-local point), resolved
/// on another race's skeleton, and measured against that race's own naked body. "Before" is how far the resolved point
/// is from that body's surface; "after" is the same once the point is moved along its normal onto the surface (the
/// re-snap at grab start, capped at 5 cm). Offline the re-snap targets the real mesh; in the game it targets the
/// ragdoll's surface profile, which approximates it.
///
/// Bodies as RagdollHullFit builds them: the smallclothes models (e0000) of the Midlander man or woman, reshaped for the
/// race by the game's race deformer. Roegadyn and Hrothgar men and Lalafell are left out: their offline skeletons and
/// models do not agree in size (the same exclusion as RagdollHullFit's "unverifiable offline").
/// </summary>
internal static class GrabAnchorCrossRace
{
    private const float SnapCap = 0.05f;

    private sealed record Body(int Race, Vector3[] Positions, (int Bone, float Weight)[][] Influences,
        int[] Triangles, string[] BoneNames, Skeleton Skeleton);

    private static readonly string[] AimBones = { "j_kubi", "j_sebo_c", "j_sebo_b", "j_sebo_a", "j_kosi", "j_sako_l", "j_ude_a_l", "j_asi_a_l" };

    internal static void Run(GameData game, Func<byte[], ModelGeometryData?> parse)
    {
        var pbd = new RenderedRaceDeformation(game.GetFile("chara/xls/boneDeformer/human.pbd")!.Data);
        var bodies = new List<Body>();
        var races = new[] { 101, 301, 501, 701, 1301, 1701 }.Select(r => (Race: r, Donor: 101))
            .Concat(new[] { 201, 401, 601, 801, 1001, 1401, 1601, 1801 }.Select(r => (Race: r, Donor: 201)));
        foreach (var (race, donor) in races)
        {
            var sklb = game.GetFile($"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb")?.Data;
            if (sklb == null) continue;
            Skeleton skeleton;
            try { skeleton = Skeleton.Read(sklb); }
            catch (Exception ex) { Console.WriteLine($"GrabAnchor cross-race: c{race:D4} skeleton unreadable ({ex.Message})"); continue; }
            var boneIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < skeleton.Names.Length; i++) boneIndex.TryAdd(skeleton.Names[i], i);

            var positions = new List<Vector3>();
            var influences = new List<(int, float)[]>();
            var triangles = new List<int>();
            foreach (var part in new[] { "top", "dwn", "glv" })
            {
                var bytes = game.GetFile($"chara/equipment/e0000/model/c{donor:D4}e0000_{part}.mdl")?.Data;
                if (bytes == null || parse(bytes) is not { } data) continue;
                string[] names = data.BoneNameOffsets.Select(offset => RenderedGeometryDecoder.StringAt(data.Strings, offset)).ToArray();
                Matrix4x4[]? deform = null;
                if (race != donor && !TryRaceDeformation(pbd, donor, race, names, skeleton, out deform)) continue;
                var identity = Enumerable.Repeat(Matrix4x4.Identity, names.Length).ToArray();
                var vertices = new List<AdvancedRagdoll.RenderedGeometryVertex>();
                var local = new List<int>();
                var lod = data.Lods[0];
                for (int mi = lod.MeshIndex; mi < lod.MeshIndex + lod.MeshCount; mi++)
                {
                    var mesh = data.Meshes[mi];
                    if (mesh.BoneTableIndex >= data.BoneTables.Length) continue;
                    var map = data.BoneTables[mesh.BoneTableIndex].BoneIndex.Select(x => (int)x).ToArray();
                    RenderedGeometryDecoder.Append(data, 0, mi, 1, uint.MaxValue, new bool[data.Shapes.Length], map, identity, deform,
                        Matrix4x4.Identity, vertices, local);
                }
                var start = positions.Count;
                foreach (var v in vertices)
                {
                    positions.Add(v.WorldPosition);
                    influences.Add(v.Influences.Select(i => (i.BoneIndex >= 0 && i.BoneIndex < names.Length
                        ? boneIndex.GetValueOrDefault(names[i.BoneIndex], -1) : -1, i.Weight)).ToArray());
                }
                foreach (var index in local) triangles.Add(start + index);
            }
            if (triangles.Count == 0) continue;
            bodies.Add(new Body(race, positions.ToArray(), influences.ToArray(), triangles.ToArray(), skeleton.Names, skeleton));
        }
        if (bodies.Count < 2) { Console.WriteLine($"GrabAnchor cross-race: only {bodies.Count} usable bodies, skipped"); return; }

        // Picks on every body: horizontal rays at each aim bone from the front (+Z faces forward), back and left.
        var picks = new Dictionary<int, List<(string Bone, Vector3 Offset, Vector3 Normal)>>();
        foreach (var body in bodies)
        {
            var list = new List<(string, Vector3, Vector3)>();
            foreach (var aim in AimBones)
            {
                var index = body.Skeleton.Index(aim);
                if (index < 0) continue;
                var at = body.Skeleton.Model[index].Translation;
                foreach (var dir in new[] { -Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX })
                {
                    if (!EnemyControlGrabAnchorMath.TryPick(at - dir * 3f, dir, body.Positions, body.Triangles, out var hit)) continue;
                    var t = hit.Triangle;
                    var owner = EnemyControlGrabAnchorMath.OwningBone(hit.Barycentric, body.Influences[body.Triangles[t]],
                        body.Influences[body.Triangles[t + 1]], body.Influences[body.Triangles[t + 2]]);
                    if (owner < 0 || owner >= body.BoneNames.Length) continue;
                    var bone = body.BoneNames[owner];
                    var b = body.Skeleton.Index(bone);
                    if (b < 0) continue;
                    var (offset, normal) = EnemyControlGrabAnchorMath.ToBoneLocal(Root, Translation(body.Skeleton, b),
                        Rotation(body.Skeleton, b), hit.Point, hit.Normal);
                    list.Add((bone, offset, normal));
                }
            }
            picks[body.Race] = list;
        }

        var allBefore = new List<float>();
        var allAfter = new List<float>();
        var sameWorst = 0f;
        var snapped = 0;
        var resolved = 0;
        var rows = new List<string>();
        foreach (var from in bodies)
        {
            var before = new List<float>();
            var after = new List<float>();
            foreach (var to in bodies)
            {
                foreach (var (bone, offset, normal) in picks[from.Race])
                {
                    var b = to.Skeleton.Index(bone);
                    if (b < 0) continue;
                    var (point, n) = EnemyControlGrabAnchorMath.ToWorld(Root, Translation(to.Skeleton, b), Rotation(to.Skeleton, b), offset, normal);
                    var d0 = DistanceToSurface(to, point);
                    if (to.Race == from.Race) { sameWorst = MathF.Max(sameWorst, d0); continue; }
                    resolved++;
                    var p = point;
                    if (NearestAlongNormal(to, p, n) is { } surface &&
                        EnemyControlGrabAnchorMath.SnapAlongNormal(ref p, n, surface, SnapCap) != 0f)
                        snapped++;
                    var d1 = DistanceToSurface(to, p);
                    before.Add(d0); after.Add(d1);
                }
            }
            allBefore.AddRange(before); allAfter.AddRange(after);
            if (before.Count > 0)
                rows.Add($"  picked on c{from.Race:D4} ({picks[from.Race].Count} points) -> other races: median {Median(before) * 1000f:F1} / max {before.Max() * 1000f:F1} mm before, " +
                         $"median {Median(after) * 1000f:F1} / max {after.Max() * 1000f:F1} mm after re-snap");
        }
        Console.WriteLine($"GrabAnchor cross-race: {bodies.Count} races (bind pose, naked bodies: smallclothes top/dwn/glv through the race deformer), same-race round trip worst {sameWorst * 1000f:F3} mm");
        foreach (var row in rows) Console.WriteLine(row);
        if (allBefore.Count > 0)
            Console.WriteLine($"GrabAnchor cross-race: {resolved} resolved points, distance to the other race's surface median {Median(allBefore) * 1000f:F1} mm " +
                              $"(p90 {Percentile(allBefore, 0.9f) * 1000f:F1}, max {allBefore.Max() * 1000f:F1}) before re-snap, " +
                              $"median {Median(allAfter) * 1000f:F1} mm (p90 {Percentile(allAfter, 0.9f) * 1000f:F1}, max {allAfter.Max() * 1000f:F1}) after; " +
                              $"{snapped}/{resolved} snapped within {SnapCap * 100f:F0} cm");
        if (sameWorst > 0.0005f) throw new Exception($"Same-race anchor does not land on the picked surface ({sameWorst * 1000f:F2} mm)");
    }

    private static readonly GrabRootTransform Root = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    // The model's bones first (their indices are the model's own), then the rest of the skeleton, with the skeleton's
    // parents: what StructuralMeshSampler.RaceDeformation hands the deformer in the plugin.
    private static bool TryRaceDeformation(RenderedRaceDeformation pbd, int source, int target, string[] modelBones,
        Skeleton skeleton, out Matrix4x4[]? matrices)
    {
        var names = new List<string>(modelBones);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < modelBones.Length; i++) index.TryAdd(modelBones[i], i);
        foreach (var bone in skeleton.Names)
            if (!index.ContainsKey(bone)) { index[bone] = names.Count; names.Add(bone); }
        var parents = new int[names.Count];
        Array.Fill(parents, -1);
        for (var i = 0; i < skeleton.Names.Length; i++)
            if (skeleton.Parents[i] >= 0) parents[index[skeleton.Names[i]]] = index[skeleton.Names[skeleton.Parents[i]]];
        var ok = pbd.TryBuild((ushort)source, (ushort)target, names.ToArray(), parents, out var built);
        matrices = built;
        return ok;
    }

    private static Vector3 Translation(Skeleton skeleton, int bone) => skeleton.Model[bone].Translation;

    private static Quaternion Rotation(Skeleton skeleton, int bone)
        => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(skeleton.Model[bone]));

    private static Vector3? NearestAlongNormal(Body body, Vector3 point, Vector3 normal)
    {
        // The surface crossing nearest to the point on the line through it along the normal, either side.
        Vector3? best = null;
        var bestDistance = float.MaxValue;
        foreach (var dir in new[] { normal, -normal })
            if (EnemyControlGrabAnchorMath.TryPick(point - dir * 1e-4f, dir, body.Positions, body.Triangles, out var hit) &&
                hit.Distance < bestDistance)
            {
                bestDistance = hit.Distance;
                best = hit.Point;
            }
        return best;
    }

    private static float DistanceToSurface(Body body, Vector3 point)
    {
        var best = float.MaxValue;
        var t = body.Triangles;
        for (int i = 0; i + 2 < t.Length; i += 3)
            best = MathF.Min(best, Vector3.DistanceSquared(point, ClosestOnTriangle(point, body.Positions[t[i]], body.Positions[t[i + 1]], body.Positions[t[i + 2]])));
        return MathF.Sqrt(best);
    }

    // Ericson, Real-Time Collision Detection 5.1.5.
    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    private static float Median(List<float> values) => Percentile(values, 0.5f);

    private static float Percentile(List<float> values, float q)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[Math.Clamp((int)MathF.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
    }
}
