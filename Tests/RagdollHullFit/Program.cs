using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using CombatSimulator.Animation;
using Lumina;

string sqpack = args.Length > 0 ? args[0] : "C:/Program Files (x86)/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/sqpack";
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string pluginDirectory = Path.Combine(root, "CombatSimulator/bin/Release");
string devDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher/addon/Hooks/dev");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string directory in new[] { pluginDirectory, devDirectory })
    {
        string file = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
    }
    return null;
};
var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(pluginDirectory, "CombatSimulator.dll"));
var parse = plugin.GetType("CombatSimulator.Animation.RagdollController", true)!
    .GetMethod("TryReadModelGeometryData", BindingFlags.NonPublic | BindingFlags.Static)!;
using var game = new GameData(sqpack);

ModelGeometryData? Load(string path)
{
    byte[]? bytes = Path.IsPathRooted(path) ? (File.Exists(path) ? File.ReadAllBytes(path) : null) : game.GetFile(path)?.Data;
    if (bytes == null) return null;
    object?[] parameters = { bytes, null };
    if (!(bool)parse.Invoke(null, parameters)!) throw new Exception($"raw parser rejected '{path}'");
    var source = parameters[1]!;
    var data = new ModelGeometryData();
    foreach (var field in typeof(ModelGeometryData).GetFields())
        field.SetValue(data, source.GetType().GetField(field.Name)!.GetValue(source));
    return data;
}

return Harness.Run(game, Load, args.Skip(1).ToArray());
