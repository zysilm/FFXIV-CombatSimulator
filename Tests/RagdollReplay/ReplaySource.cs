// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CombatSimulator.Animation.Recording;

namespace RagdollReplay;

/// <summary>
/// One recording, in memory, with the bookkeeping the replay host needs: the records of each kind in order
/// (each consumed once, by the query that produced it), the render frame being replayed, and the first
/// place where the replayed controller asked something the recording does not answer.
/// </summary>
internal sealed class ReplaySource
{
    public readonly string Path;
    public readonly List<RagdollRecord> Records;
    public readonly RagdollRecord Header;
    public readonly RagdollRecord Activate;
    public readonly int ActivateIndex;
    public readonly long StopwatchFrequency;

    private readonly Dictionary<RecordType, List<int>> byType = new();
    private readonly Dictionary<RecordType, int> cursor = new();

    /// <summary>Index of the RenderFrame record being replayed, and of the next one (the frame's records lie between).</summary>
    public int FrameIndex = -1;
    public int FrameEnd = -1;
    public int RenderFrame;

    /// <summary>The first thing the replay could not answer exactly as the game did, or null.</summary>
    public string? Divergence { get; private set; }
    public int DivergenceCount { get; private set; }

    public ReplaySource(string path)
    {
        Path = path;
        using var reader = RagdollRecordingReader.Open(path);
        Records = reader.ReadAll().ToList();
        if (reader.FormatVersion != RagdollRecordingFormat.Version)
            throw new ReplayUnsupportedException($"format version {reader.FormatVersion} (the replay reads version {RagdollRecordingFormat.Version})");
        Header = Records.Count > 0 && Records[0].Type == RecordType.Header
            ? Records[0]
            : throw new ReplayUnsupportedException("no header");
        StopwatchFrequency = Header[13].AsLong;
        ActivateIndex = Records.FindIndex(r => r.Type == RecordType.Activate);
        if (ActivateIndex < 0) throw new ReplayUnsupportedException("no Activate record");
        Activate = Records[ActivateIndex];
        for (var i = 0; i < Records.Count; i++)
        {
            var type = Records[i].Type;
            if (!byType.TryGetValue(type, out var list)) byType[type] = list = new List<int>();
            list.Add(i);
        }
    }

    public IReadOnlyList<int> IndicesOf(RecordType type) =>
        byType.TryGetValue(type, out var list) ? list : Array.Empty<int>();

    public void Diverged(string what)
    {
        DivergenceCount++;
        Divergence ??= $"render frame {RenderFrame} (record #{FrameIndex}): {what}";
    }

    private bool[] consumed = Array.Empty<bool>();

    /// <summary>The next unconsumed record of a type that satisfies <paramref name="match"/>, now consumed; records
    /// of the same type that do not match stay for other askers. <paramref name="inFrameOnly"/> limits the search
    /// to the render frame being replayed.</summary>
    public RagdollRecord? Take(RecordType type, Func<RagdollRecord, bool>? match = null, bool inFrameOnly = false)
    {
        if (consumed.Length != Records.Count) consumed = new bool[Records.Count];
        var list = IndicesOf(type);
        cursor.TryGetValue(type, out var at);
        for (var i = at; i < list.Count; i++)
        {
            var index = list[i];
            if (consumed[index]) continue;
            if (inFrameOnly)
            {
                if (FrameEnd >= 0 && index >= FrameEnd) return null;
                if (index < FrameIndex) continue;
            }
            var record = Records[index];
            if (match != null && !match(record)) continue;
            consumed[index] = true;
            while (at < list.Count && consumed[list[at]]) at++;
            cursor[type] = at;
            return record;
        }
        return null;
    }

    /// <summary>Like <see cref="Take"/> but only looks: the record stays to be taken.</summary>
    public (RagdollRecord? Record, int Index) Peek(RecordType type, Func<RagdollRecord, bool>? match = null)
    {
        if (consumed.Length != Records.Count) consumed = new bool[Records.Count];
        var list = IndicesOf(type);
        cursor.TryGetValue(type, out var at);
        for (var i = at; i < list.Count; i++)
        {
            if (consumed[list[i]]) continue;
            var record = Records[list[i]];
            if (match == null || match(record)) return (record, list[i]);
        }
        return (null, -1);
    }

    /// <summary>Records of a type the replay did not consume.</summary>
    public int Unconsumed(RecordType type, Func<RagdollRecord, bool>? match = null)
    {
        if (consumed.Length != Records.Count) consumed = new bool[Records.Count];
        var n = 0;
        foreach (var index in IndicesOf(type))
            if (!consumed[index] && (match == null || match(Records[index]))) n++;
        return n;
    }

    public int CursorOf(RecordType type) => cursor.TryGetValue(type, out var at) ? at : 0;
}

internal sealed class ReplayUnsupportedException : Exception
{
    public ReplayUnsupportedException(string message) : base(message) { }
}
