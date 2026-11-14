// Stage A3c check (docs/RAGDOLL_LIBRARY_ARCHITECTURE.md §9 suggestion 3): RagdollController.cs was cut into partial
// files of the same class. This tool proves that nothing but the file a member lives in changed.
//
//   list    <file.cs>                         members of the class with their line ranges (used to plan the cut)
//   split   <file.cs> <plan.txt> <outDir>     cut the members named in the plan into partial files, verbatim
//                                             (a3c-plan.txt is the plan used for A3c, against RagdollController.cs at 8605a57)
//   members <oldDir> <newDir>                 member declarations of RagdollController*.cs: old multiset == new multiset,
//                                             compared on the full text (comments included) and on the tokens alone,
//                                             parsed without defines, with DEV_EXPERIMENTAL and with PUBLIC_BUILD
//   usings  <refs.txt> <projectDir> <file>... [--apply]  using directives a file does not need (semantic model; a
//                                             directive counts as unused only if unused with and without DEV_EXPERIMENTAL).
//                                             refs.txt: one reference path per line, e.g. from
//                                             dotnet msbuild ../../CombatSimulator -t:ResolveAssemblyReferences -getItem:ReferencePath -p:Configuration=Release
//   il      <old.dll> <new.dll>               every method body of the assembly, metadata tokens resolved to names and the
//                                             member ordinals in compiler-generated names (<M>b__12_0, DisplayClass12_0,
//                                             <M>d__12, <>9__12_0) replaced by '#': old multiset == new multiset; also the
//                                             fields, the types and RagdollController's .ctor/.cctor separately
//
// Typical run (old = the RagdollController*.cs files of the commit before the cut, from git show; the DLLs are a build
// of each, with -o into a temporary directory, never into CombatSimulator/bin/Release):
//   dotnet run -c Release -- members <oldDir> ../../CombatSimulator/Animation
//   dotnet run -c Release -- il <old>/CombatSimulator.dll <new>/CombatSimulator.dll
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

const string ClassName = "RagdollController";

if (args.Length == 0) { Console.Error.WriteLine("usage: list | split | members | usings | il (see Program.cs)"); return 2; }
switch (args[0])
{
    case "list": return List(args[1]);
    case "split": return Split(args[1], args[2], args[3]);
    case "members": return Members(args[1], args[2]);
    case "usings": return Usings(args[1], args[2], args.Skip(3).ToArray());
    case "il": return IlCompare(args[1], args[2]);
    default: Console.Error.WriteLine("unknown mode " + args[0]); return 2;
}

static CSharpParseOptions ParseOptions(params string[] defines) =>
    new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: defines);

static ClassDeclarationSyntax MainClass(SyntaxTree tree) =>
    tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == ClassName && c.Parent is not TypeDeclarationSyntax);

static string Describe(MemberDeclarationSyntax m) => m switch
{
    MethodDeclarationSyntax x => "method " + x.Identifier.Text + "(" + x.ParameterList.Parameters.Count + ")",
    FieldDeclarationSyntax x => (x.Modifiers.Any(SyntaxKind.ConstKeyword) ? "const " : x.Modifiers.Any(SyntaxKind.StaticKeyword) ? "sfield " : "field ")
                                + string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text))
                                + (x.Declaration.Variables.Any(v => v.Initializer != null) ? " =" : ""),
    PropertyDeclarationSyntax x => "prop " + x.Identifier.Text + (x.Initializer != null ? " =" : ""),
    EventFieldDeclarationSyntax x => "event " + string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text)),
    ConstructorDeclarationSyntax x => "ctor(" + x.ParameterList.Parameters.Count + ")",
    BaseTypeDeclarationSyntax x => "type " + x.Identifier.Text,
    DelegateDeclarationSyntax x => "delegate " + x.Identifier.Text,
    IndexerDeclarationSyntax => "indexer",
    OperatorDeclarationSyntax x => "operator " + x.OperatorToken.Text,
    DestructorDeclarationSyntax => "dtor",
    _ => m.Kind().ToString(),
};

static int List(string file)
{
    var text = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(text, ParseOptions());
    var cls = MainClass(tree);
    var lines = tree.GetText().Lines;
    for (int i = 0; i < cls.Members.Count; i++)
    {
        var m = cls.Members[i];
        int a = lines.GetLineFromPosition(m.FullSpan.Start).LineNumber + 1;
        int s = lines.GetLineFromPosition(m.Span.Start).LineNumber + 1;
        int b = lines.GetLineFromPosition(m.FullSpan.End - 1).LineNumber + 1;
        Console.WriteLine($"{i,4} {a,6} {s,6} {b,6} {b - a + 1,5}  {Describe(m)}");
    }
    return 0;
}

// plan.txt: "<firstIndex>-<lastIndex> <Suffix>" or "<index> <Suffix>" per line (# comments). Members not named stay in the main file.
// Each new file gets the main file's license header, its using directives (trimmed later with `usings`), the namespace and
// the class declaration line, then the members' full text (leading comments included) in their original order.
static int Split(string file, string planFile, string outDir)
{
    var text = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(text, ParseOptions());
    var root = (CompilationUnitSyntax)tree.GetRoot();
    var cls = MainClass(tree);
    var target = new string?[cls.Members.Count];
    foreach (var raw in File.ReadAllLines(planFile))
    {
        var line = raw.Split('#')[0].Trim();
        if (line.Length == 0) continue;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var range = parts[0].Split('-');
        int a = int.Parse(range[0]), b = range.Length > 1 ? int.Parse(range[1]) : a;
        for (int i = a; i <= b; i++)
        {
            if (target[i] != null) throw new Exception($"member {i} planned twice");
            target[i] = parts[1];
        }
    }
    string header = text[..root.Usings.First().FullSpan.Start];
    string usings = text[root.Usings.First().FullSpan.Start..root.Usings.Last().FullSpan.End];
    string decl = "\nnamespace CombatSimulator.Animation;\n\npublic unsafe partial class RagdollController\n{\n";
    var files = new Dictionary<string, StringBuilder>();
    var main = new StringBuilder();
    int cursor = 0;
    for (int i = 0; i < cls.Members.Count; i++)
    {
        var m = cls.Members[i];
        if (target[i] == null) continue;
        main.Append(text, cursor, m.FullSpan.Start - cursor);
        cursor = m.FullSpan.End;
        if (!files.TryGetValue(target[i]!, out var sb))
            files[target[i]!] = sb = new StringBuilder(header + usings + decl);
        var body = text.Substring(m.FullSpan.Start, m.FullSpan.Length);
        // A member that followed its predecessor without a blank line gets one when it starts a run in the new file.
        if (sb.Length > 0 && i > 0 && target[i - 1] != target[i] && !body.StartsWith("\n") && !sb.ToString().EndsWith("{\n"))
            sb.Append('\n');
        // The first member of a file must not start with a blank line right after the opening brace.
        if (sb.ToString().EndsWith("{\n")) body = body.TrimStart('\n');
        sb.Append(body);
    }
    main.Append(text, cursor, text.Length - cursor);
    Directory.CreateDirectory(outDir);
    File.WriteAllText(Path.Combine(outDir, Path.GetFileName(file)), main.ToString());
    foreach (var (suffix, sb) in files)
    {
        sb.Append("}\n");
        File.WriteAllText(Path.Combine(outDir, $"{ClassName}.{suffix}.cs"), sb.ToString());
    }
    Console.WriteLine($"main + {files.Count} files written to {outDir}");
    return 0;
}

// --- members: the declarations of every RagdollController*.cs, old vs new ---

static IEnumerable<(string Type, MemberDeclarationSyntax Member)> TopMembers(SyntaxTree tree)
{
    foreach (var t in ((CompilationUnitSyntax)tree.GetRoot()).DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Parent is not TypeDeclarationSyntax))
        foreach (var m in t.Members)
            yield return (t.Identifier.Text, m);
}

static string Strict(MemberDeclarationSyntax m) => m.ToFullString().Replace("\r\n", "\n").Trim('\n');
static string Code(MemberDeclarationSyntax m) => string.Join(" ", m.DescendantTokens().Select(t => t.Text));

static int Members(string oldDir, string newDir)
{
    int fail = 0;
    foreach (var defines in new[] { Array.Empty<string>(), new[] { "DEV_EXPERIMENTAL" }, new[] { "PUBLIC_BUILD" } })
    {
        var opts = ParseOptions(defines);
        List<(string File, string Type, MemberDeclarationSyntax M)> Load(string dir) =>
            Directory.GetFiles(dir, ClassName + "*.cs").OrderBy(f => f, StringComparer.Ordinal)
                .SelectMany(f => TopMembers(CSharpSyntaxTree.ParseText(File.ReadAllText(f), opts)).Select(x => (Path.GetFileName(f), x.Type, x.Member)))
                .ToList();
        var oldM = Load(oldDir);
        var newM = Load(newDir);
        string label = defines.Length == 0 ? "(no defines)" : string.Join(",", defines);
        foreach (var (name, key) in new (string, Func<MemberDeclarationSyntax, string>)[] { ("strict (full text incl. comments)", Strict), ("code (tokens only)", Code) })
        {
            var a = oldM.Select(x => x.Type + "\u0001" + key(x.M)).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
            var b = newM.Select(x => x.Type + "\u0001" + key(x.M)).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
            var missing = a.Where(kv => !b.TryGetValue(kv.Key, out var n) || n < kv.Value).ToList();
            var extra = b.Where(kv => !a.TryGetValue(kv.Key, out var n) || n < kv.Value).ToList();
            bool ok = missing.Count == 0 && extra.Count == 0 && oldM.Count == newM.Count;
            if (!ok) fail++;
            Console.WriteLine($"{label,-18} {name,-36} old {oldM.Count} members in {oldM.Select(x => x.File).Distinct().Count()} files, new {newM.Count} in {newM.Select(x => x.File).Distinct().Count()} files: {(ok ? "IDENTICAL multiset" : $"DIFFERENT ({missing.Count} missing, {extra.Count} extra)")}");
            foreach (var kv in missing.Take(5)) Console.WriteLine("   missing: " + kv.Key[..Math.Min(160, kv.Key.Length)].Replace("\n", "\\n"));
            foreach (var kv in extra.Take(5)) Console.WriteLine("   extra:   " + kv.Key[..Math.Min(160, kv.Key.Length)].Replace("\n", "\\n"));
        }
        if (defines.Length == 0)
        {
            // Where the members of the old main file went.
            var moved = newM.GroupBy(x => x.File).OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in moved) Console.WriteLine($"   {g.Key,-48} {g.Count(),4} members");
            // Type declaration headers (modifiers, base list) of every part.
            static IEnumerable<string> Heads(string dir) => Directory.GetFiles(dir, ClassName + "*.cs").SelectMany(f =>
                ((CompilationUnitSyntax)CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot()).DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Where(t => t.Parent is not TypeDeclarationSyntax)
                .Select(t => $"{string.Join(" ", t.Modifiers.Select(m => m.Text))} {t.Keyword.Text} {t.Identifier.Text}{t.BaseList?.ToString().Trim().Insert(0, " ")}"))
                .Distinct().OrderBy(s => s);
            Console.WriteLine("   type headers old: " + string.Join(" | ", Heads(oldDir)));
            Console.WriteLine("   type headers new: " + string.Join(" | ", Heads(newDir)));
        }
    }
    Console.WriteLine(fail == 0 ? "MEMBERS: PASS" : "MEMBERS: FAIL");
    return fail == 0 ? 0 : 1;
}

// --- usings: which using directives each listed file does not need ---

static int Usings(string refsFile, string projectDir, string[] args)
{
    bool apply = args.Contains("--apply");
    var files = args.Where(a => a != "--apply").Select(Path.GetFullPath).ToArray();
    var refs = File.ReadAllLines(refsFile).Where(l => l.Trim().Length > 0).Select(l => MetadataReference.CreateFromFile(l.Trim())).ToList();
    var sources = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                 && !f.Contains(Path.DirectorySeparatorChar + "Tests" + Path.DirectorySeparatorChar)
                 && !f.Contains(Path.Combine("Dev", "Experimental")))
        .Select(Path.GetFullPath).ToList();
    // A using counts as unused only if it is unused both without and with DEV_EXPERIMENTAL (the private module's own
    // sources are not compiled here; only the #if DEV_EXPERIMENTAL blocks inside these files matter for their usings).
    var unusedPerFile = files.ToDictionary(f => f, f => (HashSet<string>?)null, StringComparer.OrdinalIgnoreCase);
    foreach (var defines in new[] { new[] { "PUBLIC_BUILD" }, new[] { "DEV_EXPERIMENTAL" } })
    {
        var opts = ParseOptions(defines);
        var trees = sources.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), opts, path: f)).ToList();
        var comp = CSharpCompilation.Create("CombatSimulator", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.WriteLine($"compilation {string.Join(",", defines)}: {trees.Count} files, {errs.Count} errors"
            + $" ({errs.Count(e => files.Contains(e.Location.SourceTree?.FilePath ?? "", StringComparer.OrdinalIgnoreCase))} in the listed files)");
        foreach (var f in files)
        {
            var tree = trees.First(t => string.Equals(t.FilePath, f, StringComparison.OrdinalIgnoreCase));
            var unused = comp.GetSemanticModel(tree).GetDiagnostics().Where(d => d.Id == "CS8019")
                .Select(d => tree.GetRoot().FindNode(d.Location.SourceSpan).ToString().Trim()).ToHashSet();
            if (unusedPerFile[f] == null) unusedPerFile[f] = unused; else unusedPerFile[f]!.IntersectWith(unused);
        }
    }
    foreach (var f in files)
    {
        var unused = unusedPerFile[f]!;
        Console.WriteLine($"{Path.GetFileName(f)}: unused {unused.Count}: {string.Join("  ", unused.OrderBy(s => s))}");
        if (apply && unused.Count > 0)
        {
            var lines = File.ReadAllText(f).Split('\n').ToList();
            int before = lines.Count;
            lines.RemoveAll(l => unused.Contains(l.Trim()));
            if (before - lines.Count != unused.Count) throw new Exception("using lines not found one per directive in " + f);
            File.WriteAllText(f, string.Join("\n", lines));
        }
    }
    return 0;
}

// --- il: every method body of the assembly, with metadata tokens resolved to names ---

static int IlCompare(string oldDll, string newDll)
{
    var a = Dump(oldDll);
    var b = Dump(newDll);
    int fail = 0;
    foreach (var section in new[] { "method", "field", "type" })
    {
        var x = a.Where(e => e.Kind == section).ToList();
        var y = b.Where(e => e.Kind == section).ToList();
        var xs = x.Select(e => e.Key + "\n" + e.Body).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var ys = y.Select(e => e.Key + "\n" + e.Body).OrderBy(s => s, StringComparer.Ordinal).ToList();
        bool same = xs.SequenceEqual(ys);
        Console.WriteLine($"{section,-7}: old {x.Count}, new {y.Count}: {(same ? "IDENTICAL" : "DIFFERENT")}");
        if (!same)
        {
            fail++;
            var xk = x.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Select(e => e.Body).OrderBy(s => s, StringComparer.Ordinal).ToList());
            var yk = y.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Select(e => e.Body).OrderBy(s => s, StringComparer.Ordinal).ToList());
            foreach (var k in xk.Keys.Union(yk.Keys).OrderBy(s => s, StringComparer.Ordinal))
            {
                xk.TryGetValue(k, out var l1); yk.TryGetValue(k, out var l2);
                if (l1 != null && l2 != null && l1.SequenceEqual(l2)) continue;
                Console.WriteLine($"   differs: {k} (old {l1?.Count ?? 0}, new {l2?.Count ?? 0})");
                if (l1 is { Count: 1 } && l2 is { Count: 1 })
                {
                    var o = l1[0].Split('\n'); var n = l2[0].Split('\n');
                    int d = 0; while (d < o.Length && d < n.Length && o[d] == n[d]) d++;
                    Console.WriteLine($"      first difference at line {d}: old '{(d < o.Length ? o[d] : "<end>")}' new '{(d < n.Length ? n[d] : "<end>")}'");
                }
            }
        }
    }
    // Declaration order of the controller's fields (metadata order): reported, not required.
    var fo = a.Where(e => e.Kind == "fieldorder").Select(e => e.Body).FirstOrDefault();
    var fn = b.Where(e => e.Kind == "fieldorder").Select(e => e.Body).FirstOrDefault();
    Console.WriteLine($"RagdollController field order: {(fo == fn ? "same" : "different (fields moved between files)")}");
    const string Ctl = "CombatSimulator.Animation.RagdollController";
    bool InCtl(string k) => k.StartsWith(Ctl + "::") || k.StartsWith(Ctl + "/");
    var ctlOld = a.Where(e => e.Kind == "method" && InCtl(e.Key)).Select(e => e.Key + "\n" + e.Body).OrderBy(s => s, StringComparer.Ordinal).ToList();
    var ctlNew = b.Where(e => e.Kind == "method" && InCtl(e.Key)).Select(e => e.Key + "\n" + e.Body).OrderBy(s => s, StringComparer.Ordinal).ToList();
    Console.WriteLine($"  of which RagdollController and its nested/generated types: old {ctlOld.Count}, new {ctlNew.Count} methods: {(ctlOld.SequenceEqual(ctlNew) ? "IDENTICAL" : "DIFFERENT")}");
    foreach (var special in new[] { "::.ctor(", "::.cctor(" })
    {
        var o = a.Where(e => e.Kind == "method" && e.Key.StartsWith(Ctl + special)).Select(e => e.Body).OrderBy(s => s, StringComparer.Ordinal);
        var n = b.Where(e => e.Kind == "method" && e.Key.StartsWith(Ctl + special)).Select(e => e.Body).OrderBy(s => s, StringComparer.Ordinal);
        Console.WriteLine($"  RagdollController{special.TrimEnd('(')}: {o.Count()} bodies, {(o.SequenceEqual(n) ? "IDENTICAL (field initializers run in the same order)" : "DIFFERENT")}");
    }
    Console.WriteLine(fail == 0 ? "IL: PASS" : "IL: FAIL");
    return fail == 0 ? 0 : 1;
}

static List<(string Kind, string Key, string Body)> Dump(string dll)
{
    using var pe = new PEReader(File.OpenRead(dll));
    var md = pe.GetMetadataReader();
    var names = new Names(md);
    var result = new List<(string, string, string)>();
    foreach (var th in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(th);
        string tname = names.Type(th);
        var fields = td.GetFields().Select(fh => md.GetFieldDefinition(fh)).ToList();
        result.Add(("type", tname, $"{td.Attributes} base={(td.BaseType.IsNil ? "" : names.Token(MetadataTokens.GetToken(td.BaseType)))} "
            + string.Join(",", td.GetInterfaceImplementations().Select(i => names.Token(MetadataTokens.GetToken(md.GetInterfaceImplementation(i).Interface))).OrderBy(s => s))
            + $" layout={td.GetLayout().Size}/{td.GetLayout().PackingSize} nested={td.GetNestedTypes().Length} methods={td.GetMethods().Count} fields={fields.Count}"
            + " attrs=" + string.Join(",", td.GetCustomAttributes().Select(c => names.Attr(c)).OrderBy(s => s))));
        foreach (var fh in td.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            result.Add(("field", tname + "::" + Names.Norm(md.GetString(f.Name)), $"{f.Attributes} {f.DecodeSignature(names, null)} off={f.GetOffset()} rva={(f.GetRelativeVirtualAddress() != 0 ? "data" : "")}"
                + " attrs=" + string.Join(",", f.GetCustomAttributes().Select(c => names.Attr(c)).OrderBy(s => s))));
        }
        if (tname.EndsWith(ClassName))
            result.Add(("fieldorder", tname, string.Join(",", fields.Select(f => md.GetString(f.Name)))));
        foreach (var mh in td.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            string key = tname + "::" + names.MethodSig(md.GetString(m.Name), m.Signature);
            var sb = new StringBuilder();
            sb.Append($"{m.Attributes} {m.ImplAttributes} attrs=" + string.Join(",", m.GetCustomAttributes().Select(c => names.Attr(c)).OrderBy(s => s)));
            sb.Append(" params=" + string.Join(",", m.GetParameters().Select(p => { var pd = md.GetParameter(p); return md.GetString(pd.Name) + ":" + pd.Attributes; })));
            if (m.RelativeVirtualAddress != 0)
            {
                var body = pe.GetMethodBody(m.RelativeVirtualAddress);
                sb.Append($"\nmaxstack={body.MaxStack} initlocals={body.LocalVariablesInitialized} locals=");
                if (!body.LocalSignature.IsNil)
                    sb.Append(string.Join(",", md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(names, null)));
                foreach (var r in body.ExceptionRegions)
                    sb.Append($"\n  region {r.Kind} try {r.TryOffset}+{r.TryLength} h {r.HandlerOffset}+{r.HandlerLength} f {r.FilterOffset} c {(r.CatchType.IsNil ? "" : names.Token(MetadataTokens.GetToken(r.CatchType)))}");
                sb.Append('\n').Append(names.Il(body.GetILBytes()!));
            }
            result.Add(("method", key, sb.ToString()));
        }
    }
    return result;
}

sealed class Names : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader md;
    private static readonly Dictionary<short, OpCode> Ops = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);
    // Compiler-generated names carry the ordinal of the containing member in its type; moving members between files
    // changes those ordinals and nothing else. The local index after the underscore stays.
    private static readonly Regex Ordinals = new(@"(?<=(__|\|))\d+(?=_\d+)|(?<=DisplayClass)\d+(?=_\d+)|(?<=>d__)\d+|(?<=<>9__)\d+(?=_\d+)", RegexOptions.Compiled);
    public Names(MetadataReader md) { this.md = md; }
    public static string Norm(string s) => Ordinals.Replace(s, "#");

    public string Type(TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        var name = Norm(md.GetString(t.Name));
        if (t.IsNested) return Type(t.GetDeclaringType()) + "/" + name;
        var ns = md.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
    private string TypeRef(TypeReferenceHandle h)
    {
        var t = md.GetTypeReference(h);
        var name = md.GetString(t.Name);
        if (t.ResolutionScope.Kind == HandleKind.TypeReference) return TypeRef((TypeReferenceHandle)t.ResolutionScope) + "/" + name;
        var ns = md.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
    public string MethodSig(string name, BlobHandle sig)
    {
        var decoder = new SignatureDecoder<string, object?>(this, md, null);
        var r = md.GetBlobReader(sig);
        var ms = decoder.DecodeMethodSignature(ref r);
        return $"{Norm(name)}{(ms.GenericParameterCount > 0 ? "`" + ms.GenericParameterCount : "")}({string.Join(",", ms.ParameterTypes)}):{ms.ReturnType}";
    }
    public string Attr(CustomAttributeHandle h)
    {
        var c = md.GetCustomAttribute(h);
        // The value blob as text (printable bytes as they are, the rest as \xNN), so that a state machine type named in
        // [IteratorStateMachine(typeof(...))] gets its member ordinal normalized like every other generated name.
        var text = string.Concat(md.GetBlobBytes(c.Value).Select(b => b is >= 0x20 and < 0x7F && b != (byte)'\\' ? ((char)b).ToString() : $"\\x{b:X2}"));
        return Token(MetadataTokens.GetToken(c.Constructor)) + "[" + Norm(text) + "]";
    }
    public string Token(int token)
    {
        var h = MetadataTokens.EntityHandle(token);
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition: return Type((TypeDefinitionHandle)h);
            case HandleKind.TypeReference: return TypeRef((TypeReferenceHandle)h);
            case HandleKind.TypeSpecification:
            {
                var r = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
                return new SignatureDecoder<string, object?>(this, md, null).DecodeType(ref r);
            }
            case HandleKind.MethodDefinition:
            {
                var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                return Type(m.GetDeclaringType()) + "::" + MethodSig(md.GetString(m.Name), m.Signature);
            }
            case HandleKind.FieldDefinition:
            {
                var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                return Type(f.GetDeclaringType()) + "::" + Norm(md.GetString(f.Name));
            }
            case HandleKind.MemberReference:
            {
                var m = md.GetMemberReference((MemberReferenceHandle)h);
                string parent = Token(MetadataTokens.GetToken(m.Parent));
                if (m.GetKind() == MemberReferenceKind.Method) return parent + "::" + MethodSig(md.GetString(m.Name), m.Signature);
                return parent + "::" + Norm(md.GetString(m.Name)) + ":" + m.DecodeFieldSignature(this, null);
            }
            case HandleKind.MethodSpecification:
            {
                var s = md.GetMethodSpecification((MethodSpecificationHandle)h);
                return Token(MetadataTokens.GetToken(s.Method)) + "<" + string.Join(",", s.DecodeSignature(this, null)) + ">";
            }
            case HandleKind.StandaloneSignature:
            {
                var r = md.GetBlobReader(md.GetStandaloneSignature((StandaloneSignatureHandle)h).Signature);
                var ms = new SignatureDecoder<string, object?>(this, md, null).DecodeMethodSignature(ref r);
                return $"calli({string.Join(",", ms.ParameterTypes)}):{ms.ReturnType}";
            }
            default: return h.Kind + ":" + token.ToString("X8");
        }
    }
    public string Il(byte[] il)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < il.Length)
        {
            sb.Append(i).Append(": ");
            short code = il[i++];
            if (code == 0xFE) code = (short)(0xFE00 | il[i++]);
            var op = Ops[code];
            sb.Append(op.Name);
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar:
                    sb.Append(' ').Append(il[i]); i += 1; break;
                case OperandType.InlineVar: sb.Append(' ').Append(BitConverter.ToUInt16(il, i)); i += 2; break;
                case OperandType.InlineBrTarget: case OperandType.InlineI: case OperandType.ShortInlineR:
                    sb.Append(' ').Append(BitConverter.ToInt32(il, i)); i += 4; break;
                case OperandType.InlineI8: case OperandType.InlineR: sb.Append(' ').Append(BitConverter.ToInt64(il, i)); i += 8; break;
                case OperandType.InlineSwitch:
                {
                    int n = BitConverter.ToInt32(il, i); i += 4;
                    for (int k = 0; k < n; k++) { sb.Append(' ').Append(BitConverter.ToInt32(il, i)); i += 4; }
                    break;
                }
                case OperandType.InlineString:
                {
                    int tok = BitConverter.ToInt32(il, i); i += 4;
                    sb.Append(" \"").Append(md.GetUserString(MetadataTokens.UserStringHandle(tok & 0xFFFFFF))).Append('"');
                    break;
                }
                default: // InlineField, InlineMethod, InlineTok, InlineType, InlineSig
                    sb.Append(' ').Append(Token(BitConverter.ToInt32(il, i))); i += 4; break;
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => $"fnptr({string.Join(",", s.ParameterTypes)}):{s.ReturnType}";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool req) => u + (req ? " modreq(" : " modopt(") + m + ")";
    public string GetPinnedType(string e) => e + " pinned";
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Type(h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => TypeRef(h);
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => Token(MetadataTokens.GetToken(h));
}
