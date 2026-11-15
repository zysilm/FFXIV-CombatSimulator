// TMB timeline ("TMLB") as used inside a cutscene CTTL chunk. Our own reader.
//
//   TMLB: magic, u32 size, u32 item count, then items back to back.
//   Every item: magic[4], u32 size, i16 id, i16 time (frames, 30 fps; TMDH/TMAL use it as something else).
//   Offsets stored inside items are relative to (item start + 8).
//   TMDH: i16 id, i16 0, i16 LENGTH (frames), i16 3.
//   TMAC (actor): +0x0C i32 0, +0x10 u32 0xFF0000NN (NN = cutscene actor index), +0x14 offset, +0x18 count → i16 track ids.
//   TMTR (track): +0x0C offset, +0x10 count → i16 entry ids.
//   Cxxx entries: fixed-size payload of 32-bit words. TMFC: function curves (see Curve).

using System.Text;

namespace CutsceneCamera;

public sealed class TmbItem
{
    public string Magic = "";
    public int Pos, Size;
    public short Id, Time;
    public int OwnerActor = -1; // cutscene actor index of the TMAC that owns this entry, if any
    public int OwnerTrack = -1; // TMTR id holding this entry

    public int Word(byte[] d, int i) => BitConverter.ToInt32(d, Pos + 12 + 4 * i);
    public float Float(byte[] d, int i) => BitConverter.ToSingle(d, Pos + 12 + 4 * i);
    public int WordCount => (Size - 12) / 4;
}

public sealed class CurveKey
{
    public float Frame, Value, TanIn, TanOut;
    public int Flags;
}

public sealed class CurveChannel
{
    public byte Code, Group;   // parameter code and group; together they name the parameter
    public byte Node;          // rig node index (255 = lens / not a node)
    public sbyte Parent;       // parent rig node, -1 = root
    public readonly List<CurveKey> Keys = new();

    /// <summary>Cubic Hermite between keys; tangents are value per frame (checked: on manfst00010 shot 4 the
    /// middle tangent 0.00535 matches the average slope of its neighbours).</summary>
    public float Eval(float frame)
    {
        if (Keys.Count == 0) return 0f;
        if (frame <= Keys[0].Frame) return Keys[0].Value;
        var last = Keys[^1];
        if (frame >= last.Frame) return last.Value;
        for (var i = 0; i + 1 < Keys.Count; i++)
        {
            var a = Keys[i];
            var b = Keys[i + 1];
            if (frame > b.Frame) continue;
            var dt = b.Frame - a.Frame;
            if (dt <= 0f) return b.Value;
            var t = (frame - a.Frame) / dt;
            var t2 = t * t;
            var t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * a.Value + (t3 - 2 * t2 + t) * dt * a.TanOut
                 + (-2 * t3 + 3 * t2) * b.Value + (t3 - t2) * dt * b.TanIn;
        }
        return last.Value;
    }

    public bool Animated => Keys.Count > 1 && Keys.Any(k => MathF.Abs(k.Value - Keys[0].Value) > 1e-5f);
}

public sealed class TmbTimeline
{
    public readonly int Index;
    public readonly int Length; // frames
    public readonly List<TmbItem> Items = new();
    public readonly Dictionary<int, TmbItem> ById = new();
    public readonly Dictionary<int, List<CurveChannel>> Curves = new(); // TMFC id → channels
    /// <summary>TMTR id → Lua condition tokens (op, int value, float value), when the track has one. +0x14 of a TMTR is
    /// the offset of a block {i32 8, i32 count, count × 12 B tokens}; op codes follow the operator list VFXEditor documents
    /// (0x13 variable, 0x11 int literal, 0x0D ==, 0x0C !=, 0x0E and, 0x0F or, ...).</summary>
    public readonly Dictionary<int, List<(int Op, uint Value, float F)>> TrackLua = new();

    public TmbTimeline(byte[] d, int start, int index)
    {
        Index = index;
        if (Encoding.ASCII.GetString(d, start, 4) != "TMLB")
            throw new InvalidDataException("CTTL without TMLB");
        var count = BitConverter.ToInt32(d, start + 8);
        var p = start + 12;
        for (var k = 0; k < count; k++)
        {
            var it = new TmbItem
            {
                Magic = Encoding.ASCII.GetString(d, p, 4),
                Pos = p,
                Size = BitConverter.ToInt32(d, p + 4),
                Id = BitConverter.ToInt16(d, p + 8),
                Time = BitConverter.ToInt16(d, p + 10),
            };
            Items.Add(it);
            if (it.Magic != "TMAL") ById[it.Id] = it;
            if (it.Magic == "TMDH") Length = BitConverter.ToInt16(d, p + 12);
            if (it.Magic == "TMFC") Curves[it.Id] = ReadCurves(d, it);
            p += it.Size;
        }

        foreach (var tr in Items.Where(i => i.Magic == "TMTR"))
        {
            var lo = BitConverter.ToInt32(d, tr.Pos + 0x14);
            if (lo == 0) continue;
            var at = tr.Pos + 8 + lo;
            var n = BitConverter.ToInt32(d, at + 4);
            var toks = new List<(int, uint, float)>();
            for (var i = 0; i < n && i < 64; i++)
                toks.Add((BitConverter.ToInt32(d, at + 8 + 12 * i), BitConverter.ToUInt32(d, at + 12 + 12 * i), BitConverter.ToSingle(d, at + 16 + 12 * i)));
            TrackLua[tr.Id] = toks;
        }

        // Ownership: actor → tracks → entries.
        foreach (var a in Items.Where(i => i.Magic == "TMAC"))
        {
            var actorIndex = BitConverter.ToInt32(d, a.Pos + 0x10) & 0xFF;
            foreach (var tid in ReadIdList(d, a, 0x14))
                if (ById.TryGetValue(tid, out var tr) && tr.Magic == "TMTR")
                    foreach (var eid in ReadIdList(d, tr, 0x0C))
                        if (ById.TryGetValue(eid, out var e)) { e.OwnerActor = actorIndex; e.OwnerTrack = tid; }
        }
    }

    private static List<int> ReadIdList(byte[] d, TmbItem it, int fieldOffset)
    {
        var off = BitConverter.ToInt32(d, it.Pos + fieldOffset);
        var cnt = BitConverter.ToInt32(d, it.Pos + fieldOffset + 4);
        var res = new List<int>();
        var at = it.Pos + 8 + off;
        for (var i = 0; i < cnt && at + 2 * i + 2 <= d.Length; i++)
            res.Add(BitConverter.ToInt16(d, at + 2 * i));
        return res;
    }

    // TMFC: +0x0C i32 data offset, +0x10 i32 channel count, +0x14 ?, +0x18 end offset, +0x1C ?
    // Channel header (16 bytes, at item+8+offset+4): u32 0, code, group, node, parent(sbyte), i32 key offset (from header), i32 key count.
    // Key (24 bytes): u32 flags, f32 frame, f32 1/(next.frame-frame), f32 value, f32 tangent in, f32 tangent out.
    private static List<CurveChannel> ReadCurves(byte[] d, TmbItem it)
    {
        var start = BitConverter.ToInt32(d, it.Pos + 12);
        var n = BitConverter.ToInt32(d, it.Pos + 16);
        var list = new List<CurveChannel>();
        var hp = it.Pos + 8 + start + 4;
        for (var i = 0; i < n; i++)
        {
            var h = hp + i * 16;
            var ch = new CurveChannel { Code = d[h + 4], Group = d[h + 5], Node = d[h + 6], Parent = (sbyte)d[h + 7] };
            var ko = BitConverter.ToInt32(d, h + 8);
            var kc = BitConverter.ToInt32(d, h + 12);
            for (var k = 0; k < kc; k++)
            {
                var r = h + ko + k * 24;
                ch.Keys.Add(new CurveKey
                {
                    Flags = BitConverter.ToInt32(d, r),
                    Frame = BitConverter.ToSingle(d, r + 4),
                    Value = BitConverter.ToSingle(d, r + 12),
                    TanIn = BitConverter.ToSingle(d, r + 16),
                    TanOut = BitConverter.ToSingle(d, r + 20),
                });
            }
            list.Add(ch);
        }
        return list;
    }
}
