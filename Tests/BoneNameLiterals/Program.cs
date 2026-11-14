// Fails when a string literal starting with an FFXIV bone prefix ("j_" or "n_", also $"..." and @"...")
// appears in the ragdoll library (AdvancedRagdoll/, which holds the ragdoll core Ragdoll*.cs since stage A4) or in
// the plugin's thin shells over it (CombatSimulator/Animation/RagdollController*.cs, and WeaponDropController*.cs since
// stage A5). Comments count too: quote a
// bone name in a comment without the quotes.
//
// Stage A7 (the library made publishable) adds three checks on the library sources only (AdvancedRagdoll/):
// - the game's render-object word "DrawObject" nowhere (code, comments, strings);
// - no log prefix of the plugin's classes ("RagdollController:", "WeaponDropController:"): the library logs "Ragdoll:" and
//   "WeaponDrop:";
// - no game bone name (j_... or n_..., quoted or not) inside a string literal, so log texts never name FFXIV bones.
using System.Text.RegularExpressions;

var root = FindRoot(AppContext.BaseDirectory) ?? FindRoot(Directory.GetCurrentDirectory());
if (root == null)
{
    Console.WriteLine("FAIL: repository root (CombatSimulator.sln) not found");
    return 2;
}

var literal = new Regex("\"[jn]_", RegexOptions.Compiled);
// Library-only checks (stage A7).
var hostWords = new Regex("DrawObject|RagdollController:|WeaponDropController:", RegexOptions.Compiled);
var stringLiteral = new Regex(@"\$?@?""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);
var boneToken = new Regex(@"(?<![A-Za-z0-9])[jn]_[a-z]", RegexOptions.Compiled);

var scanned = new List<string>();
foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "AdvancedRagdoll"), "*.cs", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
    if (relative.Contains("/bin/") || relative.Contains("/obj/")) continue;
    scanned.Add(relative);
}
foreach (var shell in new[] { "RagdollController*.cs", "WeaponDropController*.cs" })
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "CombatSimulator", "Animation"), shell))
        scanned.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
scanned.Sort(StringComparer.Ordinal);

var failures = 0;
var hostFailures = 0;
foreach (var relative in scanned)
{
    var library = relative.StartsWith("AdvancedRagdoll/", StringComparison.Ordinal);
    var lines = File.ReadAllLines(Path.Combine(root, relative));
    for (var i = 0; i < lines.Length; i++)
    {
        if (literal.IsMatch(lines[i]))
        {
            failures++;
            Console.WriteLine($"{relative}:{i + 1}: {lines[i].Trim()}");
        }
        if (library && (hostWords.IsMatch(lines[i]) || BoneNameInString(lines[i])))
        {
            hostFailures++;
            Console.WriteLine($"{relative}:{i + 1}: (game or plugin word) {lines[i].Trim()}");
        }
    }
}

bool BoneNameInString(string line)
{
    foreach (Match m in stringLiteral.Matches(line))
        if (boneToken.IsMatch(m.Value)) return true;
    return false;
}

// Control: the scanner must see the literals where they belong, or a broken pattern or path would pass.
var profile = Path.Combine(root, "CombatSimulator", "Animation", "FfxivHumanoidProfile.cs");
var control = File.Exists(profile) ? File.ReadAllLines(profile).Count(l => literal.IsMatch(l)) : 0;
// Controls of the stage A7 checks: lines they must flag, and lines they must let through.
var hostControls = new[]
{
    ("log.Info($\"RagdollController: Activated\");", true),
    ("log.Info(\"WeaponDropController: removed all weapons\");", true),
    ("// moves the corpse's DrawObject", true),
    ("log.Info($\"Ragdoll: n_hara index={i}\");", true),
    ("log.Info($\"[LegDiag] one of j_asi_a/b did not resolve\");", true),
    ("log.Info($\"Ragdoll: belly-follow bone index={i}, head bone index={j}\");", false),
    ("var name = \"gen_\" + i;", false),
    ("// the head bone (in the game skeleton j_kao) follows", false),
};
var hostControlFailures = hostControls.Count(c => (hostWords.IsMatch(c.Item1) || BoneNameInString(c.Item1)) != c.Item2);

var coreFiles = scanned.Count(f => f.StartsWith("AdvancedRagdoll/Ragdoll.", StringComparison.Ordinal));
Console.WriteLine($"scanned {scanned.Count} files ({scanned.Count(f => f.StartsWith("AdvancedRagdoll/", StringComparison.Ordinal))} library, " +
                  $"of which {coreFiles} ragdoll core; {scanned.Count(f => f.StartsWith("CombatSimulator/", StringComparison.Ordinal))} plugin shell); " +
                  $"control: {control} bone-name lines in FfxivHumanoidProfile.cs");
if (coreFiles == 0 || scanned.Count(f => f.StartsWith("CombatSimulator/", StringComparison.Ordinal)) == 0 || control == 0)
{
    Console.WriteLine("FAIL: the check did not find the files it is meant to scan");
    return 2;
}
if (hostControlFailures > 0)
{
    Console.WriteLine($"FAIL: {hostControlFailures} control line(s) of the game/plugin word check answered wrongly");
    return 2;
}
if (failures > 0 || hostFailures > 0)
{
    if (failures > 0)
        Console.WriteLine($"FAIL: {failures} line(s) with a bone-name literal; ask the skeleton profile instead");
    if (hostFailures > 0)
        Console.WriteLine($"FAIL: {hostFailures} library line(s) with \"DrawObject\", a plugin log prefix or a game bone name in a string");
    return 1;
}
Console.WriteLine("PASS: no bone-name literals; no game or plugin words in the library");
return 0;

static string? FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "CombatSimulator.sln")))
            return dir.FullName;
    return null;
}
