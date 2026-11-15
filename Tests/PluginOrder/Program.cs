// Stage B7 check (docs/REFACTOR_PLAN.md §4, docs/PLUGIN_ARCHITECTURE.md §6): Plugin.cs was split into feature modules
// (CombatSimulator/Composition). Plugin.cs cannot run offline, so this proves mechanically, from the source, that
//   (a) the per-frame update (OnFrameworkUpdate and every plugin/module method it calls as a statement, inlined),
//   (b) the constructor (every build step it calls, inlined: object creations, calls, property and delegate
//       assignments and subscriptions, in order), and
//   (c) Dispose
// are the same sequences of the same targets before and after, the receiver being allowed to be a module now; and
//   (d) that every method and property body of the old plugin class (and its nested types) exists after the split
//       with the same tokens, wherever it lives now.
//
// Usage (from this folder):  dotnet run -c Release [-- <old-rev> [<dump-dir>]]
//   old = CombatSimulator/Plugin*.cs at <old-rev> (default 9ea6cba, the commit before the split), read with git show,
//         and CombatSimulator/Composition/*.cs when that revision has them (a revision after the split, e.g. 9d089c4,
//         to check a later stage against it);
//   new = CombatSimulator/Plugin*.cs and CombatSimulator/Composition/*.cs in the working tree.
//   Refactoring II (stage R2-P1) changed the build list on purpose; the changes are listed one by one in
//   RefactoringII at the end of this file and applied to the old sequence before (b) compares. A module's
//   "m.Config" (the whole Configuration, handed on explicitly) reads as "config" (its narrow settings view).
//   Note: against the default revision, (b) and (d) also show the armor detachment for allies and enemies
//   (e50250f, a feature after the split): OnNpcDeathRagdoll and OnCompanionDeathRagdoll strip armour on a KO,
//   and the AllyRevived handler forgets it.
//   Each comparison is made twice: parsed without defines and with DEV_EXPERIMENTAL (the private module's #if blocks).
//   <dump-dir> receives the extracted sequences (old-*.txt, new-*.txt) for reading. Exit code 0 = everything equal.
//
// How "the same target" is decided (syntax only, no semantic model): tokens are compared with trivia removed, after
//   - a module receiver is dropped and a module's accessor is read as the field it returns: corpseModule.PlayerRagdoll
//     and m.Corpse.PlayerRagdoll are "ragdollController", corpseModule.DeactivateAllNpcRagdolls() is
//     "DeactivateAllNpcRagdolls()" (the accessor table is read from the modules' "public T Name => field;" members);
//   - CombatEvents' slots are read as the engine members they back (combatEvents.SimulationReset is
//     combatEngine.OnSimulationReset; Simulation/CombatEvents.cs, CombatEngine.cs), the allies' slots (stage B9) as the
//     companion manager's members that now forward to them (combatEngine.Events.AllySpawned is
//     companionManager.OnCompanionSpawnComplete);
//   - a state of the two AI state enums merged at stage B9 (Ai.NpcAiState.Idle, CompanionAiState.Dead) is read as the
//     merged enum's member of the same name (CombatantAiState.Idle);
//   - "this." is dropped;
//   - creating an object is compared by the "new ..." expression alone (a field of the plugin that became a module
//     field, or a local that became a field, is the same creation);
//   - inside the modules, storing a parameter in the module's own field (plumbing such as "devExperimental = module;")
//     is not a step; creating the module containers (new PluginModules / new XModule) is listed separately.
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var oldRev = args.Length > 0 ? args[0] : "9ea6cba";
var dumpDir = args.Length > 1 ? args[1] : null;
var repo = FindRepoRoot();
var pluginDir = Path.Combine(repo, "CombatSimulator");

var oldFiles = GitShowPluginFiles(repo, oldRev);
var newFiles = Directory.GetFiles(pluginDir, "Plugin*.cs")
    .Concat(Directory.GetFiles(Path.Combine(pluginDir, "Composition"), "*.cs"))
    .OrderBy(f => f, StringComparer.Ordinal)
    .Select(f => (Name: Path.GetRelativePath(pluginDir, f), Text: File.ReadAllText(f)))
    .ToList();

Console.WriteLine($"old ({oldRev}): {string.Join(", ", oldFiles.Select(f => f.Name))}");
Console.WriteLine($"new (working tree): {string.Join(", ", newFiles.Select(f => f.Name))}");

var failures = 0;
foreach (var defines in new[] { Array.Empty<string>(), new[] { "DEV_EXPERIMENTAL" } })
{
    var label = defines.Length == 0 ? "no defines" : string.Join("+", defines);
    Console.WriteLine();
    Console.WriteLine($"=== parsed with {label} ===");
    var oldModel = new Model(oldFiles, defines);
    var newModel = new Model(newFiles, defines);

    failures += CompareSequence("(b) constructor", oldModel, newModel, m => m.Constructor(), label, RefactoringII.Apply);
    failures += CompareSequence("(a) per-frame update (OnFrameworkUpdate)", oldModel, newModel,
        m => m.MethodSequence(Model.PluginClass, "OnFrameworkUpdate"), label);
    failures += CompareSequence("(c) Dispose", oldModel, newModel, m => m.MethodSequence(Model.PluginClass, "Dispose"), label);
    failures += CompareBodies(oldModel, newModel);
    Console.WriteLine($"  module containers created (not steps): {string.Join(", ", newModel.ContainerCreations.Distinct())}");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL EQUAL" : $"{failures} DIFFERENCE(S)");
return failures == 0 ? 0 : 1;

int CompareSequence(string what, Model oldModel, Model newModel, Func<Model, List<string>> extract, string label,
    Func<List<string>, List<string>, int>? edit = null)
{
    var a = extract(oldModel);
    var b = extract(newModel);
    if (edit != null)
    {
        var failed = edit(a, b);
        if (failed > 0)
            return failed;
    }
    if (dumpDir != null)
    {
        Directory.CreateDirectory(dumpDir);
        var tag = new string(what.Where(char.IsLetterOrDigit).ToArray()) + "-" + new string(label.Where(char.IsLetterOrDigit).ToArray());
        File.WriteAllLines(Path.Combine(dumpDir, $"old-{tag}.txt"), a);
        File.WriteAllLines(Path.Combine(dumpDir, $"new-{tag}.txt"), b);
    }
    var counts = $"{a.Count} steps ({Count(a, "new ")} creations, {Count(a, "call ")} calls, {Count(a, "assign ")} assignments/subscriptions, " +
                 $"{a.Count(s => s.StartsWith("assign ") && s.Contains(" += "))} of them +=)";
    var first = FirstDifference(a, b);
    if (first < 0)
    {
        Console.WriteLine($"  {what}: SAME — {counts}");
        return 0;
    }
    Console.WriteLine($"  {what}: DIFFERENT at step {first + 1} (old {a.Count} steps, new {b.Count})");
    for (var i = Math.Max(0, first - 3); i < Math.Min(Math.Max(a.Count, b.Count), first + 4); i++)
    {
        Console.WriteLine($"    {(i == first ? ">" : " ")} old {i + 1,4}: {(i < a.Count ? a[i] : "<end>")}");
        Console.WriteLine($"    {(i == first ? ">" : " ")} new {i + 1,4}: {(i < b.Count ? b[i] : "<end>")}");
    }
    return 1;
}

int CompareBodies(Model oldModel, Model newModel)
{
    var same = 0;
    var diffs = new List<string>();
    var missing = new List<string>();
    var skipped = new List<string>();
    foreach (var (key, body) in oldModel.AllBodies())
    {
        var name = key[(key.LastIndexOf('.') + 1)..];
        // Split on purpose: the constructor's build steps are compared as a sequence in (b).
        if (name is "WireVictoryCelebration" or "InitVoice" or "WireWar" || key == "CombatSimulatorPlugin..ctor" ||
            RefactoringII.Bodies.Contains(key))
        {
            skipped.Add(key);
            continue;
        }
        var candidates = newModel.BodiesNamed(name).ToList();
        if (candidates.Count == 0) { missing.Add(key); continue; }
        if (candidates.Any(c => c.Body == body)) { same++; continue; }
        diffs.Add($"{key}: {candidates.Count} candidate(s), e.g. {candidates[0].Key}\n      old: {Clip(body)}\n      new: {Clip(candidates[0].Body)}");
    }
    Console.WriteLine($"  (d) method and property bodies of the old plugin class: {same} identical, {diffs.Count} different, " +
                      $"{missing.Count} missing; split on purpose and covered by (b): {string.Join(", ", skipped)}");
    foreach (var d in diffs) Console.WriteLine("    DIFFERENT " + d);
    foreach (var m in missing) Console.WriteLine("    MISSING " + m);
    return diffs.Count + missing.Count;
}

static int Count(List<string> s, string prefix) => s.Count(x => x.StartsWith(prefix));

static string Clip(string s) => s.Length <= 300 ? s : s[..300] + " ...";

static int FirstDifference(List<string> a, List<string> b)
{
    for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        if (i >= a.Count || i >= b.Count || a[i] != b[i])
            return i;
    return -1;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CombatSimulator.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("CombatSimulator.sln not found above " + AppContext.BaseDirectory);
}

static List<(string Name, string Text)> GitShowPluginFiles(string repo, string rev)
{
    var names = Git(repo, $"ls-tree --name-only {rev} CombatSimulator/")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(p => Path.GetFileName(p).StartsWith("Plugin") && p.EndsWith(".cs"))
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToList();
    var files = names.Select(p => (Name: Path.GetFileName(p), Text: Git(repo, $"show {rev}:{p}"))).ToList();
    // A revision after the split (stage B7) has the modules too: read them as the working tree's are read
    // ("Composition/<file>"), so a later stage can be checked against it.
    var modules = Git(repo, $"ls-tree --name-only {rev} CombatSimulator/Composition/")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(p => p.EndsWith(".cs"))
        .OrderBy(p => p, StringComparer.Ordinal);
    foreach (var p in modules)
        files.Add((Name: "Composition" + Path.DirectorySeparatorChar + Path.GetFileName(p), Text: Git(repo, $"show {rev}:{p}")));
    return files;
}

static string Git(string repo, string arguments)
{
    var psi = new ProcessStartInfo("git", arguments)
    {
        WorkingDirectory = repo,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
    };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"git {arguments}: {p.StandardError.ReadToEnd()}");
    return output;
}

/// <summary>The classes of one side (merged partials, nested types by their own name) and how to read them.</summary>
sealed class Model
{
    public const string PluginClass = "CombatSimulatorPlugin";

    static readonly HashSet<string> ModuleReceivers = new(StringComparer.Ordinal)
    {
        "worldModule", "actorsModule", "corpseModule", "cameraModule", "combatModule", "fightingModule", "battleModule",
        "presentationModule", "safetyDevModule", "corpse", "corpses",
        "m.World", "m.Actors", "m.Corpse", "m.Camera", "m.Combat", "m.Fighting", "m.Battle", "m.Presentation", "m.SafetyDev",
        "modules.World", "modules.Actors", "modules.Corpse", "modules.Camera", "modules.Combat", "modules.Fighting",
        "modules.Battle", "modules.Presentation", "modules.SafetyDev",
    };

    static readonly HashSet<string> EventReceivers = new(StringComparer.Ordinal)
    {
        "combatEvents", "combatModule.Events", "combatEngine.Events", "Events", "m.Combat.Events",
    };

    // Stage B9 merged the enemies' NpcAiState and the allies' CompanionAiState into one enum, CombatantAiState, with the
    // same member names (CombatSimulator/Combat/CombatantAiState.cs): a member of any of them, however qualified, is
    // read as the merged enum's member of that name.
    static readonly HashSet<string> AiStateTypes = new(StringComparer.Ordinal)
    {
        "NpcAiState", "Ai.NpcAiState", "CombatSimulator.Ai.NpcAiState",
        "CompanionAiState", "Companions.CompanionAiState", "CombatSimulator.Companions.CompanionAiState",
        "CombatantAiState", "Combat.CombatantAiState", "CombatSimulator.Combat.CombatantAiState",
    };

    static readonly Dictionary<string, string> EventSlots = new(StringComparer.Ordinal)
    {
        ["SimulationStarted"] = "combatEngine.OnSimulationStarted",
        ["SimulationReset"] = "combatEngine.OnSimulationReset",
        ["OutcomeDecided"] = "combatEngine.OnOutcomeDecided",
        ["DamageLanded"] = "combatEngine.OnDamageLanded",
        ["EnemyDied"] = "combatEngine.OnNpcDeathRagdoll",
        ["BeforePlayerDeath"] = "combatEngine.BeforePlayerDeath",
        ["PlayerDied"] = "combatEngine.OnPlayerDeath",
        ["PlayerDamaged"] = "combatEngine.OnPlayerDamagedByNpc",
        ["PlayerHitBy"] = "combatEngine.OnPlayerHitByNpc",
        // Stage B9: the allies' notifications moved from the companion manager's own delegate properties into
        // CombatEvents slots; the manager's old members are now forwarders to these slots (CombatCompanionManager.cs,
        // .Rescue.cs), so assigning the slot is assigning the old member.
        ["AllySpawned"] = "companionManager.OnCompanionSpawnComplete",
        ["AllyDied"] = "companionManager.OnCompanionDeathRagdoll",
        ["AllyRevived"] = "companionManager.OnCompanionRevived",
        ["AllySpawnError"] = "companionManager.OnSpawnError",
    };

    readonly Dictionary<string, List<TypeDeclarationSyntax>> classes = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> accessors = new(StringComparer.Ordinal);
    readonly HashSet<string> moduleClasses = new(StringComparer.Ordinal);
    public readonly List<string> ContainerCreations = new();

    public Model(IEnumerable<(string Name, string Text)> files, string[] defines)
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: defines);
        foreach (var (name, text) in files)
        {
            var root = CSharpSyntaxTree.ParseText(text, options, name).GetRoot();
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!classes.TryGetValue(type.Identifier.Text, out var list))
                    classes[type.Identifier.Text] = list = new List<TypeDeclarationSyntax>();
                list.Add(type);
                if (name.StartsWith("Composition") && type.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
                    moduleClasses.Add(type.Identifier.Text);
            }
        }

        // A module's public accessor "public T Name => field;" reads as the field.
        foreach (var module in moduleClasses)
            foreach (var decl in classes[module])
                foreach (var prop in decl.Members.OfType<PropertyDeclarationSyntax>())
                {
                    if (!prop.Modifiers.Any(SyntaxKind.PublicKeyword) || prop.ExpressionBody?.Expression is not IdentifierNameSyntax field)
                        continue;
                    var key = prop.Identifier.Text;
                    if (accessors.TryGetValue(key, out var existing) && existing != field.Identifier.Text)
                        throw new InvalidOperationException($"accessor {key} is ambiguous: {existing} / {field.Identifier.Text}");
                    accessors[key] = field.Identifier.Text;
                }
    }

    // ---- sequences ----

    public List<string> Constructor()
    {
        var ctor = classes[PluginClass].SelectMany(c => c.Members.OfType<ConstructorDeclarationSyntax>()).Single();
        var output = new List<string>();
        WalkBody(ctor.Body, ctor.ExpressionBody, PluginClass, output, new Stack<string>());
        return output;
    }

    public List<string> MethodSequence(string className, string method)
    {
        var decl = FindMethod(className, method) ?? throw new InvalidOperationException($"{className}.{method} not found");
        var output = new List<string>();
        WalkBody(decl.Body, decl.ExpressionBody, className, output, new Stack<string>(new[] { className + "." + method }));
        return output;
    }

    void WalkBody(BlockSyntax? body, ArrowExpressionClauseSyntax? arrow, string cls, List<string> o, Stack<string> stack)
    {
        if (body != null) Walk(body, cls, o, stack);
        else if (arrow != null) Expr(arrow.Expression, cls, o, stack);
    }

    void Walk(StatementSyntax s, string cls, List<string> o, Stack<string> stack)
    {
        switch (s)
        {
            case BlockSyntax b:
                foreach (var st in b.Statements) Walk(st, cls, o, stack);
                break;
            case ExpressionStatementSyntax e:
                Expr(e.Expression, cls, o, stack);
                break;
            case LocalDeclarationStatementSyntax l:
                foreach (var v in l.Declaration.Variables)
                {
                    if (v.Initializer == null) continue;
                    if (v.Initializer.Value is BaseObjectCreationExpressionSyntax creation)
                        Creation(creation, o);
                    else if (v.Initializer.Value is InvocationExpressionSyntax inv && TryInline(inv, cls, o, stack)) { }
                    else o.Add($"local {v.Identifier.Text} = {N(v.Initializer.Value)}");
                }
                break;
            case IfStatementSyntax i:
                o.Add($"if {N(i.Condition)}");
                Walk(i.Statement, cls, o, stack);
                if (i.Else != null) { o.Add("else"); Walk(i.Else.Statement, cls, o, stack); }
                o.Add("endif");
                break;
            case ForEachStatementSyntax f:
                o.Add($"foreach {f.Identifier.Text} in {N(f.Expression)}");
                Walk(f.Statement, cls, o, stack);
                o.Add("endforeach");
                break;
            case ForStatementSyntax f:
                o.Add($"for {N(f.Declaration!)} ; {N(f.Condition!)} ; {string.Join(",", f.Incrementors.Select(N))}");
                Walk(f.Statement, cls, o, stack);
                o.Add("endfor");
                break;
            case TryStatementSyntax t:
                o.Add("try");
                Walk(t.Block, cls, o, stack);
                foreach (var c in t.Catches) { o.Add($"catch {(c.Declaration == null ? "" : N(c.Declaration))}"); Walk(c.Block, cls, o, stack); }
                if (t.Finally != null) { o.Add("finally"); Walk(t.Finally.Block, cls, o, stack); }
                o.Add("endtry");
                break;
            case ReturnStatementSyntax r:
                o.Add(r.Expression == null ? "return" : $"return {N(r.Expression)}");
                break;
            case SwitchStatementSyntax sw:
                o.Add($"switch {N(sw.Expression)}");
                foreach (var section in sw.Sections)
                {
                    o.Add("case " + string.Join(" ", section.Labels.Select(N)));
                    foreach (var st in section.Statements) Walk(st, cls, o, stack);
                }
                o.Add("endswitch");
                break;
            default:
                o.Add("stmt " + N(s));
                break;
        }
    }

    void Expr(ExpressionSyntax e, string cls, List<string> o, Stack<string> stack)
    {
        switch (e)
        {
            case InvocationExpressionSyntax inv:
                if (!TryInline(inv, cls, o, stack))
                    o.Add("call " + N(inv));
                break;
            case BaseObjectCreationExpressionSyntax creation:
                Creation(creation, o);
                break;
            case AssignmentExpressionSyntax a:
                var simpleTarget = a.Left is IdentifierNameSyntax ||
                                   a.Left is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
                if (simpleTarget && a.Right is BaseObjectCreationExpressionSyntax created)
                {
                    Creation(created, o);
                    break;
                }
                // Plumbing inside a module: keeping a parameter in the module's own field.
                if (simpleTarget && moduleClasses.Contains(cls) && a.Right is IdentifierNameSyntax)
                    break;
                o.Add($"assign {N(a.Left)} {a.OperatorToken.Text} {N(a.Right)}");
                break;
            default:
                o.Add("expr " + N(e));
                break;
        }
    }

    void Creation(BaseObjectCreationExpressionSyntax creation, List<string> o)
    {
        if (creation is ObjectCreationExpressionSyntax oc)
        {
            var typeName = oc.Type.ToString().Split('.').Last();
            if (typeName == "PluginModules" || moduleClasses.Contains(typeName))
            {
                ContainerCreations.Add(typeName);
                return;
            }
        }
        o.Add(N(creation));
    }

    bool TryInline(InvocationExpressionSyntax inv, string cls, List<string> o, Stack<string> stack)
    {
        var target = ResolveTarget(inv.Expression, cls);
        if (target == null) return false;
        var (targetClass, method) = target.Value;
        var key = targetClass + "." + method.Identifier.Text;
        if (stack.Contains(key) || stack.Count > 12) return false;
        stack.Push(key);
        WalkBody(method.Body, method.ExpressionBody, targetClass, o, stack);
        stack.Pop();
        return true;
    }

    (string Class, MethodDeclarationSyntax Method)? ResolveTarget(ExpressionSyntax callee, string cls)
    {
        string? targetClass = null;
        string? name = null;
        switch (callee)
        {
            case IdentifierNameSyntax id:
                targetClass = cls;
                name = id.Identifier.Text;
                break;
            case MemberAccessExpressionSyntax ma:
                name = ma.Name.Identifier.Text;
                targetClass = ma.Expression switch
                {
                    ThisExpressionSyntax => cls,
                    IdentifierNameSyntax rid => TypeOfMember(cls, rid.Identifier.Text),
                    MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax holder } inner
                        when holder.Identifier.Text is "m" or "modules" => TypeOfMember("PluginModules", inner.Name.Identifier.Text),
                    _ => null,
                };
                break;
        }
        if (targetClass == null || name == null) return null;
        var decl = FindMethod(targetClass, name);
        return decl == null ? null : (targetClass, decl);
    }

    string? TypeOfMember(string cls, string member)
    {
        if (!classes.TryGetValue(cls, out var decls)) return null;
        foreach (var d in decls)
            foreach (var m in d.Members)
            {
                TypeSyntax? type = m switch
                {
                    PropertyDeclarationSyntax p when p.Identifier.Text == member => p.Type,
                    FieldDeclarationSyntax f when f.Declaration.Variables.Any(v => v.Identifier.Text == member) => f.Declaration.Type,
                    _ => null,
                };
                if (type == null) continue;
                var typeName = type.ToString().TrimEnd('?').Split('.').Last();
                if (typeName == "ICorpseQuery") typeName = "CorpseModule";
                return classes.ContainsKey(typeName) ? typeName : null;
            }
        return null;
    }

    MethodDeclarationSyntax? FindMethod(string cls, string name)
    {
        if (!classes.TryGetValue(cls, out var decls)) return null;
        return decls.SelectMany(d => d.Members.OfType<MethodDeclarationSyntax>())
            .FirstOrDefault(m => m.Identifier.Text == name && (m.Body != null || m.ExpressionBody != null));
    }

    // ---- bodies ----

    public IEnumerable<(string Key, string Body)> AllBodies()
    {
        foreach (var (cls, decls) in classes)
            foreach (var d in decls)
                foreach (var m in d.Members)
                {
                    var key = cls + "." + m switch
                    {
                        MethodDeclarationSyntax md => md.Identifier.Text,
                        PropertyDeclarationSyntax pd => pd.Identifier.Text,
                        ConstructorDeclarationSyntax => ".ctor",
                        _ => null,
                    };
                    if (key.EndsWith(".")) continue;
                    SyntaxNode? body = m switch
                    {
                        MethodDeclarationSyntax md => (SyntaxNode?)md.Body ?? md.ExpressionBody,
                        PropertyDeclarationSyntax pd => (SyntaxNode?)pd.ExpressionBody ?? pd.AccessorList,
                        ConstructorDeclarationSyntax cd => (SyntaxNode?)cd.Body ?? cd.ExpressionBody,
                        _ => null,
                    };
                    if (body == null) continue;
                    if (m is PropertyDeclarationSyntax { AccessorList: { } al } && al.Accessors.All(a => a.Body == null && a.ExpressionBody == null))
                        continue; // auto-property
                    yield return (key, N(body));
                }
    }

    public IEnumerable<(string Key, string Body)> BodiesNamed(string name)
        => AllBodies().Where(b => b.Key[(b.Key.LastIndexOf('.') + 1)..] == name);

    // ---- normalisation ----

    string N(SyntaxNode node)
    {
        var rewritten = new Normalizer(this).Visit(node);
        return string.Join(" ", rewritten.DescendantTokens().Select(t => t.Text));
    }

    static string Plain(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(t => t.Text));

    sealed class Normalizer(Model model) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var receiver = Plain(node.Expression);
            var name = node.Name.Identifier.Text;
            if (node.Expression is ThisExpressionSyntax)
                return Visit(node.Name);
            // Refactoring II (settings views): a module reads its own settings through a narrow view still named
            // "config", and hands the whole Configuration (PluginModules.Config) explicitly to what still takes it
            // or writes it. Both are the plugin's one Configuration object.
            if (receiver is "m" or "modules" && name == "Config")
                return SyntaxFactory.IdentifierName("config");
            if (ModuleReceivers.Contains(receiver))
                return SyntaxFactory.IdentifierName(model.accessors.TryGetValue(name, out var field) ? field : name);
            if (EventReceivers.Contains(receiver) && EventSlots.TryGetValue(name, out var slot))
                return SyntaxFactory.ParseExpression(slot);
            if (AiStateTypes.Contains(receiver))
                return SyntaxFactory.ParseExpression("CombatantAiState." + name);
            return base.VisitMemberAccessExpression(node);
        }
    }
}

/// <summary>
/// Refactoring II, stage R2-P1 (docs/REFACTOR_PLAN.md §4.9 items 5 and 6): the engine is given the roster of the
/// player's side, who fights whom and the battle's rule parts by constructor instead of the seven queries and the
/// rule parts it was handed after the allies' manager was built; the manager is given the same three. These are the
/// only changes to the build list, each listed here and each required to apply exactly once to the old sequence (so
/// nothing else can hide behind them): the three objects are created before the engine (the roster with the
/// enemies' selector and spawner, the other two right before the engine), the engine and the manager are created
/// with them, and the seven assignments are gone (CombatModule.WireCompanions keeps ResolveNpcTarget). Applied only
/// when the new side has that shape and the old side has not.
/// </summary>
static class RefactoringII
{
    const string Marker = "new CombatRoster ( config )";

    static readonly (string Old, string[] New)[] Edits =
    {
        ("new Effects . NpcScaleController ( config , npcSelector , objectTable )",
            new[] { "new Effects . NpcScaleController ( config , npcSelector , objectTable )", Marker }),
        ("new CombatEngine ( actionDataProvider , damageCalculator , animationController , movementBlockHook , ragdollController , config , npcSelector , clientState , log )",
            new[]
            {
                "new TargetingService ( ( ) => combatEngine . State . SimulationTime )",
                "new BattleRuleSet ( BattleRuleFactory . CreateAll ( ) )",
                "new CombatEngine ( actionDataProvider , damageCalculator , animationController , movementBlockHook , ragdollController , config , npcSelector , combatRoster , targeting , ruleParts , clientState , log )",
            }),
        ("new CombatCompanionManager ( objectTable , clientState , config , combatEngine , animationController , movementBlockHook , targetManager , log )",
            new[] { "new CombatCompanionManager ( objectTable , clientState , config , combatEngine , combatRoster , targeting , ruleParts , animationController , movementBlockHook , targetManager , log )" }),
        ("assign combatEngine . ResolveExternalEntityAddress = companionManager . ResolveAddress", Array.Empty<string>()),
        ("assign combatEngine . HasLivingCompanions = ( ) => companionManager . HasLivingCompanions", Array.Empty<string>()),
        ("assign combatEngine . OnPlayerDamageDealt = companionManager . RegisterPlayerDamage", Array.Empty<string>()),
        ("assign combatEngine . OnPlayerDamageDealtToTarget = companionManager . RegisterPlayerDamage", Array.Empty<string>()),
        ("assign combatEngine . ForceNpcTarget = companionManager . ForceEnemyTarget", Array.Empty<string>()),
        ("assign combatEngine . OnHit = companionManager . RegisterHit", Array.Empty<string>()),
        ("assign combatEngine . RuleParts = companionManager . RuleParts", Array.Empty<string>()),
    };

    /// <summary>The module build steps whose bodies Refactoring II changed on purpose (their effect on the build list is (b)'s).</summary>
    public static readonly HashSet<string> Bodies = new(StringComparer.Ordinal)
    {
        "ActorsModule.CreateRoster", "ActorsModule.CreateCompanions",
        "CombatModule.CreateEngineAndActionCombat", "CombatModule.WireCompanions",
    };

    /// <summary>Applies the listed edits to the old sequence; returns how many did not apply exactly once.</summary>
    public static int Apply(List<string> oldSteps, List<string> newSteps)
    {
        if (!newSteps.Contains(Marker) || oldSteps.Contains(Marker))
            return 0;
        var failures = 0;
        foreach (var (old, replacement) in Edits)
        {
            var at = oldSteps.IndexOf(old);
            if (at < 0 || oldSteps.LastIndexOf(old) != at)
            {
                Console.WriteLine($"  (b) Refactoring II edit does not apply exactly once to the old sequence: {old}");
                failures++;
                continue;
            }
            oldSteps.RemoveAt(at);
            oldSteps.InsertRange(at, replacement);
        }
        Console.WriteLine($"  (b) Refactoring II: {Edits.Length - failures}/{Edits.Length} listed edits applied to the old sequence " +
                          "(3 objects created before the engine, the engine and the allies' manager created with them, 7 assignments gone)");
        return failures;
    }
}
