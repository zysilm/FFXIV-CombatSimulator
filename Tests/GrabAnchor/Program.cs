// Offline checks of the anchored grab (docs/ENEMY_CONTROL_GRAB.md §3). Run: dotnet run -c Release
using System.Numerics;
using CombatSimulator.Npcs;
using Newtonsoft.Json;

int checks = 0, failures = 0;
void Check(bool ok, string what)
{
    checks++;
    if (ok) return;
    failures++;
    Console.WriteLine($"FAIL: {what}");
}
void Near(Vector3 a, Vector3 b, float tolerance, string what)
    => Check(Vector3.Distance(a, b) <= tolerance, $"{what}: {a} vs {b} ({Vector3.Distance(a, b):E2})");

var random = new Random(20261115);
Vector3 RandomVector(float range) => new((float)(random.NextDouble() * 2 - 1) * range,
    (float)(random.NextDouble() * 2 - 1) * range, (float)(random.NextDouble() * 2 - 1) * range);
Quaternion RandomRotation() => Quaternion.Normalize(new Quaternion(RandomVector(1f), (float)(random.NextDouble() * 2 - 1)));

// ── 1. Bone-local <-> world round trip, with rotation and (non-uniform) scale ──
var worst = 0f;
var worstNormal = 0f;
for (var i = 0; i < 2000; i++)
{
    var root = new GrabRootTransform(RandomVector(500f), RandomRotation(),
        i % 3 == 0 ? new Vector3(0.3f + (float)random.NextDouble() * 3f) : new Vector3(0.5f, 1.4f, 2.2f));
    var bonePosition = RandomVector(1.5f);
    var boneRotation = RandomRotation();
    var point = root.Position + RandomVector(2f);
    var normal = Vector3.Normalize(RandomVector(1f) + new Vector3(0.01f));
    var (offset, localNormal) = EnemyControlGrabAnchorMath.ToBoneLocal(root, bonePosition, boneRotation, point, normal);
    var (back, backNormal) = EnemyControlGrabAnchorMath.ToWorld(root, bonePosition, boneRotation, offset, localNormal);
    worst = MathF.Max(worst, Vector3.Distance(back, point));
    worstNormal = MathF.Max(worstNormal, Vector3.Distance(backNormal, normal));
}
Check(worst < 1e-4f, $"round trip point error {worst:E2}");
Check(worstNormal < 1e-4f, $"round trip normal error {worstNormal:E2}");
Console.WriteLine($"Round trip: 2000 random roots/bones, worst point error {worst * 1000f:F3} mm (points up to 500 m from the world origin), normal {worstNormal:E1}");

// The point rides with its bone: rotate and move the bone, the anchor follows rigidly.
{
    var root = new GrabRootTransform(new Vector3(10, 2, -3), Quaternion.CreateFromYawPitchRoll(0.7f, 0f, 0f), Vector3.One);
    var bonePos = new Vector3(0.2f, 1.4f, 0.05f);
    var boneRot = Quaternion.CreateFromYawPitchRoll(0.1f, 0.4f, -0.2f);
    var surfaceModel = bonePos + Vector3.Transform(new Vector3(0f, 0.05f, 0.04f), boneRot);
    var world = EnemyControlGrabAnchorMath.ModelToWorld(root, surfaceModel);
    var (offset, n) = EnemyControlGrabAnchorMath.ToBoneLocal(root, bonePos, boneRot, world, Vector3.UnitZ);
    var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.1f);
    var newBonePos = bonePos + new Vector3(0f, -0.3f, 0.2f);
    var newBoneRot = Quaternion.Normalize(turn * boneRot);
    var expectedModel = newBonePos + Vector3.Transform(surfaceModel - bonePos, turn);
    var (moved, _) = EnemyControlGrabAnchorMath.ToWorld(root, newBonePos, newBoneRot, offset, n);
    Near(moved, EnemyControlGrabAnchorMath.ModelToWorld(root, expectedModel), 1e-5f, "anchor follows a re-posed bone");

    // Picked on an actor drawn at scale 1, resolved on the same skeleton drawn at 1.5: the point scales with it.
    var big = root with { Scale = new Vector3(1.5f) };
    var (scaled, _) = EnemyControlGrabAnchorMath.ToWorld(big, bonePos, boneRot, offset, n);
    Near(scaled, EnemyControlGrabAnchorMath.ModelToWorld(big, surfaceModel), 1e-5f, "anchor scales with the actor");
    Check(MathF.Abs(Vector3.Distance(scaled, big.Position) / Vector3.Distance(world, root.Position) - 1.5f) < 1e-4f,
        "scaled anchor is 1.5x as far from the root");
}

// ── 2. Ray–triangle hit and owning bone on a synthetic skinned mesh ──
{
    // Two boxes: bone 0's (a torso, 0.4 m) at the origin, bone 1's (an arm, 0.1 m) in front of it at z = +0.5.
    var positions = new List<Vector3>();
    var influences = new List<(int Bone, float Weight)[]>();
    var triangles = new List<int>();
    void Box(Vector3 centre, Vector3 half, Func<Vector3, (int, float)[]> weights)
    {
        var start = positions.Count;
        for (var c = 0; c < 8; c++)
        {
            var p = centre + new Vector3((c & 1) == 0 ? -half.X : half.X, (c & 2) == 0 ? -half.Y : half.Y, (c & 4) == 0 ? -half.Z : half.Z);
            positions.Add(p);
            influences.Add(weights(p));
        }
        int[] faces = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
        foreach (var f in faces) triangles.Add(start + f);
    }
    Box(Vector3.Zero, new Vector3(0.2f), _ => new[] { (0, 1f) });
    // The arm box blends into bone 0 towards its back face (z small) and is bone 1's towards the front.
    Box(new Vector3(0f, 0f, 0.5f), new Vector3(0.05f), p => p.Z > 0.5f ? new[] { (1, 0.9f), (0, 0.1f) } : new[] { (1, 0.3f), (0, 0.7f) });

    var camera = new Vector3(0.01f, 0.02f, 3f);
    Check(EnemyControlGrabAnchorMath.TryPick(camera, -Vector3.UnitZ, positions, triangles, out var hit), "ray from the front hits");
    Near(hit.Point, new Vector3(0.01f, 0.02f, 0.55f), 1e-5f, "nearest hit is the arm's front face, not the torso behind it");
    Near(hit.Normal, Vector3.UnitZ, 1e-5f, "normal faces the camera");
    var tri = hit.Triangle;
    var owner = EnemyControlGrabAnchorMath.OwningBone(hit.Barycentric, influences[triangles[tri]], influences[triangles[tri + 1]],
        influences[triangles[tri + 2]]);
    Check(owner == 1, $"front of the arm is owned by bone 1 (got {owner})");

    // From the side, through the arm's side face: corners are half front (bone 1 heavy), half back (bone 0 heavy).
    Check(EnemyControlGrabAnchorMath.TryPick(new Vector3(3f, 0f, 0.46f), -Vector3.UnitX, positions, triangles, out var side), "side ray hits");
    var sideOwner = EnemyControlGrabAnchorMath.OwningBone(side.Barycentric, influences[triangles[side.Triangle]],
        influences[triangles[side.Triangle + 1]], influences[triangles[side.Triangle + 2]]);
    Check(sideOwner == 0, $"back half of the arm's side is owned by bone 0 (got {sideOwner})");
    Check(EnemyControlGrabAnchorMath.TryPick(new Vector3(3f, 0f, 0.54f), -Vector3.UnitX, positions, triangles, out var front), "side ray hits near the front");
    var frontOwner = EnemyControlGrabAnchorMath.OwningBone(front.Barycentric, influences[triangles[front.Triangle]],
        influences[triangles[front.Triangle + 1]], influences[triangles[front.Triangle + 2]]);
    Check(frontOwner == 1, $"front half of the arm's side is owned by bone 1 (got {frontOwner})");

    // A ray from inside the back of the torso still finds the surface facing it; a ray that misses finds nothing.
    Check(EnemyControlGrabAnchorMath.TryPick(new Vector3(0f, 0f, -3f), Vector3.UnitZ, positions, triangles, out var back) &&
          MathF.Abs(back.Point.Z + 0.2f) < 1e-5f && Vector3.Dot(back.Normal, -Vector3.UnitZ) > 0.999f, "ray from behind hits the back face, normal towards it");
    Check(!EnemyControlGrabAnchorMath.TryPick(new Vector3(2f, 2f, 3f), -Vector3.UnitZ, positions, triangles, out _), "missing ray finds nothing");

    // Pick → bone-local → re-pose: the picked point stays on the skinned surface (rigid influence: exactly).
    var root = new GrabRootTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
    var (offset, n) = EnemyControlGrabAnchorMath.ToBoneLocal(root, new Vector3(0f, 0f, 0.5f), Quaternion.Identity, hit.Point, hit.Normal);
    var pose = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
    var (posed, posedNormal) = EnemyControlGrabAnchorMath.ToWorld(root, new Vector3(0f, 0f, 0.5f), pose, offset, n);
    Near(posed, new Vector3(0.05f, 0.02f, 0.5f - 0.01f), 1e-5f, "picked point re-posed with its bone");
    Near(posedNormal, Vector3.UnitX, 1e-5f, "picked normal re-posed with its bone");
}

// ── 3. Re-snap along the normal ──
{
    var p = new Vector3(0f, 1f, 0.10f);
    var moved = EnemyControlGrabAnchorMath.SnapAlongNormal(ref p, Vector3.UnitZ, new Vector3(0.03f, 1.01f, 0.08f), 0.05f);
    Check(MathF.Abs(moved + 0.02f) < 1e-6f, $"snap moved {moved}");
    Near(p, new Vector3(0f, 1f, 0.08f), 1e-6f, "snap only changes depth along the normal");
    var q = new Vector3(0f, 1f, 0.10f);
    Check(EnemyControlGrabAnchorMath.SnapAlongNormal(ref q, Vector3.UnitZ, new Vector3(0f, 1f, 0.30f), 0.05f) == 0f &&
          q == new Vector3(0f, 1f, 0.10f), "snap past the cap leaves the point");
}

// ── 4. Storage: old JSON, profile round trip, copy, de-duplication ──
// Dalamud saves plugin configurations with Newtonsoft and object type names.
var settings = new JsonSerializerSettings
{
    TypeNameHandling = TypeNameHandling.Objects,
    TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
};
{
    // A profile as saved before anchors existed.
    const string old = """
        {"Name":"Embrace","Pairs":[{"NpcBone":"j_te_r","PlayerBone":"j_sebo_c"},{"NpcBone":"j_te_l","PlayerBone":"j_asi_b_l"}],
         "GrabEmoteId":0,"GrabAnyBone":true,"GrabGripReach":1.55}
        """;
    var profile = JsonConvert.DeserializeObject<EnemyControlGrabProfile>(old, settings)!;
    Check(profile.Pairs.Count == 2 && profile.Pairs.All(p => p.NpcAnchor == null && p.PlayerAnchor == null), "old JSON: anchors are null");
    Check(profile.Pairs[0].NpcBone == "j_te_r" && profile.Pairs[1].PlayerBone == "j_asi_b_l", "old JSON: bones kept");
    var copy = EnemyControlGrabPairList.CopyForUse(profile.Pairs);
    Check(copy.Count == 2 && copy.All(p => p.NpcAnchor == null && p.PlayerAnchor == null), "old pairs load unchanged");
    Check(!JsonConvert.SerializeObject(profile.Pairs[0], settings).Contains("Anchor\":{"), "pair without anchors writes no anchor object");
}
{
    var anchor = new EnemyControlGrabAnchor { Bone = "j_kubi", Offset = new Vector3(0.01f, -0.02f, 0.071f), Normal = Vector3.Normalize(new Vector3(0.1f, 0.2f, 1f)), PickScale = 1.25f };
    var profile = new EnemyControlGrabProfile
    {
        Name = "Throat",
        Pairs =
        {
            new EnemyControlGrabPairConfig { NpcBone = "j_te_r", PlayerBone = "j_kubi", PlayerAnchor = anchor,
                NpcAnchor = new EnemyControlGrabAnchor { Bone = "j_te_r", Offset = new Vector3(0.05f, 0f, 0.02f), Normal = Vector3.UnitY } },
        },
    };
    var json = JsonConvert.SerializeObject(profile, settings);
    Check(!json.Contains("System.Numerics"), "saved anchor names no runtime vector type");
    var back = JsonConvert.DeserializeObject<EnemyControlGrabProfile>(json, settings)!;
    var pa = back.Pairs[0].PlayerAnchor;
    Check(pa != null && pa.Bone == "j_kubi" && pa.Offset == anchor.Offset && pa.Normal == anchor.Normal && pa.PickScale == 1.25f,
        "profile round trip keeps the player anchor exactly");
    Check(back.Pairs[0].NpcAnchor is { Bone: "j_te_r" } na && na.Offset == new Vector3(0.05f, 0f, 0.02f), "profile round trip keeps the enemy anchor");

    // Save copies deep: editing the live pair afterwards does not reach the saved profile, and the other way round.
    var saved = EnemyControlGrabPairList.Clone(profile.Pairs[0]);
    profile.Pairs[0].PlayerAnchor!.OffsetX = 9f;
    Check(saved.PlayerAnchor!.OffsetX == 0.01f && !ReferenceEquals(saved.PlayerAnchor, profile.Pairs[0].PlayerAnchor), "clone is deep");
    var loaded = EnemyControlGrabPairList.CopyForUse(back.Pairs);
    loaded[0].NpcAnchor!.Bone = "changed";
    Check(back.Pairs[0].NpcAnchor!.Bone == "j_te_r", "load copies deep");
}
{
    EnemyControlGrabAnchor At(string bone, float x) => new() { Bone = bone, Offset = new Vector3(x, 0f, 0f), Normal = Vector3.UnitZ };
    var pairs = new List<EnemyControlGrabPairConfig>
    {
        new() { NpcBone = "j_te_r", PlayerBone = "j_kubi" },
        new() { NpcBone = "j_te_r", PlayerBone = "j_kubi", PlayerAnchor = At("j_kubi", 0.03f) },
        new() { NpcBone = "j_te_r", PlayerBone = "j_kubi", PlayerAnchor = At("j_kubi", -0.03f) },
        new() { NpcBone = "j_te_r ", PlayerBone = "j_kubi", PlayerAnchor = At("j_kubi", -0.03f) },  // same as the one above
        new() { NpcBone = "j_te_r", PlayerBone = "j_kubi", NpcAnchor = At("j_te_r", 0.03f), PlayerAnchor = At("j_kubi", -0.03f) },
        new() { NpcBone = "j_te_r", PlayerBone = "j_kubi" },                                          // plain duplicate
    };
    var changed = EnemyControlGrabPairList.Deduplicate(pairs, ensureOne: true);
    Check(changed && pairs.Count == 4, $"dedupe keeps the 4 different holds (kept {pairs.Count})");
    Check(pairs.Count(p => p.PlayerAnchor == null && p.NpcAnchor == null) == 1, "plain duplicate dropped once");
    Check(pairs.All(p => p.NpcBone == "j_te_r"), "dedupe trims bone names");
    var empty = new List<EnemyControlGrabPairConfig>();
    Check(EnemyControlGrabPairList.Deduplicate(empty, ensureOne: true) && empty.Count == 1 && empty[0].NpcAnchor == null, "empty list gets the default pair");
}

Console.WriteLine(failures == 0 ? $"Grab anchor: {checks} checks passed." : $"Grab anchor: {failures} of {checks} checks FAILED.");
return failures == 0 ? 0 : 1;
