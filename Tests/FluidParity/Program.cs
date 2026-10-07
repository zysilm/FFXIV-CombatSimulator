using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using CombatSimulator;
using CombatSimulator.Effects.BodyFluids;
using CombatSimulator.Effects.BodyFluids.Simulation;
using CombatSimulator.Effects.BodyFluids.Surface;
using CombatSimulator.Rendering.WorldGeometry;
CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
if(args.Contains("--contacts")){ ContactChecks.Run(); return; }
if(args.Contains("--ground")){ GroundChecks.Run(); return; }
#if CURRENT
if(args.Contains("--retained")){ RetainedChecks.Run(); return; }
#endif
string output=args.Length>0?args[0]:"results.csv";
using var writer=new StreamWriter(output);
writer.WriteLine("scenario,frame,emitted,retired,total,error,deferred,film,cap,thread,drop,ground,reservoir,retained,capcount,threadcount,dropcount,groundcells,vertices,xsum,ysum,zsum,thicknesssum,coveragesum,statehash,geometryhash");
foreach(string scenario in new[]{"upright","downward","roll","corner","budget","generation","stop","capacity","wetpatch","parameters"}) {
 var config=new Configuration {BodyFluidSettingsVersion=2};
 float flow=scenario=="capacity"?.12f:.006f;
 config.BodyFluidFlowMlPerSecond=flow;
#if CURRENT
 config.BodyFluidOutlets=new(){new(){Kind=BodyFluidOutletKind.Mouth,SettingsVersion=1,FlowMlPerSecond=flow}};
#endif
 var surface=new CharacterFluidSurface(scenario=="wetpatch"?6:1); var runtime=new SalivaRuntime(surface,config){Emitting=true}; var builder=new FluidGeometryBuilder(32766);
 for(int frame=0;frame<600;frame++) {
  double time=frame/60.0;Matrix4x4 pose=Matrix4x4.Identity;
  if(scenario=="downward")pose=Matrix4x4.CreateRotationX(MathF.PI/2)*Matrix4x4.CreateTranslation(0,.65f,-.8f);
  if(scenario=="roll"){float angle=Math.Clamp((frame-90)/120f,0,1)*MathF.PI*.75f;pose=Matrix4x4.CreateRotationX(angle)*Matrix4x4.CreateTranslation(0,.3f,0);}
  if(scenario=="corner") {surface.MouthTriangle=(frame/90)%3 switch{0=>3,1=>13,_=>235};surface.MouthBary=(frame/45)%2==0?new(.3f,.3f,.4f):new(.1f,.6f,.3f);}
  if(scenario=="wetpatch")surface.MouthTriangle=Math.Min(frame/75,5)*256+3;
  if(scenario=="parameters"){config.BodyFluidViscosityPaSeconds=frame<300?.4f:.8f;config.BodyFluidStringiness=12f;config.BodyFluidSurfaceSpeed=.2f;config.BodyFluidThicknessScale=3f;config.BodyFluidReflectionStrength=.7f;config.BodyFluidCloudiness=.6f;config.BodyFluidFoamAmount=.5f;pose=Matrix4x4.CreateRotationX(MathF.PI/2)*Matrix4x4.CreateTranslation(0,.65f,-.8f);
#if CURRENT
   var site=config.BodyFluidOutlets[0];site.ViscosityPaSeconds=config.BodyFluidViscosityPaSeconds;site.Stringiness=12;site.SurfaceSpeed=.2f;site.ThicknessScale=3;site.ReflectionStrength=.7f;site.Cloudiness=.6f;site.FoamAmount=.5f;
#endif
  }
  if(scenario=="capacity")surface.MouthTriangle=(frame/20*37)%256;
  if(scenario=="generation"&&frame==250)surface.Generation++;
  if(scenario=="stop"&&frame==200)runtime.Emitting=false;
  surface.BeginFrame(pose,scenario=="budget"&&frame%90>=60&&frame%90<75);
  runtime.Advance(1.0/60);builder.Reset();runtime.AppendGeometry(builder);
#if CURRENT
  // Matching clear Mouth settings must produce the original optical uniforms,
  // including the dynamically changed appearance parameters in this suite.
  if(config.CreateBodyFluidMaterial(config.BodyFluidOutlets[0])!=config.CreateBodyFluidMaterial())
   throw new Exception($"Legacy optical parameter mismatch {scenario}/{frame}");
#endif
  var films=(SurfaceFilmRuntime[])Read(runtime,"films");var beads=(Array)Read(runtime,"beads");var threads=(Array)Read(runtime,"threads");var drops=(Array)Read(runtime,"drops");
  double fv=films.Sum(x=>x.Volume),bv=SumVolumes(beads),dv=SumVolumes(drops),tv=0,gv=0;int bc=CountVolumes(beads),dc=CountVolumes(drops),tc=0,gc=0;
  foreach(var t in threads){var model=(ViscoelasticFilament)Read(t!,"Model");tv+=model.TotalVolume;if(model.TotalVolume>0)tc++;}
#if CURRENT
  foreach(var ground in (GroundFilmRuntime?[])Read(runtime,"grounds"))if(ground!=null){gv+=ground.Volume;gc+=ground.CellCount;}
#else
  var ground=(GroundFilmRuntime)Read(runtime,"ground");gv=ground.Volume;gc=ground.CellCount;
#endif
  double retainedVolume=0;
#if CURRENT
  retainedVolume=(double)(runtime.GetType().GetProperty("RetainedRivuletVolume",BindingFlags.Instance|BindingFlags.Public)?.GetValue(runtime)??0d);
#endif
  double xs=0,ys=0,zs=0,ts=0,cs=0;foreach(var v in builder.Vertices){xs+=v.Position.X;ys+=v.Position.Y;zs+=v.Position.Z;ts+=v.Thickness;cs+=v.Coverage;}
  using var state=new MemoryStream();using(var b=new BinaryWriter(state,System.Text.Encoding.UTF8,true)){
   // Compare wet inventory and material coordinates rather than empty pool capacity or owner tags.
   foreach(var film in films)if(film.Volume>0){b.Write(film.Volume);for(int c=0;c<film.CellCount;c++){var cell=film.GetCell(c);if(cell.Volume<=0)continue;b.Write(film.GetAnchor(c).Triangle);b.Write(cell.Volume);b.Write(cell.Thickness);}}
   foreach(var bead in beads)if((double)Read(bead!,"Volume")>0){b.Write((double)Read(bead!,"Volume"));var anchor=(FluidSurfaceAnchor)Read(bead!,"Anchor");b.Write(anchor.Triangle);Vec(b,anchor.Barycentric);b.Write((double)Read(bead!,"RunoffDistance"));b.Write((bool)Read(bead!,"WalkBlocked"));}
   foreach(var t in threads){var m=(ViscoelasticFilament)Read(t!,"Model");if(m.TotalVolume<=0)continue;b.Write(m.TotalVolume);b.Write(m.TerminalVolume);b.Write(m.NodeCount);for(int n=0;n<m.NodeCount;n++){Vec(b,m.GetNodePosition(n));Vec(b,m.GetNodeVelocity(n));}for(int segment=0;segment<m.SegmentCount;segment++){var sg=m.GetSegment(segment);b.Write(sg.Volume);b.Write(sg.Radius);b.Write(sg.TensileStress);b.Write(sg.Broken);b.Write(sg.PendingBreakVolume);}b.Write((double)Read(t!,"TimeDebt"));b.Write((double)Read(t!,"Deferred"));b.Write((bool)Read(t!,"Attached"));b.Write((bool)Read(t!,"TipAttached"));b.Write((bool)Read(t!,"PendingContact"));b.Write((int)Read(t!,"ContactSegment"));}
   foreach(var drop in drops)if((double)Read(drop!,"Volume")>0){b.Write((double)Read(drop!,"Volume"));Vec(b,(Vector3)Read(drop!,"Position"));Vec(b,(Vector3)Read(drop!,"Velocity"));}
  }
  foreach(var g in GroundOwners(runtime))if(g!=null&&g.Volume>0){var film=(SurfaceFilm)Read(g,"film");using var b=new BinaryWriter(state,System.Text.Encoding.UTF8,true);b.Write(g.Volume);for(int c=0;c<film.CellCount;c++){var cell=film.GetCell(c);b.Write(cell.Volume);Vec(b,cell.Geometry.A);Vec(b,cell.Geometry.B);Vec(b,cell.Geometry.C);}}
#if CURRENT
  var residentField=runtime.GetType().GetField("retainedRivulets",BindingFlags.Instance|BindingFlags.NonPublic);
  if(residentField?.GetValue(runtime) is Array residents){using var b=new BinaryWriter(state,System.Text.Encoding.UTF8,true);foreach(var resident in residents){if(resident==null||(double)Read(resident,"Volume")<=0)continue;b.Write((double)Read(resident,"Volume"));b.Write((int)Read(resident,"Outlet"));var path=Read(resident,"Path");int rows=(int)Read(path,"Count");b.Write(rows);var anchors=(FluidSurfaceAnchor[])Read(path,"Samples");for(int i=0;i<rows*7;i++){b.Write(anchors[i].Generation);b.Write(anchors[i].Triangle);Vec(b,anchors[i].Barycentric);}}}
#endif
  string sh=Convert.ToHexString(SHA256.HashData(state.ToArray()));string gh=Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(builder.Vertices)));
  writer.WriteLine(string.Join(',',scenario,frame,runtime.EmittedVolume.ToString("R"),runtime.RetiredVolume.ToString("R"),runtime.TotalVolume.ToString("R"),runtime.ConservationError.ToString("R"),runtime.DeferredSeconds.ToString("R"),fv.ToString("R"),bv.ToString("R"),tv.ToString("R"),dv.ToString("R"),gv.ToString("R"),(runtime.TotalVolume-fv-bv-tv-dv-gv-retainedVolume).ToString("R"),retainedVolume.ToString("R"),bc,tc,dc,gc,builder.Count,xs.ToString("R"),ys.ToString("R"),zs.ToString("R"),ts.ToString("R"),cs.ToString("R"),sh,gh));
  if(!double.IsFinite(runtime.TotalVolume)||Math.Abs(runtime.ConservationError)>1e-13)throw new Exception($"Conservation failure {scenario}/{frame}: {runtime.ConservationError}");
 }
 Console.WriteLine($"{scenario}: total={runtime.TotalVolume:R}, emitted={runtime.EmittedVolume:R}, retired={runtime.RetiredVolume:R}");
}
#if CURRENT
MultiSourceChecks();
#endif
static object Read(object target,string field)=>target.GetType().GetField(field,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.GetValue(target)!;
static double SumVolumes(Array values){double sum=0;foreach(var value in values)sum+=(double)Read(value!,"Volume");return sum;}
static int CountVolumes(Array values){int count=0;foreach(var value in values)if((double)Read(value!,"Volume")>0)count++;return count;}
static void Vec(BinaryWriter writer,Vector3 v){writer.Write(v.X);writer.Write(v.Y);writer.Write(v.Z);}

static IEnumerable<GroundFilmRuntime?> GroundOwners(SalivaRuntime runtime){
#if CURRENT
 return (GroundFilmRuntime?[])Read(runtime,"grounds");
#else
 return new[]{(GroundFilmRuntime)Read(runtime,"ground")};
#endif
}
#if CURRENT
static void MultiSourceChecks(){
 var config=new Configuration{BodyFluidSettingsVersion=2,BodyFluidOutlets=new()};for(int i=0;i<9;i++)config.BodyFluidOutlets.Add(new(){Kind=(BodyFluidOutletKind)i,SettingsVersion=1,FlowMlPerSecond=.006f,BloodTint=i%2==1,ThicknessScale=i%2==0?2:3,ViscosityPaSeconds=.18f+i*.03f});
 var surface=new CharacterFluidSurface(9);var runtime=new SalivaRuntime(surface,config){Emitting=true};var builder=new FluidGeometryBuilder(32766);double expected=0;
 for(int frame=0;frame<120;frame++){
  if(frame==30)config.BodyFluidOutlets.RemoveAt(2);
  if(frame==60){config.BodyFluidOutlets[1].FlowMlPerSecond=.09f;config.BodyFluidOutlets[1].ThicknessScale=4;}
  surface.BeginFrame(Matrix4x4.Identity);runtime.Advance(1.0/60);expected+=config.BodyFluidOutlets.Sum(x=>(double)x.FlowMlPerSecond)*1e-6/60;
  if(Math.Abs(runtime.EmittedVolume-expected)>1e-18||Math.Abs(runtime.ConservationError)>1e-13)throw new Exception($"Multi-source ledger failure frame {frame}");
  var states=(Array)Read(runtime,"outletStates");if(frame==30){for(int i=0;i<9;i++){if(i==2)continue;if((double)Read(states.GetValue(i)!,"AcceptedSeconds")<30/60.0)throw new Exception("Removing site cleared another source");}}
  if(frame==61){if(config.BodyFluidOutlets[0].FlowMlPerSecond!=.006f||config.BodyFluidOutlets[0].ThicknessScale!=2)throw new Exception("Independent settings mutated another source");}
 }
 builder.Reset();runtime.AppendGeometry(builder);var groups=new int[9];var offsets=new int[9];var output=new FluidVertex[32766];builder.PartitionGroups(output,groups,offsets);
 if(groups[0]==0||groups[1]==0||groups.Sum()!=builder.Count)throw new Exception("Per-source geometry grouping failed");
 var clear=config.CreateBodyFluidMaterial(config.BodyFluidOutlets[0]);var red=config.CreateBodyFluidMaterial(config.BodyFluidOutlets[1]);if(clear.Absorption==red.Absorption)throw new Exception("Independent colors lost");
 Console.WriteLine($"multi-source: accepted emitted={runtime.EmittedVolume:R}, ledger={runtime.ConservationError:R}, groups={string.Join('/',groups)}, independent flow/color/settings and removal pass");
}
#endif
