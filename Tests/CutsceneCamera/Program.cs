// Offline probe: read the camera work of the game's own cutscenes from the installed game data.
//   dotnet run -c Release                 → report on the three sample cutscenes + BMP plots
//   dotnet run -c Release -- census [N]   → channel statistics over N cutscenes from the Cutscene sheet
//   dotnet run -c Release -- shot <path> <cttl>  → dump every camera shot of one CTTL

using CutsceneCamera;
using Lumina;
using Lumina.Excel.Sheets;

const string SqPack = @"C:/Program Files (x86)/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/sqpack";
var gd = new GameData(SqPack);
var outDir = Path.Combine(Path.GetTempPath(), "cutscene-research");
Directory.CreateDirectory(outDir);

CutbFile? Load(string sheetPath)
{
    var f = gd.GetFile($"cut/{sheetPath}.cutb");
    return f == null ? null : new CutbFile(f.Data);
}

var mode = args.Length > 0 ? args[0] : "report";
if (mode == "census")
{
    Census.Run(gd, args.Length > 1 ? int.Parse(args[1]) : 400, Load);
    return;
}
if (mode == "conv")
{
    var rows = gd.GetExcelSheet<Cutscene>()!.Select(r => r.Path.ToString()).Where(p => p.Length > 0).ToList();
    var n = args.Length > 1 ? int.Parse(args[1]) : 150;
    Convention.Run(rows.Where((_, i) => i % Math.Max(1, rows.Count / n) == 0).Select(p => { try { return Load(p); } catch { return null; } }).Where(c => c != null)!);
    return;
}
if (mode == "eid")
{
    foreach (var bp in Eid.Read(gd.GetFile(args[1])!.Data)) Console.WriteLine($"  {bp.Id,3} {bp.Bone,-16} off {bp.Offset}");
    return;
}
if (mode == "branch")
{
    Branches.Print(Load(args[1])!);
    return;
}
if (mode == "bind")
{
    var body = new Bind.Body
    {
        Skel = Skeleton.Read(gd.GetFile("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb")!.Data),
        Eid = Eid.Read(gd.GetFile("chara/human/c0101/skeleton/base/b0001/eid_c0101b0001.eid")!.Data).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First()),
    };
    Console.WriteLine($"body: {body.Skel.Names.Length} bones, head (j_kao) at {body.Head}, {body.Eid.Count} bind points");
    var rows = gd.GetExcelSheet<Cutscene>()!.Select(r => r.Path.ToString()).Where(p => p.Length > 0).ToList();
    var n = args.Length > 1 ? int.Parse(args[1]) : 300;
    var sample = rows.Where((_, i) => i % Math.Max(1, rows.Count / n) == 0).Select(p => { try { return Load(p); } catch { return null; } }).Where(c => c != null).Cast<CutbFile>().ToList();
    if (args.Length > 2 && args[2] == "lens") Lens.Run(sample, body);
    else if (args.Length > 2 && args[2] == "seq") Sequence.Run(sample, body);
    else Bind.Run(sample, body);
    return;
}
if (mode == "shot")
{
    var c = Load(args[1])!;
    var tl = c.Timelines[int.Parse(args[2])];
    foreach (var s in CameraShot.FromTimeline(c, tl)) s.Dump(Console.Out, verbose: true);
    return;
}

// Samples: ARR opening (Gridania start), an Endwalker MSQ scene, a Stormblood job quest scene.
var samples = new[]
{
    "ffxiv/manfst/manfst00010/manfst00010",
    "ex4/aktkma/aktkma10110/aktkma10110",
    "ex2/jobsam/jobsam54010/jobsam54010",
};
Bind.DefaultBody = new Bind.Body
{
    Skel = Skeleton.Read(gd.GetFile("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb")!.Data),
    Eid = Eid.Read(gd.GetFile("chara/human/c0101/skeleton/base/b0001/eid_c0101b0001.eid")!.Data).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First()),
};
var sheet = gd.GetExcelSheet<Cutscene>()!;
foreach (var path in samples)
{
    var row = sheet.FirstOrDefault(r => r.Path.ToString() == path);
    var cut = Load(path);
    Console.WriteLine($"==== {path} (Cutscene row {row.RowId})");
    if (cut == null) { Console.WriteLine("  file missing"); continue; }
    Report.Print(cut, Console.Out);
    var bmp = Path.Combine(outDir, Path.GetFileName(path) + ".bmp");
    Plot.TopDown(cut, bmp);
    Console.WriteLine($"  plot: {bmp}");
}
