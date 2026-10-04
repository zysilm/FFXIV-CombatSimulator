#if CURRENT
using System.Numerics;
using System.Reflection;
using CombatSimulator;
using CombatSimulator.Effects.BodyFluids;
using CombatSimulator.Effects.BodyFluids.Surface;
using CombatSimulator.Rendering.WorldGeometry;

static class RetainedChecks
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static object Read(object owner,string name)=>owner.GetType().GetField(name,Flags)?.GetValue(owner)
        ?? owner.GetType().GetProperty(name,Flags)!.GetValue(owner)!;
    static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,Flags)!.SetValue(owner,value);
    static object? Call(object owner,string name,params object[] args)=>owner.GetType().GetMethod(name,Flags)!.Invoke(owner,args);
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static double Retained(SalivaRuntime runtime)=>(double)Read(runtime,"RetainedRivuletVolume");
    static void Ledger(SalivaRuntime runtime)=>Require(Math.Abs(runtime.ConservationError)<1e-18,"Retained ownership violated conservation");
    static (CharacterFluidSurface Surface,Configuration Config,SalivaRuntime Runtime) Create(int panels=1)
    {
        var surface=new CharacterFluidSurface(panels);surface.BeginFrame(Matrix4x4.Identity);
        var config=new Configuration{BodyFluidSettingsVersion=2,BodyFluidOutlets=new(){new(){Kind=BodyFluidOutletKind.Mouth,SettingsVersion=1}}};
        var runtime=new SalivaRuntime(surface,config){Emitting=false};Set(runtime,"generation",surface.Generation);
        return(surface,config,runtime);
    }
    static FluidSurfaceSample SeedRibbon(CharacterFluidSurface surface,SalivaRuntime runtime,int sequence)
    {
        const double volume=8e-9;
        var anchor=surface.FindAnchor(new(0,1.11f,0));surface.TryEvaluate(anchor,out var initial);
        int film=(int)Call(runtime,"FindFilm",anchor,0)!;
        var beads=(Array)Read(runtime,"beads");var bead=beads.GetValue(0)!;
        Require((double)Read(bead,"Volume")==0,"Fixture reused a nonempty cap");
        Set(bead,"Created",(long)sequence);Set(bead,"Outlet",0);Set(bead,"Film",film);
        Set(bead,"Volume",volume);Set(bead,"Anchor",anchor);Set(bead,"HasWorldSample",true);Set(bead,"LastWorldSample",initial);
        beads.SetValue(bead,0);
        Set(runtime,"<EmittedVolume>k__BackingField",runtime.EmittedVolume+volume);
        var path=((Array)Read(runtime,"rivulets")).GetValue(0);if(path!=null)Call(path,"Reset");
        FluidSurfaceSample last=initial;
        for(int row=0;row<4;row++)
        {
            anchor=surface.FindAnchor(new(0,1.11f-row*.002f,0));surface.TryEvaluate(anchor,out last);
            Set(runtime,"surfaceBudget",999);Set(runtime,"rivuletSampleBudget",999);
            Call(runtime,"RecordRivulet",0,anchor,last);
        }
        Call(runtime,"PreserveRivulet",0);
        return last;
    }
    public static void Run()
    {
        Persistence();Transitions();WetCoat();Pool();Generation();SourceRecycle();ReservedSourceRecycle();LongSupply();
    }
    static void Persistence()
    {
        var (surface,config,runtime)=Create();var sample=SeedRibbon(surface,runtime,1);
        double retained=Retained(runtime);Require(retained>0,"No independent attached ribbon owner was created");
        Call(runtime,"PreserveRivulet",0);Require(Retained(runtime)==retained,"Repeated preservation duplicated mass");
        Call(runtime,"ReleaseBead",0,sample,Vector3.Zero);Ledger(runtime);
        Require(Retained(runtime)==retained,"Detaching the active cap erased or duplicated its trail");
        var builder=new FluidGeometryBuilder(32766);surface.BeginFrame(Matrix4x4.Identity,true);runtime.AppendGeometry(builder);
        Require(Retained(runtime)==retained && (int)Read(runtime,"RetainedRivuletsDrawn")==0,"Unavailable pose destroyed a retained owner instead of hiding it");
        config.BodyFluidOutlets.Clear();runtime.Emitting=false;
        for(int i=0;i<60;i++){surface.BeginFrame(Matrix4x4.Identity);runtime.Advance(1d/60);}
        builder.Reset();runtime.AppendGeometry(builder);
        Require(Retained(runtime)==retained && (int)Read(runtime,"RetainedRivuletsDrawn")>0,"Removing/stopping supply erased persistent material geometry");
        Ledger(runtime);runtime.Clear();Require(runtime.TotalVolume==0 && Retained(runtime)==0,"Clear failed to retire retained ribbon mass");Ledger(runtime);
        Console.WriteLine("PASS retained: detach, repeated preserve, stop/remove, draw and explicit clear");
    }
    static void Pool()
    {
        var (surface,_,runtime)=Create();
        for(int i=0;i<40;i++)
        {
            var sample=SeedRibbon(surface,runtime,i+1);Call(runtime,"ReleaseBead",0,sample,Vector3.Zero);Ledger(runtime);
        }
        Require(runtime.RetiredVolume>0,"Full fixed ribbon pool never retired its oldest owned inventory");
        var builder=new FluidGeometryBuilder(32766);runtime.AppendGeometry(builder);
        Require((int)Read(runtime,"RetainedRivuletsDrawn")==32,"Resident draw priority did not retain the full 32-ribbon pool");
        Require(builder.Count<=32766,"Resident priority exceeded the common renderer budget");Ledger(runtime);
        Console.WriteLine("PASS retained: 40 ribbon insertions, oldest retirement and 32 complete priority draws");
    }
    static void Transitions()
    {
        var (surface,_,runtime)=Create();var sample=SeedRibbon(surface,runtime,1);
        double volume=Retained(runtime);
        Require((bool)Call(runtime,"TryStartThread",0,sample)!,"Fixture did not convert a traced cap into a pendant");
        var builder=new FluidGeometryBuilder(32766);runtime.AppendGeometry(builder);
        Require(Retained(runtime)==volume && (int)Read(runtime,"RetainedRivuletsDrawn")>0,"Pendant conversion erased or duplicated its retained trail");Ledger(runtime);
        var second=Create();sample=SeedRibbon(second.Surface,second.Runtime,1);volume=Retained(second.Runtime);
        var beads=(Array)Read(second.Runtime,"beads");const double filler=1e-12;
        for(int i=1;i<beads.Length;i++)
        {
            var bead=beads.GetValue(i)!;Set(bead,"Volume",filler);Set(bead,"Created",(long)i+1);beads.SetValue(bead,i);
        }
        Set(second.Runtime,"<EmittedVolume>k__BackingField",second.Runtime.EmittedVolume+(beads.Length-1)*filler);
        Require((int)Call(second.Runtime,"AllocateSourceBead")! == 0,"Fixture did not recycle oldest traced cap");
        builder.Reset();second.Runtime.AppendGeometry(builder);
        Require(Retained(second.Runtime)==volume && (int)Read(second.Runtime,"RetainedRivuletsDrawn")>0,"Actual source-cap pool reuse erased its retained trail");Ledger(second.Runtime);
        Console.WriteLine("PASS retained: pendant conversion and actual source-cap recycling preserve independent traces");
    }
    static void WetCoat()
    {
        var (surface,config,runtime)=Create();config.BodyFluidOutlets[0].ThicknessScale=1;
        // GetOutletSettings uses the last admitted site settings, as real preparation does.
        Call(runtime,"PrepareOutlets");var sample=SeedRibbon(surface,runtime,1);
        Call(runtime,"ReleaseBead",0,sample,Vector3.Zero);double retained=Retained(runtime);
        var builder=new FluidGeometryBuilder(32766);Call(runtime,"AppendRetainedRivulets",builder);
        var clean=builder.Vertices.ToArray();Require(clean.Length>0,"Wet coat fixture did not draw initial trace");
        var film=((SurfaceFilmRuntime[])Read(runtime,"films"))[0];double added=0;
        for(int i=0;i<film.CellCount;i++)added+=film.AddVolume(film.GetAnchor(i),film.GetCell(i).Geometry.Area*.0003);
        Require(added>0,"Could not thicken the complete receiving patch");
        Set(runtime,"<EmittedVolume>k__BackingField",runtime.EmittedVolume+added);
        builder.Reset();Call(runtime,"AppendRetainedRivulets",builder);
        Require(builder.Count==clean.Length && Retained(runtime)==retained,"Wet coat changed trace ownership or topology");
        for(int i=0;i<clean.Length;i++)
        {
            Require(builder.Vertices[i].Position.Z>=clean[i].Position.Z+.000299f,"Existing thick wet coat buried attached trace geometry");
            Require(builder.Vertices[i].Coverage==clean[i].Coverage,"Broad wet coat incorrectly replaced the trace's own optical coverage");
        }
        var wetFilm=new FluidGeometryBuilder(32766);film.AppendGeometry(wetFilm);
        Require(wetFilm.Count>0,"Thickened film produced no geometry");
        float coatSurface=0;foreach(var vertex in wetFilm.Vertices)coatSurface=Math.Max(coatSurface,vertex.Position.Z);
        foreach(var vertex in builder.Vertices)Require(vertex.Position.Z>=coatSurface-1e-7f,"Actual drawn trace lies below actual wet-film free surface");
        Ledger(runtime);Console.WriteLine("PASS retained: 0.3 mm wet coat lifts trace outside film while preserving trace coverage and mass");
    }
    static void Generation()
    {
        var (surface,_,runtime)=Create();var sample=SeedRibbon(surface,runtime,1);
        Call(runtime,"ReleaseBead",0,sample,Vector3.Zero);double original=Retained(runtime);
        Require(original>0,"Generation fixture had no retained inventory");
        surface.Generation++;surface.BeginFrame(Matrix4x4.Identity);runtime.Advance(1d/60);
        Require(Retained(runtime)==0 && runtime.RetiredVolume>=original,"Unverified replacement topology retained old material triangle anchors");Ledger(runtime);
        Console.WriteLine("PASS retained: replacement generation retires stale attached mass conservatively");
    }
    static void SourceRecycle()
    {
        var (surface,_,runtime)=Create(6);const double held=1e-10;
        int other=(int)Call(runtime,"FindFilm",new FluidSurfaceAnchor(surface.Generation,4*256+3,new(1f/3)),1)!;
        var films=(SurfaceFilmRuntime[])Read(runtime,"films");Require(films[other].AddVolume(held)==held,"Other source fixture could not admit mass");
        for(int i=0;i<4;i++)
        {
            int bank=(int)Call(runtime,"FindFilm",new FluidSurfaceAnchor(surface.Generation,i*256+3,new(1f/3)),0)!;
            Require(bank>=0 && films[bank].AddVolume(held)==held,"Source fixture could not fill four wet banks");
        }
        Set(runtime,"<EmittedVolume>k__BackingField",5*held);
        int result=(int)Call(runtime,"FindOutletFilm",0,new FluidSurfaceAnchor(surface.Generation,5*256+3,new(1f/3)))!;
        Require(result>=0,"Source binding silently stopped at the fifth wet patch");
        Require(films[other].Volume==held,"Recycling one source touched another source's wet bank");Ledger(runtime);
        Console.WriteLine("PASS retained: fifth source patch recycles its own bank without clearing other sources");
    }
    static void LongSupply()
    {
        var (surface,config,runtime)=Create(6);runtime.Emitting=true;
        double requested=0;
        for(int frame=0;frame<2400;frame++)
        {
            surface.MouthTriangle=(frame/60%6)*256+3;
            surface.BeginFrame(Matrix4x4.Identity);double before=runtime.EmittedVolume;runtime.Advance(1d/60);
            double supply=config.BodyFluidOutlets[0].FlowMlPerSecond*1e-6/60;requested+=supply;
            Require(Math.Abs(runtime.EmittedVolume-before-supply)<1e-18,$"Continuous source stopped at frame {frame}");Ledger(runtime);
        }
        Require(Math.Abs(runtime.EmittedVolume-requested)<1e-18,"Long-running admitted supply disagreed with configured flow");
        Console.WriteLine("PASS retained: 2400 frames of indefinite supply across six disconnected wet patches");
    }
    static void ReservedSourceRecycle()
    {
        var (surface,_,runtime)=Create(6);const double held=1e-10, capVolume=4e-9;
        var beads=(Array)Read(runtime,"beads");
        for(int i=0;i<4;i++)
        {
            var anchor=new FluidSurfaceAnchor(surface.Generation,i*256+3,new(1f/3));surface.TryEvaluate(anchor,out var sample);
            int film=(int)Call(runtime,"FindFilm",anchor,0)!;
            Require(((SurfaceFilmRuntime[])Read(runtime,"films"))[film].AddVolume(held)==held,"Could not seed reserved bank wetness");
            var bead=beads.GetValue(i)!;Set(bead,"Volume",capVolume);Set(bead,"Film",film);Set(bead,"Anchor",anchor);
            Set(bead,"HasWorldSample",true);Set(bead,"LastWorldSample",sample);Set(bead,"Created",(long)i+1);
            beads.SetValue(bead,i);
            Require((bool)Call(runtime,"TryStartThread",i,sample)!,"Could not reserve all four film banks with owned threads");
        }
        Set(runtime,"<EmittedVolume>k__BackingField",4*(held+capVolume));
        var threads=(Array)Read(runtime,"threads");var first=threads.GetValue(0)!;
        var anchorBefore=(FluidSurfaceAnchor)Read(first,"Anchor");
        var model=(CombatSimulator.Effects.BodyFluids.Simulation.ViscoelasticFilament)Read(first,"Model");
        double volumeBefore=model.TotalVolume;Vector3 pointBefore=model.GetNodePosition(0);
        int newBank=(int)Call(runtime,"FindOutletFilm",0,new FluidSurfaceAnchor(surface.Generation,5*256+3,new(1f/3)))!;
        Require(newBank>=0 && (int)Read(first,"Bead")==-1,"Fully reserved banks blocked continuous source binding");
        var anchorAfter=(FluidSurfaceAnchor)Read(first,"Anchor");
        Require((bool)Read(first,"Attached") && anchorAfter.Triangle==anchorBefore.Triangle && anchorAfter.Barycentric==anchorBefore.Barycentric && anchorAfter.Generation==anchorBefore.Generation,
            "Source bank reuse moved or detached old thread's material endpoint");
        Require(model.TotalVolume==volumeBefore && model.GetNodePosition(0)==pointBefore,"Source recycling changed old thread-owned mass or geometry");Ledger(runtime);
        Console.WriteLine("PASS retained: all four reserved banks recycle without removing or moving old thread inventory");
    }
}
#endif
