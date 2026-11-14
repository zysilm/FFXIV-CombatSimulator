// Fails when a string literal starting with an FFXIV bone prefix ("j_" or "n_", also $"..." and @"...") appears in the
// plugin's thin shells over the ragdoll library (CombatSimulator/Animation/RagdollController*.cs and
// WeaponDropController*.cs): bone names live in the skeleton profile (FfxivHumanoidProfile.cs). Comments count too:
// quote a bone name in a comment without the quotes.
//
// The library itself (no bone-name literals, no game or third-party names) is checked in its own repository
// (https://github.com/zysilm/AdvancedRagdoll, tests/BoneNameLiterals).
using System.Text.RegularExpressions;

var root = FindRoot(AppContext.BaseDirectory) ?? FindRoot(Directory.GetCurrentDirectory());
if (root == null)
{
    Console.WriteLine("FAIL: repository root (CombatSimulator.sln) not found");
    return 2;
}

var literal = new Regex("\"[jn]_", RegexOptions.Compiled);

var scanned = new List<string>();
foreach (var shell in new[] { "RagdollController*.cs", "WeaponDropController*.cs" })
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "CombatSimulator", "Animation"), shell))
        scanned.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
scanned.Sort(StringComparer.Ordinal);

var failures = 0;
foreach (var relative in scanned)
{
    var lines = File.ReadAllLines(Path.Combine(root, relative));
    for (var i = 0; i < lines.Length; i++)
        if (literal.IsMatch(lines[i]))
        {
            failures++;
            Console.WriteLine($"{relative}:{i + 1}: {lines[i].Trim()}");
        }
}

// Control: the scanner must see the literals where they belong, or a broken pattern or path would pass.
var profile = Path.Combine(root, "CombatSimulator", "Animation", "FfxivHumanoidProfile.cs");
var control = File.Exists(profile) ? File.ReadAllLines(profile).Count(l => literal.IsMatch(l)) : 0;

Console.WriteLine($"scanned {scanned.Count} plugin shell files; control: {control} bone-name lines in FfxivHumanoidProfile.cs");
if (scanned.Count == 0 || control == 0)
{
    Console.WriteLine("FAIL: the check did not find the files it is meant to scan");
    return 2;
}
if (failures > 0)
{
    Console.WriteLine($"FAIL: {failures} line(s) with a bone-name literal; ask the skeleton profile instead");
    return 1;
}
Console.WriteLine("PASS: no bone-name literals in the plugin's ragdoll shells");
return 0;

static string? FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "CombatSimulator.sln")))
            return dir.FullName;
    return null;
}
