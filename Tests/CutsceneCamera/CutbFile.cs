// Offline reader for FFXIV cutscene files (.cutb) — only the parts needed to study the camera.
// Written from our own byte-level inspection of the game files; no third-party code copied.
//
// Container layout (little endian):
//   0x00 "CUTB"   0x04 u32 file size   0x08 u32 chunk count
//   0x0C chunk table, 16 bytes each: tag[4], u32 (always 0x10), i32 offset, i32 size.
//        The offset is relative to the offset field itself (entry + 8).
//   Tags seen: CTRL (resource path list), CTIS, CTDS (level name + a centre/radius), CTEX, CTAL (actor table),
//   CTCB (text keys), CTPA (per-actor cut animation .pap list), CTTL (one TMB timeline each — "TMLB").

using System.Text;

namespace CutsceneCamera;

public sealed record CutbChunk(string Tag, int Offset, int Size);

public sealed class CutbFile
{
    public readonly byte[] Data;
    public readonly List<CutbChunk> Chunks = new();
    public readonly List<TmbTimeline> Timelines = new();

    public CutbFile(byte[] data)
    {
        Data = data;
        if (Encoding.ASCII.GetString(data, 0, 4) != "CUTB")
            throw new InvalidDataException("not a CUTB file");
        var count = BitConverter.ToInt32(data, 8);
        for (var i = 0; i < count; i++)
        {
            var e = 0x0C + i * 16;
            var tag = Encoding.ASCII.GetString(data, e, 4);
            var off = e + 8 + BitConverter.ToInt32(data, e + 8);
            var size = BitConverter.ToInt32(data, e + 12);
            Chunks.Add(new CutbChunk(tag, off, size));
            if (tag == "CTTL")
                Timelines.Add(new TmbTimeline(data, off, Timelines.Count));
        }
    }

    /// <summary>CTDS carries a world-space point and a radius right after its first three words
    /// (e.g. manfst00010: (327.6, -3.04, -105.7) r=120). Meaning inferred, not confirmed.</summary>
    public (float X, float Y, float Z, float R)? SceneCentre()
    {
        var c = Chunks.FirstOrDefault(x => x.Tag == "CTDS");
        if (c == null) return null;
        var o = c.Offset + 0x10;
        return (BitConverter.ToSingle(Data, o), BitConverter.ToSingle(Data, o + 4),
                BitConverter.ToSingle(Data, o + 8), BitConverter.ToSingle(Data, o + 12));
    }
}
