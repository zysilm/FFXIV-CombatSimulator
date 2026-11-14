// Fails when a string literal starting with an FFXIV bone prefix ("j_" or "n_", also $"..." and @"...")
// appears in the ragdoll library (AdvancedRagdoll/) or in the ragdoll core (CombatSimulator/Animation/
// RagdollController*.cs). Comments count too: quote a bone name in a comment without the quotes.
using System.Text.RegularExpressions;

var root = FindRoot(AppContext.BaseDirectory) ?? FindRoot(Directory.GetCurrentDirectory());
if (root == null)
{
    Console.WriteLine("FAIL: repository root (CombatSimulator.sln) not found");
    return 2;
}

var literal = new Regex("\"[jn]_", RegexOptions.Compiled);

var scanned = new List<string>();
foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "AdvancedRagdoll"), "*.cs", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
    if (relative.Contains("/bin/") || relative.Contains("/obj/")) continue;
    scanned.Add(relative);
}
foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "CombatSimulator", "Animation"), "RagdollController*.cs"))
    scanned.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
scanned.Sort(StringComparer.Ordinal);

var failures = 0;
foreach (var relative in scanned)
{
    var lines = File.ReadAllLines(Path.Combine(root, relative));
    for (var i = 0; i < lines.Length; i++)
    {
        if (!literal.IsMatch(lines[i])) continue;
        failures++;
        Console.WriteLine($"{relative}:{i + 1}: {lines[i].Trim()}");
    }
}

// Control: the scanner must see the literals where they belong, or a broken pattern or path would pass.
var profile = Path.Combine(root, "CombatSimulator", "Animation", "FfxivHumanoidProfile.cs");
var control = File.Exists(profile) ? File.ReadAllLines(profile).Count(l => literal.IsMatch(l)) : 0;

Console.WriteLine($"scanned {scanned.Count} files ({scanned.Count(f => f.StartsWith("AdvancedRagdoll/", StringComparison.Ordinal))} library, " +
                  $"{scanned.Count(f => f.StartsWith("CombatSimulator/", StringComparison.Ordinal))} ragdoll core); " +
                  $"control: {control} bone-name lines in FfxivHumanoidProfile.cs");
if (scanned.Count(f => f.StartsWith("CombatSimulator/", StringComparison.Ordinal)) == 0 || control == 0)
{
    Console.WriteLine("FAIL: the check did not find the files it is meant to scan");
    return 2;
}
if (failures > 0)
{
    Console.WriteLine($"FAIL: {failures} line(s) with a bone-name literal; ask the skeleton profile instead");
    return 1;
}
Console.WriteLine("PASS: no bone-name literals");
return 0;

static string? FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "CombatSimulator.sln")))
            return dir.FullName;
    return null;
}
