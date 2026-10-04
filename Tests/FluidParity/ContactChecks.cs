using System.Numerics;
using System.Reflection;
using CombatSimulator;
using CombatSimulator.Effects.BodyFluids;
using CombatSimulator.Effects.BodyFluids.Surface;
using CombatSimulator.Rendering.WorldGeometry;

static class ContactChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static object Read(object owner, string field) => owner.GetType().GetField(field, Flags)!.GetValue(owner)!;
    static void Set(object owner, string field, object value) => owner.GetType().GetField(field, Flags)!.SetValue(owner, value);
    static object? Call(object owner, string method, params object[] args) => owner.GetType().GetMethod(method, Flags)!.Invoke(owner, args);
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static SalivaRuntime Create(CharacterFluidSurface surface) => new(surface, new Configuration { BodyFluidSettingsVersion = 2 }) { Emitting = false };
    static void SeedDrop(SalivaRuntime runtime, Vector3 position, double volume)
    {
        var method = runtime.GetType().GetMethod("TryAddDrop", Flags)!;
        object[] values = method.GetParameters().Length == 5
            ? new object[] { position, Vector3.Zero, volume, 0d, 0 }
            : new object[] { position, Vector3.Zero, volume, 0d };
        method.Invoke(runtime, values);
        Set(runtime, "<EmittedVolume>k__BackingField", volume);
    }
    static void Budget(SalivaRuntime runtime, int terrain, int surface)
    { Set(runtime, "terrainBudget", terrain); Set(runtime, "surfaceBudget", surface); }
    public static void Run()
    {
        var failures = new List<string>();
        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine($"PASS contact: {name}"); }
            catch (Exception ex) { failures.Add(name); Console.WriteLine($"FAIL contact: {name}: {ex.GetBaseException().Message}"); }
        }
        Check("zero terrain and skin budgets", () => FreeFall(0, 0, false));
        Check("zero skin budget", () => FreeFall(32, 0, false));
        Check("unavailable native skin query", () => FreeFall(32, 40, true));
        Check("deferred ground sweep and conservation", GroundSweep);
        Check("saturated body transfers remain permeable", BodyPermeability);
        Check("partial skin transfer continues to ground in same sweep", PartialSkinGround);
        Check("terrain queries fairly visit the drop pool", GroundQueryFairness);
        Check("convex rivulet material support", CurvedRivulet);
        if (failures.Count > 0) throw new Exception($"{failures.Count} contact checks failed: {string.Join(", ", failures)}");
    }
    static void FreeFall(int terrain, int skin, bool unavailable)
    {
        var surface = new CharacterFluidSurface(); surface.BeginFrame(Matrix4x4.Identity, unavailable);
        var runtime = Create(surface); const double volume = 1e-10; var initial = new Vector3(2, 1, 2);
        SeedDrop(runtime, initial, volume); Budget(runtime, terrain, skin); Call(runtime, "StepDrops", .02d);
        var drop = ((Array)Read(runtime, "drops")).GetValue(0)!;
        var position = (Vector3)Read(drop, "Position"); var velocity = (Vector3)Read(drop, "Velocity");
        Require(Math.Abs(position.Y - (1 - 9.81f * .02f * .02f / 2)) < 1e-6, "Missing ballistic displacement under optional contact starvation");
        Require(Math.Abs(velocity.Y + 9.81f * .02f) < 1e-6, "Gravity velocity stalled or reflected");
        Require(Math.Abs(runtime.ConservationError) < 1e-18, "Free fall lost volume");
    }
    static void GroundSweep()
    {
        var surface = new CharacterFluidSurface(); surface.BeginFrame(Matrix4x4.Identity);
        var runtime = Create(surface); const double volume = 1e-10;
        SeedDrop(runtime, new(2, .01f, 2), volume);
        for(int i = 0; i < 5; i++) { Budget(runtime, 0, 0); Call(runtime, "StepDrops", .02d); }
        var drop = ((Array)Read(runtime, "drops")).GetValue(0)!;
        Require(((Vector3)Read(drop, "Position")).Y < 0, "Gravity froze before deferred sweep crossed ground");
        Budget(runtime, 32, 0); Call(runtime, "StepDrops", .02d);
        double remaining = 0; foreach(var value in (Array)Read(runtime, "drops")) remaining += (double)Read(value!, "Volume");
        Require(remaining < volume * 1e-6, "Historical ground crossing was not caught when query budget returned");
        Require(Math.Abs(runtime.TotalVolume - volume) < 1e-18 && Math.Abs(runtime.ConservationError) < 1e-18, "Deferred deposition lost inventory");
    }
    static void CurvedRivulet()
    {
        var surface = new CharacterFluidSurface(cylinderRadius: .003f); surface.BeginFrame(Matrix4x4.Identity);
        var runtime = Create(surface); var beads = (Array)Read(runtime, "beads"); var bead = beads.GetValue(0)!;
        Set(bead, "Volume", 8e-9d); beads.SetValue(bead, 0);
        for(int row=0; row<4; row++)
        {
            var anchor = surface.FindAnchor(new Vector3(0, 1.11f-row*.002f, 0));
            Require(surface.TryEvaluate(anchor, out var sample), "Fixture anchor failed");
            Budget(runtime, 999, 999); Set(runtime, "rivuletSampleBudget", 999);
            Call(runtime, "RecordRivulet", 0, anchor, sample);
        }
        var builder = new FluidGeometryBuilder(32766);
        Require((bool)Call(runtime, "AppendRivulet", 0, builder)!, "Curved fixture did not generate production rivulet");
        var path = ((Array)Read(runtime, "rivulets")).GetValue(0)!;
        int count=(int)Read(path,"Count")*7;
        var bases=(Vector3[])Read(path,"Base"); var free=(Vector3[])Read(path,"Free");
        float minimum=1; for(int i=0;i<count;i++)
        {
            Require(Math.Abs(surface.ConvexSupportHeight(bases[i])) < 2e-7f, $"Material sample {i} was replaced by a chord inside convex skin");
            minimum=Math.Min(minimum,surface.ConvexSupportHeight(free[i]));
        }
        Require(minimum >= .000075f, $"Rivulet free surface penetrates support: {minimum:R}");
        Require(builder.Count > 0, "Production geometry missing");
    }
    static void BodyPermeability()
    {
        var surface = new CharacterFluidSurface(6); surface.BeginFrame(Matrix4x4.Identity);
        var runtime = Create(surface); var films=(SurfaceFilmRuntime[])Read(runtime,"films");
        const double held = 1e-10, falling = 1e-10;
        for(int i=0; i<4; i++)
        {
            Require(films[i].TryBind(new FluidSurfaceAnchor(surface.Generation, i*256+3, new Vector3(1f/3))), "Could not fill independent receiving patch");
            Require(films[i].AddVolume(held) == held, "Could not seed wet patch");
        }
        SeedDrop(runtime,new(10, 1, .01f),falling);
        Set(runtime,"<EmittedVolume>k__BackingField",4*held+falling);
        var drops=(Array)Read(runtime,"drops"); var drop=drops.GetValue(0)!;
        Set(drop,"Velocity",new Vector3(0,0,-1)); drops.SetValue(drop,0);
        Budget(runtime,32,40); Call(runtime,"StepDrops",.02d);
        drop=drops.GetValue(0)!;
        Require((double)Read(drop,"SkinCooldown") > .1, "Fixture did not exercise a refused body wetting transfer");
        Require(((Vector3)Read(drop,"Position")).Z < 0, "Full body receiving pool pinned incoming drop at skin contact");
        var velocity=(Vector3)Read(drop,"Velocity");
        Require(velocity.Z == -1 && velocity.Y < 0, "Skin response reflected or zeroed incoming velocity");
        Require((double)Read(drop,"Volume") == falling && Math.Abs(runtime.ConservationError)<1e-18,"Permeable failed transfer changed inventory");
    }
    static void PartialSkinGround()
    {
        var surface=new CharacterFluidSurface();
        surface.BeginFrame(Matrix4x4.CreateRotationX(-MathF.PI/2)*Matrix4x4.CreateTranslation(0,.4f,0));
        var runtime=Create(surface); const double volume=1e-7;
        SeedDrop(runtime,new(0,.5f,-1),volume);
        var drops=(Array)Read(runtime,"drops");var drop=drops.GetValue(0)!;
        Set(drop,"Velocity",new Vector3(0,-2,0));drops.SetValue(drop,0);
        Budget(runtime,32,40);Call(runtime,"StepDrops",.3d);
        double skin=((SurfaceFilmRuntime[])Read(runtime,"films")).Sum(x=>x.Volume);
        foreach(var bead in (Array)Read(runtime,"beads"))skin+=(double)Read(bead!,"Volume");
        Require(skin>0 && skin<volume,"Fixture did not exercise partial skin acceptance");
#if CURRENT
        double ground=((CombatSimulator.Effects.BodyFluids.GroundFilmRuntime?[])Read(runtime,"grounds")).Sum(x=>x?.Volume??0);
#else
        double ground=((CombatSimulator.Effects.BodyFluids.GroundFilmRuntime)Read(runtime,"ground")).Volume;
#endif
        Require(ground>volume*.01,"Skin's remaining free drop missed an already-known ground crossing");
        Require(Math.Abs(runtime.TotalVolume-volume)<1e-18 && Math.Abs(runtime.ConservationError)<1e-18,"Same-sweep multi-owner transfer lost inventory");
    }
    static void GroundQueryFairness()
    {
        var surface=new CharacterFluidSurface();surface.BeginFrame(Matrix4x4.Identity);
        var runtime=Create(surface);const double volume=1e-10;
        for(int i=0;i<6;i++)SeedDrop(runtime,new(2+i,10,2),volume);
        Set(runtime,"<EmittedVolume>k__BackingField",6*volume);
        var last=new int[6];Array.Fill(last,-1);
        try { for(int frame=0;frame<12;frame++)
        {
            int now=frame;
            FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.QueryStart=start=>{
                int index=(int)Math.Round(start.X)-2;
                if(index>=0 && index<last.Length)last[index]=now;
            };
            Budget(runtime,2,40);Call(runtime,"StepDrops",.005d);
            for(int i=0;i<6;i++)
            {
                if(frame>=2)Require(last[i]>=0 && frame-last[i]<=2,$"Drop {i} waited beyond ceil(pool/budget) query batches");
            }
        } } finally { FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.QueryStart=null; }
        Require(Math.Abs(runtime.ConservationError)<1e-18,"Fair query scheduling altered inventory");
    }
}
