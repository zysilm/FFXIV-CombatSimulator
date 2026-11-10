using System.Numerics;
using CombatSimulator.Animation.Attachment;

static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
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
        Check(GarmentTether.IsPinned(1, "j_kosi", 0) && !GarmentTether.IsPinned(1, "j_sebo_a", 0) && !GarmentTether.IsPinned(1, "j_ude_a_l", 0), "top pins");
        Check(GarmentTether.IsPinned(3, "j_asi_b_l", 0) && GarmentTether.IsPinned(3, "j_asi_b_r", 0) && !GarmentTether.IsPinned(3, "j_kosi", 0) && !GarmentTether.IsPinned(3, "j_asi_a_l", 0), "lower pins");
        Check(!GarmentTether.IsPinned(2, "j_te_l", 0) && !GarmentTether.IsPinned(4, "j_asi_b_l", 0), "gloves or boots pinned");
        // Every selectable hang point pins something, never the whole rig, and bad saved values fall back to the default.
        var top = new[] { "j_kosi", "j_sebo_a", "j_sebo_b", "j_sebo_c", "j_sako_l", "j_ude_a_l", "j_ude_b_l", "j_sako_r", "j_ude_a_r", "j_ude_b_r" };
        var lower = new[] { "j_kosi", "j_asi_a_l", "j_asi_b_l", "j_asi_a_r", "j_asi_b_r" };
        for (var i = 0; i < GarmentTether.TopHangLabels.Length; i++)
        { var n = top.Count(b => GarmentTether.IsPinned(1, b, i)); Check(n >= 1 && n < top.Length, $"top hang {i} pins {n}"); }
        for (var i = 0; i < GarmentTether.LowerHangLabels.Length; i++)
        { var n = lower.Count(b => GarmentTether.IsPinned(3, b, i)); Check(n >= 1 && n < lower.Length, $"lower hang {i} pins {n}"); }
        Check(GarmentTether.IsPinned(1, "j_kosi", 99) && GarmentTether.IsPinned(3, "j_asi_b_r", -1), "bad hang index");
        Check(GarmentTether.IsPinned(1, "j_ude_b_l", 3) && !GarmentTether.IsPinned(1, "j_ude_b_r", 3) && GarmentTether.IsPinned(3, "j_asi_a_r", 4), "side-specific hang");
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
