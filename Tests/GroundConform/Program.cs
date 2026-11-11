using System.Numerics;
using CombatSimulator.Animation;
using Lumina;

// Pushes real skeletons into the ground through the production GroundConformSolver, from standing to
// far below the floor, and checks what comes out at every depth. The solver looks no bone up by name;
// the names are used here only to check what a human does.

string sqpack = args.Length > 0 ? args[0] : "C:/Program Files (x86)/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/sqpack";
using var game = new GameData(sqpack);
var failures = 0;
var handledCount = 0;
var flatCount = 0;
void Check(bool condition, string what)
{
    if (condition) return;
    failures++;
    Console.WriteLine($"   FAIL: {what}");
}

var skeletons = new List<(string Label, Skeleton Skeleton, bool Human)>();
foreach (var race in new[] { 101, 201, 1001 })
    if (game.GetFile($"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb")?.Data is { } bytes)
        skeletons.Add(($"human c{race:D4}", Skeleton.Read(bytes), true));
// Whatever monsters can be read: four legs, wings, tails, no legs at all.
for (var monster = 1; monster <= 900 && skeletons.Count < 43; monster++)
{
    var bytes = game.GetFile($"chara/monster/m{monster:D4}/skeleton/base/b0001/skl_m{monster:D4}b0001.sklb")?.Data;
    if (bytes == null) continue;
    try { skeletons.Add(($"monster m{monster:D4}", Skeleton.Read(bytes), false)); }
    catch { /* a layout the offline reader does not know */ }
}

foreach (var (label, skeleton, human) in skeletons)
{
    var count = skeleton.Names.Length;
    var animated = skeleton.Model.Select(m => m.Translation).ToArray();
    var rotations = skeleton.Model.Select(m => Matrix4x4.Decompose(m, out _, out var r, out _) ? r : Quaternion.Identity).ToArray();
    if (human)
    {
        // The reference pose holds the arms out sideways; nobody stands like that. Let them hang.
        foreach (var side in new[] { "l", "r" })
        {
            int shoulder = skeleton.Index($"j_ude_a_{side}"), hand = skeleton.Index($"j_te_{side}");
            if (shoulder < 0 || hand < 0) continue;
            var from = Vector3.Normalize(animated[hand] - animated[shoulder]);
            var to = Vector3.Normalize(new Vector3(0f, -1f, 0.08f));
            var down = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(from, to)), MathF.Acos(Math.Clamp(Vector3.Dot(from, to), -1f, 1f)));
            var centre = animated[shoulder];
            for (var i = shoulder; i < count; i++)
            {
                var under = false;
                for (var p = i; p >= 0 && !under; p = skeleton.Parents[p]) under = p == shoulder;
                if (!under) continue;
                animated[i] = centre + Vector3.Transform(animated[i] - centre, down);
                rotations[i] = Quaternion.Normalize(down * rotations[i]);
            }
        }
    }
    var height = animated.Max(p => p.Y) - animated.Min(p => p.Y);
    if (height < 0.05f) continue;
    var onAllFours = false;
    var solver = new GroundConformSolver();
    // A reference pose that dips below its owner's feet (a hanging tail) stands on its own lowest point.
    var dip = MathF.Min(0f, animated.Min(p => p.Y));
    var steps = 80;
    var deepest = 1.3f * height;

    Vector3[]? previous = null;
    var worstLength = 0f; var worstJump = 0f; var worstBelow = 0f; var worstStretch = 0f; var worstHangingJump = 0f;
    var notes = new List<string>();
    for (var step = 0; step <= steps; step++)
    {
        var ground = deepest * step / steps;
        var positions = (Vector3[])animated.Clone();
        var turned = (Quaternion[])rotations.Clone();
        var changed = solver.Solve(skeleton.Parents, positions, turned, ground, Vector3.UnitZ);
        if (step == 0) Check(!changed || positions.Zip(animated).All(p => Vector3.Distance(p.First, p.Second) < 0.02f * height), $"{label}: standing on the ground changed the pose");
        Check(positions.All(p => float.IsFinite(p.X + p.Y + p.Z)) && turned.All(q => float.IsFinite(q.X + q.Y + q.Z + q.W)), $"{label}: not a number at depth {ground:F2}");
        if (!changed) { previous = positions; continue; }

        var structural = solver.Structural;
        if (args.Contains("--trace") && label.EndsWith(args[^1])) { Console.WriteLine($"      trace {ground / height:P0}: stage {solver.LastStage:F2} tip {solver.LastTip * 57.3f:F0} lift {solver.LastLift:F2} crouch {solver.LastCrouch:F2} rest {solver.LastResting:F2} deepest {solver.LastDeepest:F2}"); if (step == 40) for (var l = 0; l < solver.Limbs.Count; l++) Console.WriteLine($"      limb {(solver.StandsOn(l) ? "STANDS" : "hangs ")}: {string.Join(" ", solver.Limbs[l].Select(i => $"{skeleton.Names[i]}@{animated[i].Y:F2},{animated[i].Z:F2}"))}"); }
        for (var i = 0; i < count; i++)
        {
            if (!structural[i]) continue;
if (ground - positions[i].Y > worstBelow + 0.02f && args.Contains("--diag")) Console.WriteLine($"      diag {label} ground {ground:F2}: {skeleton.Names[i]} (parent {skeleton.Names[skeleton.Parents[i]]}, core={solver.Core[i]}) animated y {animated[i].Y:F2} is {ground - positions[i].Y:F2} below");
            worstBelow = MathF.Max(worstBelow, ground + dip - positions[i].Y);
            var parent = skeleton.Parents[i];
            if (parent >= 0 && structural[parent])
            {
                var before = Vector3.Distance(animated[i], animated[parent]);
                var after = Vector3.Distance(positions[i], positions[parent]);
if (MathF.Abs(after - before) > worstLength && args.Contains("--diag")) Console.WriteLine($"      diag {label} ground {ground:F2}: {skeleton.Names[parent]} -> {skeleton.Names[i]} length {before:F3} -> {after:F3}; parent y {positions[parent].Y - ground:F2}, child y {positions[i].Y - ground:F2}");
                worstLength = MathF.Max(worstLength, MathF.Abs(after - before));
                // What the parent's rotation alone would make of this bone, against where it was put.
                var carried = positions[parent] + Vector3.Transform(animated[i] - animated[parent],
                    Quaternion.Normalize(turned[parent] * Quaternion.Inverse(rotations[parent])));
                worstStretch = MathF.Max(worstStretch, Vector3.Distance(carried, positions[i]));
            }
        }
        if (previous != null)
            for (var i = 0; i < count; i++)
{ if (Vector3.Distance(positions[i], previous[i]) > 8f * deepest / steps && args.Contains("--jump") && solver.Structural[i]) Console.WriteLine($"      jump {label} ground {ground / height:P0}: {skeleton.Names[i]} moved {Vector3.Distance(positions[i], previous[i]) * 100:F0} cm: {previous[i] - new Vector3(0, ground, 0)} -> {positions[i] - new Vector3(0, ground, 0)}");
                // What the body is and stands on must move smoothly; what merely hangs from it is reported.
                if (solver.Hanging[i] || !solver.Structural[i]) worstHangingJump = MathF.Max(worstHangingJump, Vector3.Distance(positions[i], previous[i]));
                else worstJump = MathF.Max(worstJump, Vector3.Distance(positions[i], previous[i])); }
        previous = positions;

        if (human)
        {
            Vector3 At(string name) => positions[skeleton.Index(name)];
            Vector3 Was(string name) => animated[skeleton.Index(name)];
            var leg = Was("j_asi_a_l").Y - Was("j_asi_d_l").Y;
            if (step % 6 == 0 || step == steps)
                notes.Add($"      ground {ground / height,4:P0} of height: pelvis y={At("j_kosi").Y - ground:F2} z={At("j_kosi").Z:+0.00;-0.00}  knee y={At("j_asi_c_l").Y - ground:F2} z={At("j_asi_c_l").Z:+0.00;-0.00}  foot y={At("j_asi_d_l").Y - ground:F2} z={At("j_asi_d_l").Z:+0.00;-0.00}  chest y={At("j_sebo_c").Y - ground:F2} z={At("j_sebo_c").Z:+0.00;-0.00}  hand y={At("j_te_l").Y - ground:F2} z={At("j_te_l").Z:+0.00;-0.00}  stage {solver.LastStage:F2}  head y={At("j_kao").Y - ground:F2} z={At("j_kao").Z:+0.00;-0.00}");
            // Crouching: the ground is a quarter of a leg up. The body comes down with its owner
            // (in its own space it stays put), the feet stand on the ground where they were, the knees go forward.
            if (MathF.Abs(ground - 0.25f * leg) < deepest / steps * 0.51f)
            {
                Check(MathF.Abs(At("j_kosi").Y - Was("j_kosi").Y) < 0.03f * height, $"{label}: crouching, the pelvis is {At("j_kosi").Y - Was("j_kosi").Y:+0.000;-0.000} off the height it is held at");
                Check(MathF.Abs(At("j_kao").Y - Was("j_kao").Y) < 0.03f * height, $"{label}: crouching, the head moved {At("j_kao").Y - Was("j_kao").Y:+0.000;-0.000}");
                foreach (var side in new[] { "l", "r" })
                {
                    var foot = At($"j_asi_d_{side}");
                    Check(foot.Y >= ground && foot.Y < ground + 0.08f * height, $"{label}: crouching, foot {side} is {foot.Y - ground:F3} above the ground");
                    Check(new Vector2(foot.X - Was($"j_asi_d_{side}").X, foot.Z - Was($"j_asi_d_{side}").Z).Length() < 0.06f * height, $"{label}: crouching, foot {side} slid");
                    Check(At($"j_asi_c_{side}").Z > Was($"j_asi_c_{side}").Z + 0.01f * height, $"{label}: crouching, knee {side} did not go forward");
                }
            }
            // On all fours: level, on its knees and its hands, hips up over the knees.
            if (!onAllFours && solver.LastStage >= 1f)
            {
                onAllFours = true;
                var thigh = Vector3.Distance(Was("j_asi_a_l"), Was("j_asi_c_l"));
                Check(At("j_kosi").Y - ground > 0.7f * thigh, $"{label}: on all fours, the hips are only {At("j_kosi").Y - ground:F2} up (thigh {thigh:F2})");
                Check(MathF.Abs(At("j_sebo_c").Y - At("j_kosi").Y) < 0.12f * height, $"{label}: on all fours, the back is not level (chest {At("j_sebo_c").Y - At("j_kosi").Y:+0.00;-0.00} from the hips)");
                Check(At("j_sebo_c").Z - At("j_kosi").Z > 0.1f * height, $"{label}: on all fours, the chest is not ahead of the hips");
                // Against a crawl made by hand (measured on c0101: hips 0.51 m, chest 0.53, shin 21 degrees
                // up off the ground, toes 35 down and behind the ankle, face 42 below level, knee bent 109).
                float Deg(float radians) => radians * 180f / MathF.PI;
                float Rise(Vector3 from, Vector3 to) => Deg(MathF.Asin(Math.Clamp(Vector3.Normalize(to - from).Y, -1f, 1f)));
                var faces = Vector3.Transform(Vector3.UnitZ, Quaternion.Normalize(turned[skeleton.Index("j_kao")] * Quaternion.Inverse(rotations[skeleton.Index("j_kao")])));
                var face = Deg(MathF.Asin(Math.Clamp(faces.Y, -1f, 1f)));
                var kneeBend = Deg(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(At("j_asi_c_l") - At("j_asi_a_l")), Vector3.Normalize(At("j_asi_d_l") - At("j_asi_c_l"))), -1f, 1f)));
                var elbowBend = Deg(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(At("j_ude_b_l") - At("j_ude_a_l")), Vector3.Normalize(At("j_te_l") - At("j_ude_b_l"))), -1f, 1f)));
                var lowestBone = Enumerable.Range(0, count).Where(i => skeleton.Names[i].StartsWith("j_") && !skeleton.Names[i].StartsWith("j_sk_")).OrderBy(i => positions[i].Y).First();
                var fingers = Enumerable.Range(0, count).Where(i => { for (var b = i; b >= 0; b = skeleton.Parents[b]) if (b == skeleton.Index("j_te_l")) return i != b; return false; }).ToArray();
                var fingerRise = fingers.Length > 0 ? fingers.Max(i => positions[i].Y) - fingers.Min(i => positions[i].Y) : 0f;
                Console.WriteLine($"   {label} on all fours, against the crawl made by hand (scaled by height {height / 1.73f:F2}): hips {At("j_kosi").Y - ground:F2} (0.51), chest {At("j_sebo_c").Y - ground:F2} (0.53), head {At("j_kao").Y - ground:F2} (0.52), " +
                                  $"face {face:F0} (-42), knee bend {kneeBend:F0} (109), shin up {Rise(At("j_asi_c_l"), At("j_asi_d_l")):F0} (21), foot {Rise(At("j_asi_d_l"), At("j_asi_e_l")):F0} (-35), ankle {At("j_asi_d_l").Y - ground:F2} (0.20), " +
                                  $"elbow bend {elbowBend:F0} (50), elbow {At("j_ude_b_l").Y - ground:F2} (0.27), wrist {At("j_te_l").Y - ground:F2} (0.06), lowest bone {skeleton.Names[lowestBone]} {positions[lowestBone].Y - ground:+0.00;-0.00}, hand spans {fingerRise:F2} in height");
                Check(face is > -65f and < -20f, $"{label}: on all fours, the face looks {face:F0} degrees from level (a crawl looks ahead and down, about -42)");
                Check(positions[lowestBone].Y - ground > -0.02f, $"{label}: on all fours, {skeleton.Names[lowestBone]} is {ground - positions[lowestBone].Y:F2} under the ground");
                foreach (var side in new[] { "l", "r" })
                {
                    Check(Rise(At($"j_asi_c_{side}"), At($"j_asi_d_{side}")) is > -5f and < 35f, $"{label}: on all fours, shin {side} is {Rise(At($"j_asi_c_{side}"), At($"j_asi_d_{side}")):F0} degrees up (0 to 21)");
                    Check(At($"j_asi_e_{side}").Z < At($"j_asi_d_{side}").Z && At($"j_asi_e_{side}").Y < At($"j_asi_d_{side}").Y, $"{label}: on all fours, foot {side} does not trail toes down behind the ankle");
                }
                foreach (var side in new[] { "l", "r" })
                {
                    Check(MathF.Min(At($"j_asi_b_{side}").Y, At($"j_asi_c_{side}").Y) - ground < 0.06f * height, $"{label}: on all fours, knee {side} is {MathF.Min(At($"j_asi_b_{side}").Y, At($"j_asi_c_{side}").Y) - ground:F2} off the ground");
                    Check(At($"j_te_{side}").Y - ground < 0.06f * height, $"{label}: on all fours, hand {side} is {At($"j_te_{side}").Y - ground:F2} off the ground");
                    Check(MathF.Abs(At($"j_te_{side}").Z - At($"j_ude_a_{side}").Z) < 0.2f * height, $"{label}: on all fours, hand {side} is not under its shoulder ({At($"j_te_{side}").Z - At($"j_ude_a_{side}").Z:+0.00;-0.00})");
                    Check(At($"j_asi_d_{side}").Z < At($"j_asi_c_{side}").Z, $"{label}: on all fours, shin {side} does not lie back from the knee");
                }
            }
            // As low as it goes: on its front and low, but still on its limbs, not flat.
            if (step == steps)
            {
                Check(onAllFours, $"{label}: never went on all fours");
                var highest = Enumerable.Range(0, count).Where(i => structural[i]).Max(i => positions[i].Y - ground);
                Check(highest < 0.4f * height, $"{label}: pushed under, the body still stands {highest:F2} high");
                Check(At("j_kosi").Y - ground > 0.08f * height && At("j_sebo_c").Y - ground > 0.08f * height, $"{label}: pushed under, it lies flat (hips {At("j_kosi").Y - ground:F2}, chest {At("j_sebo_c").Y - ground:F2})");
                Check(At("j_kao").Z - At("j_kosi").Z > 0.2f * height, $"{label}: pushed under, it is not on its front (head {At("j_kao").Z - At("j_kosi").Z:+0.00;-0.00} from the pelvis)");
                Check(solver.LastDeepest < ground, $"{label}: is not as low as it goes at {ground:F2} (deepest {solver.LastDeepest:F2})");
                // Deeper than as low as it goes changes nothing but the height it all sits at.
                var deeper = (Vector3[])animated.Clone();
                var deeperTurned = (Quaternion[])rotations.Clone();
                var probe = new GroundConformSolver();
                probe.Solve(skeleton.Parents, deeper, deeperTurned, solver.LastDeepest, Vector3.UnitZ);
                var off = Enumerable.Range(0, count).Where(i => structural[i]).Max(i => Vector3.Distance(deeper[i] - new Vector3(0f, solver.LastDeepest, 0f), positions[i] - new Vector3(0f, ground, 0f)));
                Check(off < 0.01f * height, $"{label}: the pose still changes ({off * 100:F1} cm) past its deepest");
            }
        }
        else if (step == steps)
        {
            var highest = Enumerable.Range(0, count).Where(i => structural[i]).Max(i => positions[i].Y - ground);
            // A reference pose is not a standing pose, and a core is carried as one piece: how flat a creature ends is reported, not required.
            if (highest < 0.35f * height) flatCount++;
        }
    }

    if (human)
    {
        // Moving: legs and arms swing as in a walk. On all fours and as low as it goes, the swing
        // must still show in the limbs, and a little more swing must give a little more of it.
        void Turn(Vector3[] pose, Quaternion[] turns, string bone, float angle)
        {
            var at = skeleton.Index(bone);
            if (at < 0) return;
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle);
            var centre = pose[at];
            for (var i = at; i < count; i++)
            {
                var under = false;
                for (var p = i; p >= 0 && !under; p = skeleton.Parents[p]) under = p == at;
                if (!under) continue;
                pose[i] = centre + Vector3.Transform(pose[i] - centre, turn);
                turns[i] = Quaternion.Normalize(turn * turns[i]);
            }
        }
        foreach (var (name, ground) in new[] { ("crouching", 0.3f * height), ("between", 0.5f * height), ("on all fours", 0.62f * height), ("as low as it goes", 0.85f * height) })
        {
            Vector3[]? last = null;
            var worst = 0f;
            float nearHand = float.MaxValue, farHand = float.MinValue, nearKnee = float.MaxValue, farKnee = float.MinValue;
            for (var degrees = -24; degrees <= 24; degrees++)
            {
                var swing = degrees * MathF.PI / 180f;
                var pose = (Vector3[])animated.Clone();
                var turns = (Quaternion[])rotations.Clone();
                Turn(pose, turns, "j_asi_a_l", swing); Turn(pose, turns, "j_asi_a_r", -swing);
                Turn(pose, turns, "j_ude_a_l", -swing); Turn(pose, turns, "j_ude_a_r", swing);
                solver.Solve(skeleton.Parents, pose, turns, ground, Vector3.UnitZ);
                if (last != null)
                    for (var i = 0; i < count; i++)
                        if (solver.Structural[i]) { if (args.Contains("--swing") && Vector3.Distance(pose[i], last[i]) > 0.03f * height) Console.WriteLine($"      swing {name} {degrees}: {skeleton.Names[i]} {last[i] - new Vector3(0, ground, 0)} -> {pose[i] - new Vector3(0, ground, 0)} stage {solver.LastStage:F2} tip {solver.LastTip:F2}"); worst = MathF.Max(worst, Vector3.Distance(pose[i], last[i])); }
                last = pose;
                var hand = pose[skeleton.Index("j_te_l")].Z; var knee = pose[skeleton.Index("j_asi_c_l")].Z;
                nearHand = MathF.Min(nearHand, hand); farHand = MathF.Max(farHand, hand);
                nearKnee = MathF.Min(nearKnee, knee); farKnee = MathF.Max(farKnee, knee);
            }
            Console.WriteLine($"   {label} {name}, limbs swinging 24 degrees either way: hand travels {(farHand - nearHand) * 100:F0} cm, knee {(farKnee - nearKnee) * 100:F0} cm; largest move for one degree {worst * 100:F1} cm");
            Check(farHand - nearHand > 0.05f * height, $"{label} {name}: the arms' swing does not show ({(farHand - nearHand) * 100:F0} cm)");
            Check(farKnee - nearKnee > 0.03f * height, $"{label} {name}: the legs' swing does not show ({(farKnee - nearKnee) * 100:F0} cm)");
            Check(worst < 0.04f * height, $"{label} {name}: one degree of swing moves a bone {worst * 100:F1} cm");
        }
    }
    if (args.Contains("--core")) Console.WriteLine($"   core of {label}: {string.Join(" ", Enumerable.Range(0, count).Where(i => solver.Core[i]).Select(i => skeleton.Names[i] + "@" + animated[i].Y.ToString("F2")))}");
    var handled = GroundConformSolver.Handles(skeleton.Parents, animated, rotations, Vector3.UnitZ);
    if (handled) handledCount++;
    if (human) Check(handled, $"{label}: a human skeleton did not pass the fit's own check");
    Console.Write(handled ? "[handled] " : "[left to sink] ");
    if (!handled && args.Contains("--why"))
    {
        var probe = new GroundConformSolver();
        Vector3[]? last = null;
        var stepSize = 1.3f * height / 60;
        var said = 0;
        for (var n = 1; n <= 60 && said < 3; n++)
        {
            var pos = (Vector3[])animated.Clone(); var rot = (Quaternion[])rotations.Clone();
            if (!probe.Solve(skeleton.Parents, pos, rot, stepSize * n, Vector3.UnitZ)) { last = null; continue; }
            if (args.Contains("--core-trace")) Console.WriteLine($"      trace {label} {stepSize * n / height:P0}: ground {stepSize * n:F2} tip {probe.LastTip * 57.3f:F0} lift {probe.LastLift:F2} crouch {probe.LastCrouch:F2} resting {probe.LastResting:F2}");
            if (n == 1 || last == null && said == 0)
            {
                Console.WriteLine();
                Console.WriteLine($"   why {label}: core = {string.Join(" ", Enumerable.Range(0, count).Where(i => probe.Core[i]).Select(i => skeleton.Names[i]))}");
                for (var l = 0; l < probe.Limbs.Count; l++)
                    Console.WriteLine($"      limb {(probe.StandsOn(l) ? "STANDS" : "hangs ")}: {string.Join(" ", probe.Limbs[l].Select(i => $"{skeleton.Names[i]}@{animated[i].Y:F2}"))}");
            }
            for (var i = 0; i < count && said < 3; i++)
            {
                if (!probe.Structural[i]) continue;
                var kind = probe.Core[i] ? "core" : probe.Hanging[i] ? "hanging" : "standing";
                if (pos[i].Y < stepSize * n + dip - 0.03f * height) { Console.WriteLine($"      at {stepSize * n / height:P0}: {skeleton.Names[i]} ({kind}) is {(stepSize * n + dip - pos[i].Y) * 100:F0} cm under"); said++; }
                else if (last != null && !probe.Hanging[i] && Vector3.Distance(pos[i], last[i]) > 12f * stepSize) { Console.WriteLine($"      at {stepSize * n / height:P0}: {skeleton.Names[i]} ({kind}) jumped {Vector3.Distance(pos[i], last[i]) * 100:F0} cm: {last[i] - new Vector3(0, stepSize * (n - 1), 0)} -> {pos[i] - new Vector3(0, stepSize * n, 0)}"); said++; }
            }
            last = pos;
        }
    }
    Console.WriteLine($"{label}: {count} bones, {solver.Structural.Count(s => s)} take part, height {height:F2}; worst over the push: " +
                      $"below ground {MathF.Max(0f, worstBelow) * 100:F1} cm, bone length off {worstLength * 100:F1} cm, " +
                      $"bone off its parent's turn {worstStretch * 100:F1} cm, largest move per step {worstJump * 100:F1} cm, of hanging limbs {worstHangingJump * 100:F1} cm (step {deepest / steps * 100:F1} cm)");
    foreach (var note in notes) Console.WriteLine(note);
    Check(worstLength < 0.002f * height + 1e-4f, $"{label}: a bone changed length by {worstLength * 100:F1} cm");
    // Below the ground, or jumping: what the fit's own check is there to catch. A skeleton it lets
    // through must be clean; one it turns away is left to sink, and only counted.
    if (handled)
    {
        Check(worstBelow < 0.03f * height, $"{label}: handled, but a bone is {worstBelow * 100:F1} cm below the ground");
        Check(worstJump < 14f * deepest / steps, $"{label}: handled, but the pose jumps {worstJump * 100:F1} cm for a {deepest / steps * 100:F1} cm push");
    }
}

Check(skeletons.Count(s => s.Human) >= 2, "no human skeletons were read");
Check(skeletons.Count(s => !s.Human) >= 10, $"only {skeletons.Count(s => !s.Human)} monster skeletons were read");
var monsters = skeletons.Count(s => !s.Human);
Console.WriteLine($"Fitted to the ground: {handledCount} of {skeletons.Count} skeletons ({skeletons.Count - handledCount} left to sink); {flatCount} of {monsters} monsters end under 35% of their height.");
Check(handledCount >= 0.85f * skeletons.Count, $"only {handledCount} of {skeletons.Count} skeletons pass the fit's own check");
Console.WriteLine(failures == 0 ? "GroundConform: all checks passed." : $"GroundConform: {failures} check(s) FAILED.");
return failures;
