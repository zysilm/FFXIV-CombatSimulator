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
