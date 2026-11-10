using System.Numerics;
using CombatSimulator.Animation.Attachment;

static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

// --- skirt cloth fixtures: measured adult skeleton (waistband ring at the hips, chains reaching the knee) ---
// Ring order front-left, side-left, back-left, back-right, side-right, front-right; +Z is the front of the body.
static SkirtCloth.ChainRest[] SkirtRest(Vector3 waistCenter, Quaternion body)
{
    var angles = new[] { 30f, 90f, 150f, 210f, 270f, 330f };
    var first = new[] { .415f, .218f, .186f, .186f, .218f, .415f };
    var second = new[] { .120f, .273f, .306f, .306f, .273f, .120f };
    var chains = new SkirtCloth.ChainRest[6];
    for (var i = 0; i < 6; i++)
    {
        var a = angles[i] * MathF.PI / 180f;
        var outward = new Vector3(MathF.Sin(a), 0, MathF.Cos(a));
        var waist = outward * .15f;
        var mid = waist + Vector3.Normalize(outward * .25f - Vector3.UnitY) * first[i];
        var lower = mid + Vector3.Normalize(outward * .15f - Vector3.UnitY) * second[i];
        chains[i] = new SkirtCloth.ChainRest(waistCenter + Vector3.Transform(waist, body),
            waistCenter + Vector3.Transform(mid, body), waistCenter + Vector3.Transform(lower, body));
    }
    return chains;
}
static Vector3[] SkirtWaist(SkirtCloth.ChainRest[] rest) => rest.Select(r => r.Waist).ToArray();
static float SkirtLengthError(SkirtCloth cloth, SkirtCloth.ChainRest[] rest)
{
    var worst = 0f;
    for (var c = 0; c < rest.Length; c++)
    {
        var lengths = new[] { Vector3.Distance(rest[c].Waist, rest[c].Mid), Vector3.Distance(rest[c].Mid, rest[c].Lower), Vector3.Distance(rest[c].Mid, rest[c].Lower) };
        for (var t = 0; t < 3; t++)
            worst = MathF.Max(worst, MathF.Abs(Vector3.Distance(cloth.Position(c, t), cloth.Position(c, t + 1)) - lengths[t]) / lengths[t]);
    }
    return worst;
}
static float SkirtCapsuleDepth(SkirtCloth cloth, SkirtCloth.Capsule capsule)
{
    var worst = 0f;
    for (var c = 0; c < cloth.ChainCount; c++)
        for (var t = 1; t < SkirtCloth.Tiers; t++)
            worst = MathF.Max(worst, capsule.Radius - GarmentFlatten.DistanceToSegment(cloth.Position(c, t), new GarmentFlatten.Segment(capsule.A, capsule.B)));
    return worst;
}
var cases = new (string, Action)[]
{
    ("wide stance retains waistband without cancelling the lower garment slide", () => {
        var waist=new WaistRetentionMotion(); var slide=new Vector3(0,-.26f,0);
        for(var i=0;i<600;i++)waist.Advance(1f/60,new Vector3(-.866f,-.5f,0),new Vector3(.866f,-.5f,0));
        Check((slide+waist.Correction(slide,1)).Length()<.0001f,"waist floated away from pelvis");
        var rootWeight=WaistRetentionMotion.BoneWeight("j_sk_a_l",1);
        var tipWeight=WaistRetentionMotion.BoneWeight("j_sk_b_l",rootWeight);
        Check((slide+waist.Correction(slide,tipWeight)).Length()>(slide+waist.Correction(slide,rootWeight)).Length(),"skirt pulled as one rigid shell");
        for(var i=0;i<600;i++)waist.Advance(1f/60,-Vector3.UnitY,-Vector3.UnitY);
        Check(waist.Correction(slide,1).Length()<.0001f,"closed stance lost its full slide");
    }),
    ("waist retention has no threshold snap during pose changes", () => {
        foreach(var fps in new[]{30,60,144,300}) {
            var waist=new WaistRetentionMotion(); var slide=new Vector3(0,-.3f,0);
            for(var i=0;i<fps*4;i++) {
                var before=waist.Correction(slide,1);
                var angle=i<fps*2?i/(float)fps*MathF.PI*.5f:(i%2==0?0:MathF.PI*.5f);
                waist.Advance(1f/fps,new Vector3(-MathF.Sin(angle),-MathF.Cos(angle),0),new Vector3(MathF.Sin(angle),-MathF.Cos(angle),0));
                Check(Vector3.Distance(before,waist.Correction(slide,1))<=.6f/fps+1e-6f,"waist correction snapped");
            }
        }
    }),
    ("whole garment collapse and direction reversals remain continuous", () => {
        foreach(var fps in new[] {30,60,144,300}) {
            var motion=new WholeGarmentSlide();
            for(var i=0;i<fps*5;i++) {
                var before=motion.Offset;
                var angle=i/(float)fps*MathF.PI;
                var direction=new Vector3(MathF.Sin(angle),-MathF.Cos(angle),0);
                if(i>fps*3) direction=(i%2==0?1:-1)*Vector3.UnitX;
                motion.Advance(1f/fps,direction,MathF.Max(0,-direction.Y),.2f,.4f,.2f,2);
                Check(Vector3.Distance(before,motion.Offset)<=.4f/fps+1e-6f,"collapse teleported garment");
                Check(motion.Offset.Length()<=.20001f,"travel escaped retention limit");
            }
        }
    }),
    ("whole garment live distance reduction eases back without snapping", () => {
        var motion=new WholeGarmentSlide();
        for(var i=0;i<600;i++)motion.Advance(1f/60,-Vector3.UnitY,1,.2f,.4f,0,1);
        Check(motion.Offset.Length()>.19f,"slider produced no slide");
        var before=motion.Offset;
        motion.Advance(1f/60,-Vector3.UnitY,1,0,.4f,0,1);
        Check(Vector3.Distance(before,motion.Offset)<=.4f/60+1e-6f,"live setting teleported garment");
        for(var i=0;i<600;i++)motion.Advance(1f/60,-Vector3.UnitY,1,0,.4f,0,1);
        Check(motion.Offset.Length()<.0001f,"zero distance did not return to worn pose");
    }),
    ("a garment on a body lying flat still slides its full distance, slower than when upright", () => {
        float TimeToReach(Vector3 direction) {
            var motion = new WholeGarmentSlide();
            for (var i = 1; i <= 60 * 30; i++) {
                motion.Advance(1f / 60, direction, WholeGarmentSlide.Drive(direction), .13f, .6f, .45f, 1.5f);
                if (motion.Offset.Length() >= .129f) return i / 60f;
            }
            return float.PositiveInfinity;
        }
        var upright = TimeToReach(-Vector3.UnitY); var lying = TimeToReach(Vector3.UnitX); var inverted = TimeToReach(Vector3.UnitY);
        Check(float.IsFinite(lying) && float.IsFinite(inverted), "no slide without gravity along the body");
        Check(lying > upright && lying < 6f, $"lying slide time {lying}s vs upright {upright}s");
        Check(WholeGarmentSlide.Drive(-Vector3.UnitY) == 1 && WholeGarmentSlide.Drive(Vector3.UnitX) == WholeGarmentSlide.RestingDrive, "drive range");
        Check(WholeGarmentSlide.Drive(new Vector3(0, float.NaN, 0)) == WholeGarmentSlide.RestingDrive, "NaN drive");
    }),
    ("half off: the top stays on at the waist, a lower garment at both shins", () => {
        var top = new[] { "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_sako_l", "j_ude_a_l", "j_ude_b_l", "j_sako_r", "j_ude_a_r", "j_ude_b_r" };
        var lower = new[] { "j_kosi", "j_asi_a_l", "j_asi_b_l", "j_asi_a_r", "j_asi_b_r" };
        string Held(int slot, string[] bones, int holds) => string.Join(",", bones.Where(b => GarmentTether.IsPinned(slot, b, holds)));
        Check(Held(1, top, GarmentTether.DefaultTopHold) == "j_kosi", "default top hold");
        Check(Held(3, lower, GarmentTether.DefaultLowerHold) == "j_asi_b_l,j_asi_b_r", "default lower hold");
        Check(Held(1, top, 0) == "" && Held(3, lower, 0) == "", "no holds still pinned something");
        // Every segment of both rigs has exactly one hold bit, and every bit holds at least one segment.
        foreach (var (slot, bones, bits) in new[] { (1, top, 8), (3, lower, 5) })
        {
            Check(bones.All(b => GarmentTether.HoldBit(slot, b) is var bit && bit != 0 && (bit & (bit - 1)) == 0), $"slot {slot} segment without a single hold");
            for (var i = 0; i < bits; i++) Check(Held(slot, bones, 1 << i) != "", $"slot {slot} hold {1 << i} holds nothing");
            Check(Held(slot, bones, (1 << bits) - 1) == string.Join(",", bones), $"slot {slot} all holds leave a segment loose");
        }
        // Several holds combine; the shoulder piece goes with its upper arm.
        Check(Held(1, top, GarmentTether.TopWaist | GarmentTether.TopLeftUpperArm) == "j_kosi,j_sako_l,j_ude_a_l", "combined top holds");
        Check(Held(3, lower, GarmentTether.LowerWaist | GarmentTether.LowerRightShin) == "j_kosi,j_asi_b_r", "combined lower holds");
        Check(!GarmentTether.IsPinned(2, "j_te_l", 255) && !GarmentTether.IsPinned(4, "j_asi_b_l", 255), "gloves or boots pinned");
        // The earlier single-choice dropdown values map to the same segments as before.
        Check(Held(1, top, GarmentTether.HoldsFromLegacyChoice(1, 0)) == "j_kosi" && Held(1, top, GarmentTether.HoldsFromLegacyChoice(1, 2)) == "j_sebo_c", "legacy top choice");
        Check(Held(1, top, GarmentTether.HoldsFromLegacyChoice(1, 3)) == "j_sako_l,j_ude_a_l,j_ude_b_l", "legacy left sleeve now includes the shoulder piece");
        Check(Held(3, lower, GarmentTether.HoldsFromLegacyChoice(3, 4)) == "j_asi_a_r,j_asi_b_r" && Held(3, lower, GarmentTether.HoldsFromLegacyChoice(3, 99)) == "j_asi_b_l,j_asi_b_r", "legacy lower choice");
        Check(GarmentTether.PullScale(GarmentTether.DefaultPull) == 1 && GarmentTether.PullScale(0) == 0 && GarmentTether.PullScale(99) == 1.5f && GarmentTether.PullScale(-3) == 0, "pull scale");
        Check(GarmentTether.PullVelocity(-Vector3.UnitY, Vector3.UnitZ, .3f, 0) == Vector3.Zero, "zero pull moved the garment");
    }),
    ("a pinned segment puts its bone exactly on the live body bone", () => {
        var bodyToBone = Quaternion.CreateFromYawPitchRoll(.4f, -.7f, 1.2f); var offset = new Vector3(0, -.11f, 0);
        var bonePos = new Vector3(3, 1.2f, -2); var boneRot = Quaternion.CreateFromYawPitchRoll(-1.1f, .3f, .5f);
        var (pos, rot) = GarmentTether.PinnedBodyPose(bonePos, boneRot, bodyToBone, offset);
        // The rig maps a box back to its bone as: boneRot = bodyRot * bodyToBone, bonePos = bodyPos + bodyRot * offset.
        var backRot = Quaternion.Normalize(rot * bodyToBone); var backPos = pos + Vector3.Transform(offset, rot);
        Check(Vector3.Distance(backPos, bonePos) < 1e-5f, "pinned bone position drifted");
        Check(MathF.Abs(Quaternion.Dot(backRot, boneRot)) > .99999f, "pinned bone rotation drifted");
    }),
    ("the loose part is lifted off whichever side of the body faces up", () => {
        var lateral = Vector3.UnitX;
        var supine = GarmentTether.LiftDirection(Vector3.UnitZ, lateral, Vector3.UnitZ);   // lying, feet toward +Z
        var prone = GarmentTether.LiftDirection(-Vector3.UnitZ, lateral, Vector3.UnitZ);
        Check(supine.Y > .99f && prone.Y > .99f, "lift points into the ground for a lying body");
        var upright = GarmentTether.LiftDirection(-Vector3.UnitY, lateral, Vector3.UnitZ); // standing, facing +Z
        Check(MathF.Abs(upright.Y) < 1e-5f && upright.Z > .99f, "upright body not lifted toward its front");
        var near = GarmentTether.PullVelocity(-Vector3.UnitY, upright, .05f); var far = GarmentTether.PullVelocity(-Vector3.UnitY, upright, 2f);
        Check(far.Length() > near.Length() && far.Length() <= 1.5f * MathF.Sqrt(GarmentTether.PullSpeed * GarmentTether.PullSpeed + GarmentTether.LiftSpeed * GarmentTether.LiftSpeed) + 1e-4f, "pull not bounded");
        Check(near.Y < 0 && near.Z > 0, "pull not toward the feet and off the body");
        Check(float.IsFinite(GarmentTether.PullVelocity(-Vector3.UnitY, upright, float.NaN).Length()), "NaN distance leaked");
    }),
    ("a pinned segment follows the body but is never launched by a teleport", () => {
        var v = GarmentTether.FollowVelocity(Vector3.Zero, new Vector3(.02f, 0, 0), 1f / 60);
        Check(MathF.Abs(v.X - 1.2f) < 1e-4f, "follow velocity wrong");
        Check(GarmentTether.FollowVelocity(Vector3.Zero, new Vector3(0, -9999, 0), 1f / 60) == Vector3.Zero, "teleport became velocity");
        Check(GarmentTether.FollowVelocity(Vector3.Zero, Vector3.One, 0) == Vector3.Zero, "zero dt");
        var from = Quaternion.Identity; var to = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .05f);
        var w = GarmentTether.FollowAngularVelocity(from, to, 1f / 60);
        Check(MathF.Abs(w.Y - 3f) < 1e-3f && MathF.Abs(w.X) < 1e-5f, $"angular follow wrong {w}");
        Check(GarmentTether.FollowAngularVelocity(from, new Quaternion(-to.X, -to.Y, -to.Z, -to.W), 1f / 60).Y > 2.9f, "quaternion sign changed the result");
        Check(GarmentTether.FollowAngularVelocity(from, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 3f), 1f / 60) == Vector3.Zero, "snap became spin");
    }),
    ("skirt: a garment lying on the ground keeps every chain its length (the frozen bell crushed them)", () => {
        // Waist 5 cm above the ground, body axis horizontal.
        var body = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2); var center = new Vector3(0, .05f, 0);
        var rest = SkirtRest(center, body); var waist = SkirtWaist(rest);
        var cloth = new SkirtCloth(rest, true, Vector3.Zero);
        for (var i = 0; i < 300; i++) cloth.Advance(1f / 60, waist, ReadOnlySpan<SkirtCloth.Capsule>.Empty, 0, -Vector3.UnitY * 9.81f);
        var error = SkirtLengthError(cloth, rest);
        Check(error < .03f, $"cloth chain length error {error:P1}");
        for (var c = 0; c < 6; c++) for (var t = 1; t < 4; t++) Check(cloth.Position(c, t).Y >= .0099f, "cloth below the ground");
        // Old behaviour for the same pose: every bone at its captured offset below its parent, clamped to the ground.
        var upright = SkirtRest(Vector3.Zero, Quaternion.Identity); var bellWorst = 0f;
        for (var c = 0; c < 6; c++)
        {
            var mid = rest[c].Waist + (upright[c].Mid - upright[c].Waist); mid.Y = MathF.Max(mid.Y, .012f);
            bellWorst = MathF.Max(bellWorst, MathF.Abs(Vector3.Distance(rest[c].Waist, mid) - Vector3.Distance(upright[c].Waist, upright[c].Mid)) / Vector3.Distance(upright[c].Waist, upright[c].Mid));
        }
        Check(bellWorst > .5f, $"baseline expected to crush chains, got {bellWorst:P0}");
        Console.WriteLine($"      chain length error lying down: frozen bell {bellWorst:P0}, cloth {error:P1}");
    }),
    ("skirt: it hangs from the waist, lags a sudden move and comes to rest", () => {
        var rest = SkirtRest(new Vector3(0, 1, 0), Quaternion.Identity); var waist = SkirtWaist(rest);
        var cloth = new SkirtCloth(rest, true, Vector3.Zero);
        for (var i = 0; i < 240; i++) cloth.Advance(1f / 60, waist, ReadOnlySpan<SkirtCloth.Capsule>.Empty, 0, -Vector3.UnitY * 9.81f);
        var hemBefore = cloth.Position(1, 3);
        var moved = waist.Select(w => w + Vector3.UnitX * .3f).ToArray();
        cloth.Advance(1f / 60, moved, ReadOnlySpan<SkirtCloth.Capsule>.Empty, 0, -Vector3.UnitY * 9.81f);
        Check(cloth.Position(1, 3).X - hemBefore.X < .15f, "hem moved with the waist instantly: no lag");
        Check(SkirtLengthError(cloth, rest) < .05f, "sudden move stretched the cloth");
        var last = cloth.Position(1, 3); var settledAt = -1;
        for (var i = 0; i < 300; i++)
        {
            cloth.Advance(1f / 60, moved, ReadOnlySpan<SkirtCloth.Capsule>.Empty, 0, -Vector3.UnitY * 9.81f);
            var now = cloth.Position(1, 3);
            if (Vector3.Distance(now, last) >= .0005f) settledAt = -1; else if (settledAt < 0) settledAt = i;
            last = now;
        }
        Check(settledAt >= 0 && settledAt < 240, $"hem still moving after 4 s (settled at frame {settledAt})");
        Check(MathF.Abs(last.X - (hemBefore.X + .3f)) < .05f, "hem did not follow the waist to its new place");
        Check(last.Y < moved[1].Y - .3f, "skirt is not hanging below the waist");
    }),
    ("skirt: it stays out of a leg, including a leg that passes between two points of a chain", () => {
        var rest = SkirtRest(new Vector3(0, 1, 0), Quaternion.Identity); var waist = SkirtWaist(rest);
        // A thigh raised forward through the front panels, whose first segment is 0.4 m long.
        var thigh = new SkirtCloth.Capsule(new Vector3(.09f, .95f, .02f), new Vector3(.09f, .85f, .42f), .08f);
        var cloth = new SkirtCloth(rest, true, Vector3.Zero);
        for (var i = 0; i < 360; i++) cloth.Advance(1f / 60, waist, new[] { thigh }, 0, -Vector3.UnitY * 9.81f);
        var depth = SkirtCapsuleDepth(cloth, thigh);
        Check(depth < .005f, $"cloth point {depth * 1000:F1} mm inside the leg");
        var edgeDepth = 0f;
        for (var c = 0; c < 6; c++) for (var t = 0; t < 3; t++) for (var k = 1; k < 8; k++)
            edgeDepth = MathF.Max(edgeDepth, thigh.Radius - GarmentFlatten.DistanceToSegment(Vector3.Lerp(cloth.Position(c, t), cloth.Position(c, t + 1), k / 8f), new GarmentFlatten.Segment(thigh.A, thigh.B)));
        Check(edgeDepth < .012f, $"cloth edge {edgeDepth * 1000:F1} mm inside the leg");
        Check(SkirtLengthError(cloth, rest) < .04f, "leg contact stretched the cloth");
        Console.WriteLine($"      raised thigh: deepest point {MathF.Max(0, depth) * 1000:F1} mm, deepest edge sample {MathF.Max(0, edgeDepth) * 1000:F1} mm");
    }),
    ("skirt: stable at 30, 60, 144 and 300 fps, with a tumbling waist, and after a teleport", () => {
        foreach (var fps in new[] { 30, 60, 144, 300 })
        {
            var center = new Vector3(0, 1, 0); var rest = SkirtRest(center, Quaternion.Identity);
            var cloth = new SkirtCloth(SkirtRest(center, Quaternion.CreateFromYawPitchRoll(0, 0, .8f)), true, new Vector3(1, 0, 0));
            for (var i = 0; i < fps * 6; i++)
            {
                var t = i / (float)fps;
                var body = t < 3 ? Quaternion.CreateFromYawPitchRoll(t * 2, MathF.Sin(t * 3) * 1.2f, MathF.Cos(t * 2) * .8f) : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f);
                var c2 = center + new Vector3(t < 3 ? MathF.Sin(t * 2) * .4f : 0, t < 3 ? 0 : -.7f, 0) + (i >= fps * 2 ? new Vector3(40, 0, 0) : Vector3.Zero);
                var waist = SkirtWaist(SkirtRest(c2, body));
                var before = cloth.Position(2, 3);
                cloth.Advance(1f / fps, waist, ReadOnlySpan<SkirtCloth.Capsule>.Empty, 0, -Vector3.UnitY * 9.81f);
                for (var c = 0; c < 6; c++) for (var k = 0; k < 4; k++) Check(float.IsFinite(cloth.Position(c, k).LengthSquared()), "NaN");
                // The waist is teleported 40 m at t=2 and snapped to a new pose 0.7 m lower at t=3. The cloth may
                // be out of shape for a quarter of a second after each; it must be whole again after that.
                var recovering = (t >= 2 && t < 2.25f) || (t >= 3 && t < 3.25f);
                if (!recovering) Check(Vector3.Distance(before, cloth.Position(2, 3)) < 12f / fps + .35f, $"hem jumped at {fps} fps, t={t:F2}");
                if (!recovering) Check(SkirtLengthError(cloth, rest) < .08f, $"cloth stretched {SkirtLengthError(cloth, rest):P1} at {fps} fps, t={t:F2}");
                if (t > 5.5f) Check(Vector3.Distance(before, cloth.Position(2, 3)) < .0005f * 60 / fps + 1e-5f, $"resting cloth still moving at {fps} fps");
            }
        }
    }),
    ("skirt: bone rotation follows the cloth without relying on bone axes", () => {
        var rest = SkirtRest(new Vector3(0, 1, 0), Quaternion.Identity);
        var cloth = new SkirtCloth(rest, true, Vector3.Zero);
        for (var c = 0; c < 6; c++) for (var t = 0; t < 3; t++)
            Check(MathF.Abs(cloth.RotationDelta(c, t).W) > .99999f, "untouched cloth already rotated a bone");
        // A cloth created in a rigidly turned pose is at rest there: no delta, wherever its bones point.
        var turn = Quaternion.CreateFromYawPitchRoll(.7f, -.4f, 1.1f);
        var rigid = new SkirtCloth(rest.Select(r => new SkirtCloth.ChainRest(Vector3.Transform(r.Waist, turn), Vector3.Transform(r.Mid, turn), Vector3.Transform(r.Lower, turn))).ToArray(), true, Vector3.Zero);
        for (var c = 0; c < 6; c++) for (var t = 0; t < 3; t++)
            Check(MathF.Abs(rigid.RotationDelta(c, t).W) > .99999f, "rigidly turned rest pose reported a delta");
        // Swinging the waist ring about the vertical carries a hanging skirt round with it.
        var hanging = new SkirtCloth(rest, true, Vector3.Zero); var waist = SkirtWaist(rest);
        for (var i = 0; i < 240; i++) hanging.Advance(1f / 60, waist, ReadOnlySpan<SkirtCloth.Capsule>.Empty, float.NegativeInfinity, -Vector3.UnitY * 9.81f);
        var settled = hanging.RotationDelta(1, 1);
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .6f);
        var yawed = SkirtWaist(SkirtRest(new Vector3(0, 1, 0), yaw));
        for (var i = 0; i < 360; i++) hanging.Advance(1f / 60, yawed, ReadOnlySpan<SkirtCloth.Capsule>.Empty, float.NegativeInfinity, -Vector3.UnitY * 9.81f);
        var relative = Quaternion.Normalize(hanging.RotationDelta(1, 1) * Quaternion.Inverse(settled));
        var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(relative.W), 0, 1));
        Check(MathF.Abs(angle - .6f) < .12f, $"bone turned {angle:F2} rad for a 0.60 rad turn of the waist");
        // One chain alone has no ring direction: the rotation falls back to the swing of its edge and stays valid.
        var single = new SkirtCloth(new[] { rest[0] }, false, Vector3.Zero);
        var w = new[] { rest[0].Waist };
        for (var i = 0; i < 120; i++) single.Advance(1f / 60, w, ReadOnlySpan<SkirtCloth.Capsule>.Empty, float.NegativeInfinity, -Vector3.UnitY * 9.81f);
        var q = single.RotationDelta(0, 1);
        Check(float.IsFinite(q.LengthSquared()) && MathF.Abs(q.Length() - 1) < 1e-4f, "single-chain rotation invalid");
        Check(single.Position(0, 3).Y < rest[0].Waist.Y - .5f, "single chain did not hang down");
    }),
    ("a segment still around the body keeps its worn volume", () => {
        var torso = new[] { new GarmentFlatten.Segment(Vector3.Zero, new Vector3(0, .5f, 0)) };
        var progress = 0f;
        // Slid 12 cm down the spine and 5 cm off-centre: the spine still passes through a 7 cm wide box.
        for (var i = 0; i < 600; i++)
            progress = GarmentFlatten.Advance(progress, GarmentFlatten.IsOffBody(new Vector3(.05f, .13f, 0), torso, .07f), 1f / 60);
        Check(progress == 0 && GarmentFlatten.ThicknessScale(progress) == 1, "worn segment collapsed");
    }),
    ("a segment off the body collapses to a flat panel at any frame rate", () => {
        var torso = new[] { new GarmentFlatten.Segment(Vector3.Zero, new Vector3(0, .5f, 0)) };
        foreach (var fps in new[] { 30, 60, 144, 300 })
        {
            var progress = 0f; var previous = 1f;
            for (var i = 0; i < fps; i++)
            {
                progress = GarmentFlatten.Advance(progress, GarmentFlatten.IsOffBody(new Vector3(.2f, .2f, 0), torso, .07f), 1f / fps);
                var scale = GarmentFlatten.ThicknessScale(progress);
                Check(scale <= previous + 1e-6f, "collapse reversed");
                Check(previous - scale <= 1.5f * (1 - GarmentFlatten.CollapsedThickness) / (GarmentFlatten.CollapseSeconds * fps) + 1e-4f, "collapse snapped");
                previous = scale;
                if (i + 1 == (int)MathF.Ceiling(GarmentFlatten.CollapseSeconds * fps) + 1)
                    Check(MathF.Abs(scale - GarmentFlatten.CollapsedThickness) < 1e-4f, $"not flat after {GarmentFlatten.CollapseSeconds}s at {fps} fps");
            }
            Check(MathF.Abs(previous - GarmentFlatten.CollapsedThickness) < 1e-5f, "final thickness wrong");
        }
    }),
    ("a body that returns into the segment refills it; no body means off the body", () => {
        var torso = new[] { new GarmentFlatten.Segment(Vector3.Zero, new Vector3(0, .5f, 0)) };
        var progress = 0f;
        for (var i = 0; i < 12; i++) progress = GarmentFlatten.Advance(progress, true, 1f / 60);
        Check(progress > .5f && progress < 1, "partial collapse expected");
        for (var i = 0; i < 60; i++) progress = GarmentFlatten.Advance(progress, GarmentFlatten.IsOffBody(new Vector3(0, .2f, .02f), torso, .07f), 1f / 60);
        Check(progress == 0, "segment did not refill around the body");
        Check(GarmentFlatten.IsOffBody(Vector3.Zero, ReadOnlySpan<GarmentFlatten.Segment>.Empty, .07f), "missing body counted as worn");
        Check(GarmentFlatten.Advance(float.NaN, true, 1f / 60) is >= 0 and <= 1, "NaN progress leaked");
    }),
    ("collapse removes depth only: width and length are untouched", () => {
        var lateral = Vector3.UnitX; var along = Vector3.Normalize(new Vector3(.1f, 1, .2f));
        var depth = GarmentFlatten.DepthAxis(along, lateral);
        Check(MathF.Abs(Vector3.Dot(depth, along)) < 1e-5f && MathF.Abs(Vector3.Dot(depth, lateral)) < 1e-5f, "depth not perpendicular");
        var width = Vector3.Normalize(Vector3.Cross(depth, along));
        var origin = new Vector3(1, 2, 3); var scale = GarmentFlatten.ThicknessScale(1);
        var point = origin + along * .3f + width * .12f + depth * .08f;
        var flat = GarmentFlatten.FlattenPoint(point, origin, depth, scale) - origin;
        Check(MathF.Abs(Vector3.Dot(flat, along) - .3f) < 1e-5f, "length changed");
        Check(MathF.Abs(Vector3.Dot(flat, width) - .12f) < 1e-5f, "width changed");
        Check(MathF.Abs(Vector3.Dot(flat, depth) - .08f * GarmentFlatten.CollapsedThickness) < 1e-5f, "depth not collapsed to the panel thickness");
        Check(GarmentFlatten.FlattenPoint(point, origin, depth, 1) == point, "worn scale moved a point");
        var sideways = GarmentFlatten.DepthAxis(Vector3.UnitX, Vector3.UnitX);
        Check(MathF.Abs(sideways.Length() - 1) < 1e-5f && MathF.Abs(Vector3.Dot(sideways, Vector3.UnitX)) < 1e-5f, "degenerate arm direction");
    }),
    ("bone scale and collision box collapse along the axis closest to depth", () => {
        var bone = Quaternion.CreateFromYawPitchRoll(.3f, 1.1f, -.4f);
        for (var axis = 0; axis < 3; axis++)
        {
            var tilted = Vector3.Normalize(GarmentFlatten.LocalAxis(bone, axis) + GarmentFlatten.LocalAxis(bone, (axis + 1) % 3) * .3f);
            Check(GarmentFlatten.NearestLocalAxis(bone, tilted) == axis, "wrong bone axis");
            Check(GarmentFlatten.NearestLocalAxis(bone, -tilted) == axis, "axis sign mattered");
        }
        var half = new Vector3(.07f, .11f, .033f);
        var thin = GarmentFlatten.CollapsedHalfExtents(half, 2);
        Check(thin.X == half.X && thin.Y == half.Y && MathF.Abs(thin.Z - GarmentFlatten.MinCollapsedHalfExtent) < 1e-6f, "depth box not thinned to the floor value");
        var wide = GarmentFlatten.CollapsedHalfExtents(half, 0);
        Check(MathF.Abs(wide.X - .07f * GarmentFlatten.CollapsedThickness) < 1e-6f && wide.Z == half.Z, "wrong extent thinned");
        Check(GarmentFlatten.CollapsedHalfExtents(new Vector3(.005f, .1f, .005f), 2).Z == .005f, "collapse thickened a thin box");
    }),
};
var failures = 0;
foreach (var (name, test) in cases)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
return failures == 0 ? 0 : 1;
