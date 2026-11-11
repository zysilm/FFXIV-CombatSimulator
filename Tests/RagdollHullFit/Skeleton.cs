using System.Numerics;
using System.Text;

/// <summary>Reference pose of a game skeleton, read from the Havok packfile inside a .sklb without Havok.</summary>
internal sealed class Skeleton
{
    public string[] Names = Array.Empty<string>();
    public int[] Parents = Array.Empty<int>();
    public Matrix4x4[] Model = Array.Empty<Matrix4x4>();
    public int Index(string name) => Array.IndexOf(Names, name);

    public static Skeleton Read(byte[] sklb)
    {
        // The reference pose is the longest run of 48-byte transforms (translation, unit quaternion, unit scale).
        int bestStart = -1, bestCount = 0;
        for (int align = 0; align < 48; align++)
        {
            int runStart = -1, run = 0;
            for (int at = align; at + 48 <= sklb.Length; at += 48)
            {
                if (IsTransform(sklb, at)) { if (run == 0) runStart = at; run++; }
                else { if (run > bestCount) { bestCount = run; bestStart = runStart; } run = 0; }
            }
            if (run > bestCount) { bestCount = run; bestStart = runStart; }
        }
        // A run found on a 48-byte grid may have started mid-array; extend it backwards on its own grid.
        while (bestStart >= 48 && IsTransform(sklb, bestStart - 48)) { bestStart -= 48; bestCount++; }
        if (bestCount < 30) throw new Exception($"No reference pose found ({bestCount} transforms)");

        // The file is a Havok tagfile: integers are variable-length with the sign in the lowest bit,
        // strings are length-prefixed.
        int namesAt = IndexOf(sklb, Encoding.ASCII.GetBytes("n_root")) - 1;
        if (namesAt < 0) throw new Exception("No bone names found");
        var names = new List<string>();
        for (int at = namesAt; names.Count < bestCount && at < sklb.Length;)
        {
            if (!TryVarint(sklb, ref at, out int length) || length <= 0 || length > 64 || at + length > sklb.Length) break;
            var name = Encoding.ASCII.GetString(sklb, at, length);
            if (name.Any(c => c < 0x21 || c > 0x7E)) break;
            names.Add(name);
            at += length;
        }
        // The run of transforms may run on into other data; the names say how many bones there are.
        if (names.Count < 30) throw new Exception($"Found {names.Count} names for {bestCount} bones");
        bestCount = names.Count;

        // Parent indices: that many integers, the first -1, each below its index.
        int[]? parents = null;
        for (int at = 0; at < sklb.Length && parents == null; at++)
        {
            if (sklb[at] != 0x03 || at + 1 >= sklb.Length || sklb[at + 1] != 0x00) continue;
            var candidate = new int[bestCount];
            int cursor = at; bool ok = true;
            for (int i = 0; i < bestCount && ok; i++)
            {
                ok = TryVarint(sklb, ref cursor, out int value);
                candidate[i] = value;
                ok = ok && (i == 0 ? value == -1 : value >= 0 && value < i);
            }
            if (ok) parents = candidate;
        }
        if (parents == null) throw new Exception("No parent index array found");

        var skeleton = new Skeleton { Names = names.ToArray(), Parents = new int[bestCount], Model = new Matrix4x4[bestCount] };
        for (int i = 0; i < bestCount; i++)
        {
            int at = bestStart + i * 48;
            var t = new Vector3(F(sklb, at), F(sklb, at + 4), F(sklb, at + 8));
            var q = new Quaternion(F(sklb, at + 16), F(sklb, at + 20), F(sklb, at + 24), F(sklb, at + 28));
            var local = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(t);
            int parent = parents[i];
            skeleton.Parents[i] = parent;
            skeleton.Model[i] = parent >= 0 ? local * skeleton.Model[parent] : local;
        }
        return skeleton;
    }

    private static bool TryVarint(byte[] b, ref int at, out int value)
    {
        value = 0;
        long raw = 0; int shift = 0;
        while (true)
        {
            if (at >= b.Length || shift > 28) return false;
            byte current = b[at++];
            raw |= (long)(current & 0x7F) << shift;
            shift += 7;
            if ((current & 0x80) == 0) break;
        }
        value = (int)(raw >> 1);
        if ((raw & 1) != 0) value = -value;
        return true;
    }
    private static float F(byte[] b, int at) => BitConverter.ToSingle(b, at);
    private static bool IsTransform(byte[] b, int at)
    {
        float qx = F(b, at + 16), qy = F(b, at + 20), qz = F(b, at + 24), qw = F(b, at + 28);
        float norm = qx * qx + qy * qy + qz * qz + qw * qw;
        if (!(MathF.Abs(norm - 1f) < 2e-3f)) return false;
        for (int i = 0; i < 3; i++)
        {
            if (!(MathF.Abs(F(b, at + 32 + i * 4) - 1f) < 1e-3f)) return false;
            float t = F(b, at + i * 4);
            if (!float.IsFinite(t) || MathF.Abs(t) > 10f) return false;
        }
        return true;
    }
    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
