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
    ("legacy rigid and invalid constructions map by slot", () => {
        Check(AttachmentSettings.TemplateForSlot((GarmentTemplate)5, 3) == GarmentTemplate.Trousers, "legacy legs stay rigid");
        Check(AttachmentSettings.TemplateForSlot((GarmentTemplate)5, 1) == GarmentTemplate.Top, "legacy body stays rigid");
        Check(AttachmentSettings.TemplateForSlot(GarmentTemplate.Skirt, 3) == GarmentTemplate.Trousers, "legacy skirt did not use general lower-body motion");
        Check(AttachmentSettings.TemplateForSlot(GarmentTemplate.Dress, 1) == GarmentTemplate.Dress, "dress lost");
        Check(AttachmentSettings.TemplateForSlot((GarmentTemplate)999, 3) == GarmentTemplate.Trousers, "bad legs configuration");
    }),
    ("both clothing slots retain three metre slip settings", () => {
        foreach (var template in new[] { GarmentTemplate.Top, GarmentTemplate.Trousers })
        {
            var s = new AttachmentSettings { Template = template, SlipDistance = 3 };
            var saved = System.Text.Json.JsonSerializer.Deserialize<AttachmentSettings>(System.Text.Json.JsonSerializer.Serialize(s))!.Validated();
            Check(saved.SlipDistance == 3, "three metre distance truncated");
            s.SlipDistance = 4; Check(s.Validated().SlipDistance == 3, "distance range exceeded");
        }
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AttachmentSettings>("{\"BodyFitEnabled\":true,\"HipHalfWidth\":0.4,\"SlipDistance\":2.5}")!;
        Check(legacy.Validated().SlipDistance == 2.5f && !System.Text.Json.JsonSerializer.Serialize(legacy).Contains("BodyFit"), "removed settings broke migration");
    }),
};
var failures = 0;
foreach (var (name, test) in cases)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
return failures == 0 ? 0 : 1;
