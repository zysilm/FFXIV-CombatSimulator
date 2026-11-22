using System.Globalization;
using DeathShots;

// Offline check of the automatic death shots (docs/DEATH_SHOTS.md).
//   dotnet run -c Release                      unit checks, then the metric run and its gates (exit code 0 = all pass)
//   dotnet run -c Release -- stats             what the exported trajectories contain
//   dotnet run -c Release -- unit              unit checks only
//   dotnet run -c Release -- sheets [n]        contact sheets (PNG) of n recordings into %TEMP%\DeathShots\sheets
//   dotnet run -c Release -- probe <name> <profile> [0..7]   one run on one recording, printed twice a second
//   ... --traj <folder>                        trajectories from somewhere else (default %TEMP%\DeathShots\traj)
// The trajectories come from the user's ragdoll recordings:
//   Tests/RagdollReplay: dotnet run -c Release -- <ragdoll-rec folder> --export-trajectories
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var folder = Path.Combine(Path.GetTempPath(), "DeathShots", "traj");
var at = Array.IndexOf(args, "--traj");
if (at >= 0 && at + 1 < args.Length) folder = args[at + 1];
var command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "run";

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
    case "sheets":
        Sheets.Run(all, args.Length > 1 && int.TryParse(args[1], out var n) ? n : 6);
        return 0;
    default:
        failures += MetricRun.Run(all, args.Contains("--verbose"));
        Console.WriteLine(failures == 0 ? "== ALL PASS" : $"== {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
}
