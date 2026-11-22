using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using CombatSimulator.Camera;
using CombatSimulator.Camera.DeathShots;

namespace DeathShots;

/// <summary>
/// Contact sheets for eyeballing the shots: one PNG per recording in %TEMP%\DeathShots\sheets. Rows: the five
/// profiles, Auto, the Directed baseline, the old Death Cam "da", then a top view of Auto. Columns: six moments of
/// the death. A cell is what the camera sees: the body as a stick figure (spine white, left limbs blue, right limbs
/// red, head a ring), the recorded ground samples as dots, the frame edge. The top view shows the body, the eye
/// (yellow) and its view direction. Bones only, no meshes: this shows framing and side, not the look.
/// </summary>
internal static class Sheets
{
    internal const int CellW = 256, CellH = 144, Pad = 4;

    internal static readonly (string A, string B, int Color)[] Segments =
    {
        ("j_kosi", "j_sebo_a", 0), ("j_sebo_a", "j_sebo_b", 0), ("j_sebo_b", "j_sebo_c", 0), ("j_sebo_c", "j_kubi", 0), ("j_kubi", "j_kao", 0),
        ("j_sebo_c", "j_sako_l", 1), ("j_sako_l", "j_ude_a_l", 1), ("j_ude_a_l", "j_ude_b_l", 1), ("j_ude_b_l", "j_te_l", 1),
        ("j_sebo_c", "j_sako_r", 2), ("j_sako_r", "j_ude_a_r", 2), ("j_ude_a_r", "j_ude_b_r", 2), ("j_ude_b_r", "j_te_r", 2),
        ("j_kosi", "j_asi_a_l", 1), ("j_asi_a_l", "j_asi_b_l", 1), ("j_asi_b_l", "j_asi_c_l", 1), ("j_asi_c_l", "j_asi_d_l", 1),
        ("j_kosi", "j_asi_a_r", 2), ("j_asi_a_r", "j_asi_b_r", 2), ("j_asi_b_r", "j_asi_c_r", 2), ("j_asi_c_r", "j_asi_d_r", 2),
    };

    internal static readonly uint[] Colors = { 0xFFF0F0F0, 0xFF5090FF, 0xFFFF6050 };

    public static void Run(List<Trajectory> all, int count)
    {
        var folder = Path.Combine(Path.GetTempPath(), "DeathShots", "sheets");
        Directory.CreateDirectory(folder);
        // The player's recordings first, then the longest of the others.
        var picks = all.Where(t => t.Humanoid && t.Seconds > 8f && t.Frames.Any(f => f.PhysicsStarted))
            .OrderByDescending(t => t.IsPlayer).ThenByDescending(t => t.Seconds).Take(count).ToList();
        var legend = new StringBuilder();
        legend.AppendLine("rows: Low three-quarter, Low on the face, Hips up to the face, Low from the feet, Low from behind, Low full body, Auto, Directed (baseline), old Death Cam 'da' (baseline), top view of Auto");
        legend.AppendLine("columns: the six moments listed per file (seconds after death); start camera: behind-right of the body at 5 m (azimuth 135 deg)");
        foreach (var t in picks)
        {
            var world = new RecordedWorld(t);
            var start = StartCamera.For(t, 3);
            var rows = new List<Cam[]>();
            foreach (var profile in DeathShotProfiles.Choices.Skip(1).Append(DeathShotProfile.Auto))
                rows.Add(Runners.New(t, world, start, profile, out _));
            rows.Add(Runners.Directed(t, world, start));
            rows.Add(Runners.OldDeathCam(t, start, Runners.OldShots[0]));

            var physics = t.Frames.First(f => f.PhysicsStarted).Time;
            var settledAt = rows[5].Select((c, i) => (c, i)).Where(x => x.c.Holding).Select(x => t.Frames[x.i].Time).DefaultIfEmpty(t.Seconds).First();
            var moments = new[] { 0.8f, 2.2f, MathF.Max(2.4f, physics - 0.3f), physics + 1.0f, MathF.Min(t.Seconds, settledAt + 3f), t.Seconds };
            var frames = moments.Select(m => Math.Clamp(Array.FindIndex(t.Frames, f => f.Time >= m), 0, t.Frames.Length - 1)).ToArray();
            for (var i = 0; i < frames.Length; i++) if (frames[i] < 0) frames[i] = t.Frames.Length - 1;

            var image = new Image((CellW + Pad) * frames.Length + Pad, (CellH + Pad) * (rows.Count + 1) + Pad);
            for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < frames.Length; c++)
                DrawView(image, Pad + c * (CellW + Pad), Pad + r * (CellH + Pad), t, world, t.Frames[frames[c]], rows[r][frames[c]]);
            for (var c = 0; c < frames.Length; c++)
                DrawTop(image, Pad + c * (CellW + Pad), Pad + rows.Count * (CellH + Pad), t, t.Frames[frames[c]], rows[5][frames[c]]);
            var file = Path.Combine(folder, t.Name + ".png");
            image.Save(file);
            legend.AppendLine($"{t.Name}.png: {string.Join(", ", frames.Select(f => t.Frames[f].Time.ToString("F1")))} s; ragdoll at {physics:F1} s; Auto: {string.Join(" / ", frames.Select(f => rows[5][f].Shot))}");
            Console.WriteLine(file);
        }
        File.WriteAllText(Path.Combine(folder, "legend.txt"), legend.ToString());
        Console.WriteLine(Path.Combine(folder, "legend.txt"));
    }

    private static void DrawView(Image image, int x0, int y0, Trajectory t, RecordedWorld world, Trajectory.Frame f, Cam cam)
    {
        image.Fill(x0, y0, CellW, CellH, 0xFF202428);
        var dir = Vector3.Normalize(cam.LookAt - cam.Eye);
        DynamicCameraSolver.BasisFromForward(dir, out var right, out var up);
        var tanV = MathF.Tan(cam.Fov * 0.5f);
        var lens = new DynamicCameraSolver.Lens(tanV * Runners.Aspect, tanV);
        bool Screen(Vector3 p, out float sx, out float sy)
        {
            var ndc = DynamicCameraSolver.Project(p, cam.Eye, dir, right, up, lens);
            sx = x0 + (ndc.X * 0.5f + 0.5f) * CellW;
            sy = y0 + (0.5f - ndc.Y * 0.5f) * CellH;
            return ndc.Z > 0.05f;
        }
        foreach (var g in t.GroundRays)
            if (Screen(g, out var gx, out var gy)) image.Dot(gx, gy, 0xFF55606A, x0, y0);
        foreach (var (a, b, color) in Segments)
        {
            var pa = f.Runtime[t.BoneIndex(a)];
            var pb = f.Runtime[t.BoneIndex(b)];
            if (Screen(pa, out var ax, out var ay) & Screen(pb, out var bx, out var by)) image.Line(ax, ay, bx, by, Colors[color], x0, y0);
        }
        var head = f.Runtime[t.BoneIndex("j_kao")];
        var neck = f.Runtime[t.BoneIndex("j_kubi")];
        var centre = head + (head - neck) * 0.6f;
        if (Screen(centre, out var hx, out var hy))
        {
            var depth = MathF.Max(0.1f, Vector3.Dot(centre - cam.Eye, dir));
            image.Ring(hx, hy, 0.11f / (depth * tanV) * CellH * 0.5f, 0xFFF0F0F0, x0, y0);
        }
        _ = world;
        image.Box(x0, y0, CellW, CellH, cam.Cut ? 0xFFFFD040 : 0xFF9098A0);
    }

    private static void DrawTop(Image image, int x0, int y0, Trajectory t, Trajectory.Frame f, Cam cam)
    {
        image.Fill(x0, y0, CellW, CellH, 0xFF181C20);
        var hips = f.Runtime[t.BoneIndex("j_kosi")];
        const float metres = 7f; // across the cell's height
        var scale = CellH / metres;
        (float, float) Map(Vector3 p) => (x0 + CellW * 0.5f + (p.X - hips.X) * scale, y0 + CellH * 0.5f - (p.Z - hips.Z) * scale);
        foreach (var g in t.GroundRays) { var (gx, gy) = Map(g); image.Dot(gx, gy, 0xFF3A444C, x0, y0); }
        foreach (var (a, b, color) in Segments)
        {
            var (ax, ay) = Map(f.Runtime[t.BoneIndex(a)]);
            var (bx, by) = Map(f.Runtime[t.BoneIndex(b)]);
            image.Line(ax, ay, bx, by, Colors[color], x0, y0);
        }
        var (hx, hy) = Map(f.Runtime[t.BoneIndex("j_kao")]);
        image.Ring(hx, hy, 3f, 0xFFF0F0F0, x0, y0);
        var (ex, ey) = Map(cam.Eye);
        var (lx, ly) = Map(cam.LookAt);
        image.Line(ex, ey, lx, ly, 0xFFE0C040, x0, y0);
        image.Ring(ex, ey, 3f, 0xFFFFE060, x0, y0);
        image.Box(x0, y0, CellW, CellH, 0xFF606870);
    }

    /// <summary>A small RGBA canvas with clipped drawing and a PNG writer (zlib + CRC from the framework).</summary>
    internal sealed class Image
    {
        private readonly int width, height;
        private readonly uint[] pixels;

        public Image(int width, int height)
        {
            this.width = width;
            this.height = height;
            pixels = new uint[width * height];
            Array.Fill(pixels, 0xFF101214);
        }

        /// <summary>The size of the cells drawing is clipped to.</summary>
        public int ClipW = CellW, ClipH = CellH;

        private void Set(int x, int y, uint argb, int cx, int cy)
        {
            if (x < cx || y < cy || x >= cx + ClipW || y >= cy + ClipH || x < 0 || y < 0 || x >= width || y >= height) return;
            pixels[y * width + x] = argb;
        }

        public void Fill(int x0, int y0, int w, int h, uint argb)
        {
            for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++)
                Set(x, y, argb, x0, y0);
        }

        public void Box(int x0, int y0, int w, int h, uint argb)
        {
            for (var x = x0; x < x0 + w; x++) { Set(x, y0, argb, x0, y0); Set(x, y0 + h - 1, argb, x0, y0); }
            for (var y = y0; y < y0 + h; y++) { Set(x0, y, argb, x0, y0); Set(x0 + w - 1, y, argb, x0, y0); }
        }

        public void Dot(float x, float y, uint argb, int cx, int cy)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || MathF.Abs(x) > 1e5f || MathF.Abs(y) > 1e5f) return;
            Set((int)x, (int)y, argb, cx, cy);
        }

        public void Line(float ax, float ay, float bx, float by, uint argb, int cx, int cy)
        {
            if (!float.IsFinite(ax + ay + bx + by)) return;
            var steps = (int)MathF.Min(4000f, MathF.Max(MathF.Abs(bx - ax), MathF.Abs(by - ay))) + 1;
            for (var i = 0; i <= steps; i++)
            {
                var t = i / (float)steps;
                var x = (int)(ax + (bx - ax) * t);
                var y = (int)(ay + (by - ay) * t);
                Set(x, y, argb, cx, cy);
                Set(x + 1, y, argb, cx, cy);
            }
        }

        public void Ring(float x, float y, float radius, uint argb, int cx, int cy)
        {
            if (!float.IsFinite(x + y + radius)) return;
            radius = Math.Clamp(radius, 2f, 400f);
            var steps = (int)(radius * 7f) + 8;
            for (var i = 0; i < steps; i++)
            {
                var a = i * MathF.Tau / steps;
                Set((int)(x + MathF.Cos(a) * radius), (int)(y + MathF.Sin(a) * radius), argb, cx, cy);
            }
        }

        public void Save(string path)
        {
            var raw = new byte[(width * 4 + 1) * height];
            for (var y = 0; y < height; y++)
            {
                var at = y * (width * 4 + 1) + 1;
                for (var x = 0; x < width; x++)
                {
                    var p = pixels[y * width + x];
                    raw[at++] = (byte)(p >> 16);
                    raw[at++] = (byte)(p >> 8);
                    raw[at++] = (byte)p;
                    raw[at++] = (byte)(p >> 24);
                }
            }
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true)) z.Write(raw);
            using var file = File.Create(path);
            file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8;
            header[9] = 6;
            Chunk(file, "IHDR", header);
            Chunk(file, "IDAT", packed.ToArray());
            Chunk(file, "IEND", Array.Empty<byte>());
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> four = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
            s.Write(four);
            var name = Encoding.ASCII.GetBytes(type);
            s.Write(name);
            s.Write(data);
            var crc = 0xFFFFFFFFu;
            foreach (var b in name) crc = Crc(crc, b);
            foreach (var b in data) crc = Crc(crc, b);
            BinaryPrimitives.WriteUInt32BigEndian(four, ~crc);
            s.Write(four);
        }

        private static uint Crc(uint crc, byte b)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            return crc;
        }
    }
}
