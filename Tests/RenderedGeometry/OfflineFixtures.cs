using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using CombatSimulator.Animation;
using Lumina;

internal static class OfflineFixtures
{
    internal static void Run(string sqpack)
    {
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
        var type = plugin.GetType("CombatSimulator.Animation.RagdollController", true)!;
        var parse = type.GetMethod("TryReadModelGeometryData", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var game = new GameData(sqpack);
        var pbdBytes = game.GetFile("chara/xls/boneDeformer/human.pbd")?.Data ?? throw new Exception("Installed human.pbd missing");
        var pbd = new RenderedRaceDeformation(pbdBytes);
        int models = 0, faces = 0, routes = 0;
        foreach (int race in Enumerable.Range(1, 18).Select(i => i * 100 + 1))
        {
            string path = $"chara/human/c{race:D4}/obj/body/b0001/model/c{race:D4}b0001_top.mdl";
            var bytes = game.GetFile(path)?.Data;
            if (bytes == null)
                for (int equipment = 1; equipment <= 50 && bytes == null; equipment++)
                {
                    path = $"chara/equipment/e{equipment:D4}/model/c{race:D4}e{equipment:D4}_top.mdl";
                    bytes = game.GetFile(path)?.Data;
                }
            if (bytes == null) continue;
            object?[] parameters = { bytes, null };
            if (!(bool)parse.Invoke(null, parameters)!) throw new Exception($"Production raw parser rejected installed '{path}' (version={BitConverter.ToUInt32(bytes):X8})");
            var source = parameters[1]!;
            var data = new ModelGeometryData();
            foreach (var field in typeof(ModelGeometryData).GetFields())
                field.SetValue(data, source.GetType().GetField(field.Name)!.GetValue(source));
            string[] names = data.BoneNameOffsets.Select(offset => RenderedGeometryDecoder.StringAt(data.Strings, offset)).ToArray();
            var identity = Enumerable.Repeat(Matrix4x4.Identity, names.Length).ToArray();
            var vertices = new List<RenderedGeometryVertex>(); var triangles = new List<int>();
            var lod = data.Lods[0];
            for (int mi = lod.MeshIndex; mi < lod.MeshIndex + lod.MeshCount; mi++)
            {
                var mesh = data.Meshes[mi];
                if (mesh.BoneTableIndex >= data.BoneTables.Length) continue;
                var map = data.BoneTables[mesh.BoneTableIndex].BoneIndex.Select(x => (int)x).ToArray();
                RenderedGeometryDecoder.Append(data, 0, mi, 1, uint.MaxValue, new bool[data.Shapes.Length], map, identity, null,
                    Matrix4x4.Identity, vertices, triangles);
            }
            if (triangles.Count == 0) throw new Exception($"No decoded faces for installed '{path}'");
            models++; faces += triangles.Count / 3;
            ushort donor = (ushort)(race % 200 == 101 ? 101 : 201);
            var parentless = Enumerable.Repeat(-1, names.Length).ToArray();
            if (!pbd.TryBuild(donor, (ushort)race, names, parentless, out var deformation))
            {
                donor = 101;
                if (!pbd.TryBuild(donor, (ushort)race, names, parentless, out deformation))
                { donor = (ushort)race; pbd.TryBuild(donor, (ushort)race, names, parentless, out deformation); }
            }
            foreach (var matrix in deformation)
                if (!float.IsFinite(matrix.GetDeterminant())) throw new Exception("Installed PBD produced a non-finite matrix");
            routes++;
            Console.WriteLine($"Installed fixture c{race:D4}: version=0x{data.FileHeader.Version:X8} bytes={bytes.Length} meshes={lod.MeshCount} vertices={vertices.Count} triangles={triangles.Count / 3} bones={names.Length} PBD=c{donor:D4}->c{race:D4}");
        }
        if (models < 4) throw new Exception($"Expected installed body models from at least four races, found {models}");
        Console.WriteLine($"Installed fixtures: {models} actual MDLs, {faces} triangles, {routes} PBD routes; production raw parser invoked from built plugin. No live process access.");
    }
}
