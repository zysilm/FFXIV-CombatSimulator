using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace CutsceneCamera;

/// <summary>
/// Parser regression digest: for every entry of the curated list, the shots it plays (C004 fields, attach slots, every
/// curve key) and the evaluated eye / aim / up / FOV at every frame, with the Hyur reference body (A at the origin
/// facing +Z, B placed 2 m in front facing back). Written with round-trip number formatting, so two parsers that read
/// the files the same way produce byte-identical digests (`-- digest out.txt`).
/// </summary>
internal static class Digest
{
    public static string Build(string jsonPath, Func<string, CutbFile?> load, Bind.Body body)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var sb = new StringBuilder();
        string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        string V(Vector3 v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";
        var bMatrix = Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0, 0, 2f);
        var cache = new Dictionary<string, CutbFile?>(StringComparer.Ordinal);
        foreach (var e in doc.RootElement.GetProperty("shots").EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;
            var path = e.GetProperty("cutscene").GetString()!;
            var tlIndex = e.GetProperty("timeline").GetInt32();
            var a = e.GetProperty("followActor").GetInt32();
            if (!cache.TryGetValue(path, out var c)) cache[path] = c = load(path);
            sb.Append("entry ").Append(id).Append('\n');
            if (c == null || tlIndex >= c.Timelines.Count) { sb.Append("  missing\n"); continue; }
            var tl = c.Timelines[tlIndex];
            var shots = CameraShot.FromTimeline(c, tl).ToList();
            sb.Append($"  timeline length {tl.Length} shots {shots.Count}\n");
            foreach (var span in e.GetProperty("shots").EnumerateArray())
            {
                var s = shots[span.GetProperty("shot").GetInt32()];
                var from = span.GetProperty("from").GetInt32();
                var to = span.GetProperty("to").GetInt32();
                sb.Append($"  shot {s.Index} start {s.Start} dur {s.Duration} tmfc {s.TmfcId} key {s.Key} near {F(s.Near)} far {F(s.Far)} root {s.Root} eye {s.Eye} aim {s.Aim} overlay {s.IsOverlay}\n");
                foreach (var ch in s.Channels ?? new List<CurveChannel>())
                {
                    sb.Append($"    g{ch.Group} c{ch.Code} n{ch.Node} p{ch.Parent}:");
                    foreach (var k in ch.Keys) sb.Append($" [{k.Flags} {F(k.Frame)} {F(k.Value)} {F(k.TanIn)} {F(k.TanOut)}]");
                    sb.Append('\n');
                }
                Matrix4x4 Anchor(Attach at)
                {
                    if (at.Actor == 255) return Matrix4x4.Identity;
                    return Bind.PointFrame(Bind.Mapping.EidBindPoint, body, at.Point, at.Actor == a ? Matrix4x4.Identity : bMatrix);
                }
                for (var f = from; f < to; f++)
                {
                    var ev = s.Evaluate(f - s.Start, Anchor);
                    var roll = s.Channel(9, 117, 255)?.Eval(f - s.Start) ?? 0f;
                    sb.Append($"    f{f} eye {V(ev.Eye)} aim {V(ev.Aim)} up {V(ev.Up)} fov {F(ev.Fov)} c117 {F(roll)}\n");
                }
            }
        }
        return sb.ToString();
    }
}
