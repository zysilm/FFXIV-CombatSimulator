using System.Globalization;
using DeathShots;

// Offline check of the automatic death shots (docs/DEATH_SHOTS.md).
//   dotnet run -c Release                      unit checks, then the metric run and its gates (exit code 0 = all pass)
//   dotnet run -c Release -- stats             what the exported trajectories contain
//   dotnet run -c Release -- unit              unit checks only
//   dotnet run -c Release -- sheets [n]        contact sheets (PNG) of n recordings into %TEMP%\DeathShots\sheets
//   dotnet run -c Release -- probe <name> <profile> [0..7]   one run on one recording, printed twice a second
//   dotnet run -c Release -- face              which axis of the head bone points out of the face (calibration)
//   dotnet run -c Release -- where             where each shot sits on a lying / standing body (for the documentation)
//   dotnet run -c Release -- hash [--save <file> | --against <file>]
//                                              one hash per Low three-quarter run (default options) over every pose;
//                                              --against compares with a saved list: equal = bit-identical output
//   ... --traj <folder>                        trajectories from somewhere else (default %TEMP%\DeathShots\traj)
// The trajectories come from the user's ragdoll recordings:
//   Tests/RagdollReplay: dotnet run -c Release -- <ragdoll-rec folder> --export-trajectories
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var folder = Path.Combine(Path.GetTempPath(), "DeathShots", "traj");
var at = Array.IndexOf(args, "--traj");
if (at >= 0 && at + 1 < args.Length) folder = args[at + 1];
var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "run";

// Death guidance (ragdoll), phase P0: tables and sheets over the what-if re-simulations of Tests/RagdollReplay
// (Guidance.cs). They read <root>/<profile>/s<percent>/ (default %TEMP%\DeathGuidance\whatif), not the trajectory folder:
//   dotnet run -c Release -- guidance [root] [--start 100]      Limp against the tension profiles, per start class
//   dotnet run -c Release -- guidance-validate [root] --traj <folder>   the "recorded" what-if against the recordings
//   dotnet run -c Release -- guidance-sheets [root] [n]         stick-figure sheets into <root>\sheets
if (command is "guidance" or "guidance-validate" or "guidance-sheets")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var root = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : Path.Combine(Path.GetTempPath(), "DeathGuidance", "whatif");
    // --profiles a,b,c: the folders to compare, the baseline first (default limp,hold-legs,hold-pose,slow-slump).
    if (Array.IndexOf(args, "--profiles") is var profilesAt and >= 0 && profilesAt + 1 < args.Length) Guidance.Profiles = args[profilesAt + 1].Split(',');
    return command switch
    {
        "guidance" => Guidance.Table(root, Array.IndexOf(args, "--start") is var startAt and >= 0 && startAt + 1 < args.Length && int.TryParse(args[startAt + 1], out var onlyStart) ? onlyStart : -1),
        "guidance-validate" => Guidance.Validate(root, folder),
        _ => Guidance.SheetsRun(root, args.Length > 2 && int.TryParse(args[2], out var sheetCount) ? sheetCount : 6, args.Contains("--zoom")),
    };
}

var failures = 0;
if (command is "unit" or "run") failures += UnitChecks.Run();
if (command == "unit") return failures == 0 ? 0 : 1;

if (!Directory.Exists(folder) || Directory.GetFiles(folder, "*.dstraj").Length == 0)
{
    Console.Error.WriteLine($"no trajectories in {folder}: export them first (Tests/RagdollReplay --export-trajectories)");
    return 2;
}
var all = Directory.GetFiles(folder, "*.dstraj").OrderBy(p => p, StringComparer.Ordinal).Select(Trajectory.Read).ToList();
switch (command)
{
    case "stats":
        Stats.Run(all);
        return 0;
    case "probe":
        Probe.Run(all, args.Length > 1 ? args[1] : "", args.Length > 2 ? args[2] : "Auto", args.Length > 3 && int.TryParse(args[3], out var k) ? k : 0);
        return 0;
    case "face":
        FaceAxis.Run(all);
        return 0;
    case "where":
        UnitChecks.Where();
        return 0;
    case "hash":
        return MetricRun.Hash(all, args.Length > 2 && args[1] == "--save" ? args[2] : null, args.Length > 2 && args[1] == "--against" ? args[2] : null);
    case "sheets":
        Sheets.Run(all, args.Length > 1 && int.TryParse(args[1], out var n) ? n : 6);
        return 0;
    default:
        failures += MetricRun.Run(all, args.Contains("--verbose"));
        Console.WriteLine(failures == 0 ? "== ALL PASS" : $"== {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
}
