using System.Numerics;

namespace CutsceneCamera;

/// <summary>Top-down (X right, Z down) plot of one cutscene: actor placements (white squares), camera eye path
/// per shot (one colour per shot), short lines from eye toward the aim point every 15 frames. Only shots that are
/// not attached to an actor are drawn (their rig is in world space); the view is framed on the median 80% of eyes.</summary>
public static class Plot
{
    public static void TopDown(CutbFile c, string path, int size = 900)
    {
        var shots = c.Timelines.SelectMany(tl => CameraShot.FromTimeline(c, tl)).Where(s => !s.IsOverlay && s.Channels != null && s.Root.Actor == 255 && s.Eye.Actor == 255 && s.Aim.Actor == 255).ToList(); // world-space shots only: attached ones need bone positions
        var actors = Report.ActorPlacements(c).SelectMany(kv => kv.Value.Select(v => v.Pos)).ToList();
        var samples = new List<(int Shot, Vector3 Eye, Vector3 Aim)>();
        for (var i = 0; i < shots.Count; i++)
        {
            var s = shots[i];
            for (var f = 0; f <= s.Duration; f += 3)
            {
                var e = s.Evaluate(f, Report.Anchors(c, s.Timeline, s.Start + f));
                samples.Add((i, e.Eye, e.Aim));
            }
        }
        if (samples.Count == 0) return;

        // Frame the camera path; actors far outside are clipped.
        var pts = samples.Select(s => s.Eye).ToList();
        var xs = pts.Select(p => p.X).OrderBy(v => v).ToList(); var zs = pts.Select(p => p.Z).OrderBy(v => v).ToList();
        float minX = xs[xs.Count / 10], maxX = xs[xs.Count * 9 / 10], minZ = zs[zs.Count / 10], maxZ = zs[zs.Count * 9 / 10];
        var span = MathF.Max(MathF.Max(maxX - minX, maxZ - minZ), 10f) * 1.15f;
        var cx = (minX + maxX) / 2; var cz = (minZ + maxZ) / 2;
        var px = new byte[size * size * 3];
        (int, int) Map(Vector3 p) => ((int)((p.X - cx) / span * size + size / 2), (int)((p.Z - cz) / span * size + size / 2));
        void Set(int x, int y, (byte r, byte g, byte b) col)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            var o = ((size - 1 - y) * size + x) * 3; // BMP rows are bottom-up; flip so +Z is down
            px[o] = col.b; px[o + 1] = col.g; px[o + 2] = col.r;
        }
        void Line(Vector3 a, Vector3 b, (byte, byte, byte) col)
        {
            var (x0, y0) = Map(a); var (x1, y1) = Map(b);
            var n = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1;
            for (var i = 0; i <= n; i++) Set(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n, col);
        }
        // 1 m grid every 5 m
        for (var g = -100; g <= 100; g++)
        {
            var gx = MathF.Floor(cx / 5) * 5 + g * 5; var gz = MathF.Floor(cz / 5) * 5 + g * 5;
            Line(new Vector3(gx, 0, cz - span), new Vector3(gx, 0, cz + span), (35, 35, 35));
            Line(new Vector3(cx - span, 0, gz), new Vector3(cx + span, 0, gz), (35, 35, 35));
        }
        foreach (var a in actors)
        {
            var (x, y) = Map(a);
            for (var dx = -3; dx <= 3; dx++) for (var dy = -3; dy <= 3; dy++) Set(x + dx, y + dy, (255, 255, 255));
        }
        (byte, byte, byte) Colour(int i)
        {
            var h = i * 0.618034f % 1f;
            float r = MathF.Abs(h * 6 - 3) - 1, g = 2 - MathF.Abs(h * 6 - 2), b = 2 - MathF.Abs(h * 6 - 4);
            return ((byte)(Math.Clamp(r, 0, 1) * 225 + 30), (byte)(Math.Clamp(g, 0, 1) * 225 + 30), (byte)(Math.Clamp(b, 0, 1) * 225 + 30));
        }
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var col = Colour(s.Shot);
            if (i % 5 == 0)
            {
                var dir = s.Aim - s.Eye; dir.Y = 0;
                if (dir.LengthSquared() > 1e-6f) Line(s.Eye, s.Eye + Vector3.Normalize(dir) * span * 0.04f, ((byte)(col.Item1 / 2), (byte)(col.Item2 / 2), (byte)(col.Item3 / 2)));
            }
            var (x, y) = Map(s.Eye);
            for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) Set(x + dx, y + dy, col);
        }

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        var rowBytes = size * 3; var pad = (4 - rowBytes % 4) % 4;
        bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(54 + (rowBytes + pad) * size); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(size); bw.Write(size); bw.Write((short)1); bw.Write((short)24); bw.Write(0);
        bw.Write((rowBytes + pad) * size); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        for (var y = 0; y < size; y++) { bw.Write(px, y * rowBytes, rowBytes); for (var p = 0; p < pad; p++) bw.Write((byte)0); }
    }
}
