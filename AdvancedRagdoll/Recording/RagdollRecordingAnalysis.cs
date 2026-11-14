// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CombatSimulator.Animation.Recording;

/// <summary>What one recording contains, for a quick look.</summary>
public sealed class RagdollRecordingSummary
{
    public string Path = string.Empty;
    public long FileBytes;
    public bool Truncated;
    public RagdollRecord? Header;
    public RagdollRecord? End;
    public readonly SortedDictionary<RecordType, (long Count, long Bytes)> ByType = new();
    public long Records;
    public int RenderFrames;
    public int PhysicsFrames;
    public int FixedSteps;
    public int Bodies;
    public int RigBodies;
    public long WorldQueries;
    public long ApiCalls;
    public long OutsideApiCalls;
    public double Seconds;
    public int ConfigProperties;
    public int FormatVersion;
    /// <summary>Records replaced by a note because their body did not finish.</summary>
    public int DiscardedRecords;
    /// <summary>The state carried over from earlier activations of the controller (format version 2), as text.</summary>
    public string? CarriedState;
    public readonly List<string> Notes = new();

    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine($"file: {Path}");
        text.AppendLine($"size: {FileBytes} bytes, {Records} records{(Truncated ? " (TRUNCATED: the last record is incomplete)" : string.Empty)}");
        if (Header is { } h)
        {
            text.AppendLine($"format version: {h[0]}");
            text.AppendLine($"plugin: {h[1]}  DEV_EXPERIMENTAL={h[2]}  PUBLIC_BUILD={h[3]}");
            text.AppendLine($"BepuPhysics: {h[4]}");
            text.AppendLine($"runtime: {h[5]} on {h[6]} ({h[7]}), {h[8]} processors, vector width {h[9]}, SIMD {h[10]}, little-endian={h[11]}");
            text.AppendLine($"recorded (UTC): {new DateTime(h[12].AsLong, DateTimeKind.Utc):yyyy-MM-dd HH:mm:ss}  stopwatch frequency {h[13]}");
            text.AppendLine($"session {h[14]} file #{h[15]} controller {h[16]} (main controller: {h[17]})");
            text.AppendLine($"actor {h[18]} entity {h[19]} object index {h[20]} local player: {h[21]}");
        }
        else text.AppendLine("header: MISSING");
        text.AppendLine($"render frames: {RenderFrames}  physics frames: {PhysicsFrames}  fixed steps: {FixedSteps}  duration: {Seconds.ToString("F3", CultureInfo.InvariantCulture)} s");
        text.AppendLine($"bodies: {Bodies} in the simulation, {RigBodies} ragdoll bones  configuration properties: {ConfigProperties}");
        if (Header != null)
            text.AppendLine(CarriedState != null
                ? $"carried over from earlier activations: {CarriedState}"
                : FormatVersion < RagdollRecordingFormat.FirstVersionWithCarriedState
                    ? $"carried over from earlier activations: NOT RECORDED (format version {FormatVersion}; a replay cannot restore it)"
                    : "carried over from earlier activations: MISSING (no complete Activate record)");
        if (DiscardedRecords > 0)
            text.AppendLine($"discarded partial records: {DiscardedRecords} (each is a note in the file where the record would have been)");
        text.AppendLine($"world queries: {WorldQueries}  calls from outside: {OutsideApiCalls} (of {ApiCalls} recorded calls)");
        text.AppendLine(End is { } e
            ? $"end: {e[0]} (render frames {e[1]}, physics frames {e[2]}, fixed steps {e[3]})"
            : "end: MISSING (the recording was not closed cleanly)");
        text.AppendLine("records by type:");
        foreach (var entry in ByType)
            text.AppendLine($"  {entry.Key,-18} {entry.Value.Count,9}  {entry.Value.Bytes,12} bytes");
        foreach (var note in Notes.Take(20))
            text.AppendLine($"note: {note}");
        if (Notes.Count > 20) text.AppendLine($"... {Notes.Count - 20} more notes");
        return text.ToString();
    }
}

/// <summary>The first place two recordings disagree.</summary>
public sealed class RagdollRecordingDifference
{
    public bool Identical;
    public long OutputRecordsCompared;
    public string Description = string.Empty;
    /// <summary>First input record that differs, if any (a different input explains a different output).</summary>
    public string? InputDifference;
}

public static class RagdollRecordingAnalysis
{
    public static RagdollRecordingSummary Summarize(string path)
    {
        var summary = new RagdollRecordingSummary { Path = path, FileBytes = new FileInfo(path).Length };
        using var reader = RagdollRecordingReader.Open(path);
        while (reader.Read() is { } record)
        {
            summary.Records++;
            summary.ByType.TryGetValue(record.Type, out var tally);
            summary.ByType[record.Type] = (tally.Count + 1, tally.Bytes + record.PayloadBytes + 6);
            if (RagdollRecordingFormat.IsWorldQuery(record.Type)) summary.WorldQueries++;
            switch (record.Type)
            {
                case RecordType.Header:
                    if (summary.Header == null)
                    {
                        summary.Header = record;
                        summary.FormatVersion = record[0].AsInt;
                    }
                    break;
                case RecordType.Activate:
                    if (summary.FormatVersion >= RagdollRecordingFormat.FirstVersionWithCarriedState)
                        summary.CarriedState ??= DescribeCarriedState(record);
                    break;
                case RecordType.End:
                    summary.End = record;
                    break;
                case RecordType.Note:
                    summary.Notes.Add(record[0].AsString ?? string.Empty);
                    if (IsDiscardedRecord(record)) summary.DiscardedRecords++;
                    break;
                case RecordType.ConfigSnapshot:
                    summary.ConfigProperties = record[0].Count / 2;
                    break;
                case RecordType.RenderFrame:
                    summary.RenderFrames++;
                    summary.Seconds += record[1].AsFloat;
                    break;
                case RecordType.FrameBegin:
                    summary.PhysicsFrames++;
                    break;
                case RecordType.BodyStates:
                    if (record[0].AsInt > 0) summary.FixedSteps++;
                    summary.Bodies = Math.Max(summary.Bodies, record[4].Count);
                    break;
                case RecordType.RigBodies:
                    summary.RigBodies = record[0].AsInt;
                    break;
                case RecordType.ApiCall:
                    summary.ApiCalls++;
                    if (record[0].AsInt == 0) summary.OutsideApiCalls++;
                    break;
            }
        }
        summary.Truncated = reader.Truncated;
        return summary;
    }

    /// <summary>True for the note that stands where a record whose body did not finish would have been.</summary>
    public static bool IsDiscardedRecord(RagdollRecord record) =>
        record.Type == RecordType.Note &&
        (record[0].AsString?.StartsWith(RagdollRecordingFormat.DiscardedRecordNote, StringComparison.Ordinal) ?? false);

    /// <summary>The carried-over state of an Activate record (format version 2), or null when the record is too short.</summary>
    public static string? DescribeCarriedState(RagdollRecord activate)
    {
        var at = RagdollRecordingFormat.ActivateCarriedStateIndex(activate);
        if (at < 0 || at + 3 >= activate.Values.Length || activate[at + 3].Tag != ValueTag.List) return null;
        var struck = activate[at + 3].Count;
        var colliders = at + 4 + struck;
        if (activate[colliders].Tag != ValueTag.List) return null;
        var requests = colliders + 1 + activate[colliders].Count;
        if (requests >= activate.Values.Length) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"impactRandomState 0x{activate[at].AsUInt:X8}, nextGrabSlotId {activate[at + 1].AsInt}, " +
            $"externalBodyGeneration {activate[at + 2].AsInt}, {struck} body handle(s) already struck, " +
            $"{activate[colliders].Count} strike collider(s), {activate[requests].AsInt} pending traversal proxy request(s)");
    }

    /// <summary>
    /// Compares the output records of two recordings in order and reports the first difference
    /// (tick, body, field), or that they are identical. Also reports the first differing input.
    /// </summary>
    public static RagdollRecordingDifference Compare(string pathA, string pathB) => Compare(pathA, pathB, null);

    /// <param name="isComparableInput">Which input records to compare; null for the default (every input except
    /// the header, the end, notes and wall-clock stamps). The offline replay feeds the recorded clock stamps back,
    /// so it compares those too.</param>
    public static RagdollRecordingDifference Compare(string pathA, string pathB, Func<RecordType, bool>? isComparableInput)
        => Compare(pathA, pathB, isComparableInput == null ? null : record => isComparableInput(record.Type), null);

    /// <param name="isComparableInput">Which input records to compare, record by record (applied to both files);
    /// null for the default.</param>
    /// <param name="inputsEqual">When two compared input records count as equal; null for identical content. A
    /// replay of an older format uses it to compare a record the newer format extended (by its common prefix).</param>
    public static RagdollRecordingDifference Compare(string pathA, string pathB, Func<RagdollRecord, bool>? isComparableInput,
        Func<RagdollRecord, RagdollRecord, bool>? inputsEqual)
    {
        isComparableInput ??= record => IsComparableInput(record.Type);
        inputsEqual ??= (x, y) => x.ContentEquals(y);
        var result = new RagdollRecordingDifference();
        using var a = RagdollRecordingReader.Open(pathA);
        using var b = RagdollRecordingReader.Open(pathB);
        var inputsA = new Queue<RagdollRecord>();
        var inputsB = new Queue<RagdollRecord>();
        long inputIndex = 0;
        var names = new Dictionary<int, string>();
        var tick = 0;
        var frame = -1;

        void DrainInputs()
        {
            while (result.InputDifference == null && inputsA.Count > 0 && inputsB.Count > 0)
            {
                var x = inputsA.Dequeue();
                var y = inputsB.Dequeue();
                inputIndex++;
                if (!inputsEqual(x, y))
                    result.InputDifference =
                        $"input record #{inputIndex} differs (after tick {tick}, physics frame {frame}): " +
                        $"A = {Clip(x.ToString())} | B = {Clip(y.ToString())}";
            }
            if (result.InputDifference != null) { inputsA.Clear(); inputsB.Clear(); }
        }

        RagdollRecord? NextOutput(RagdollRecordingReader reader, Queue<RagdollRecord> inputs)
        {
            while (reader.Read() is { } record)
            {
                if (RagdollRecordingFormat.IsOutput(record.Type)) return record;
                if (isComparableInput(record) && result.InputDifference == null) inputs.Enqueue(record);
            }
            return null;
        }

        while (true)
        {
            var x = NextOutput(a, inputsA);
            var y = NextOutput(b, inputsB);
            DrainInputs();
            if (x == null && y == null)
            {
                if (result.InputDifference == null && inputsA.Count != inputsB.Count)
                    result.InputDifference = $"one recording has {Math.Abs(inputsA.Count - inputsB.Count)} more input record(s) at the end";
                result.Identical = true;
                result.Description = $"identical: {result.OutputRecordsCompared} output records compared, last tick {tick}" +
                                     (a.Truncated || b.Truncated ? " (a file was truncated)" : string.Empty);
                return result;
            }
            if (x == null || y == null)
            {
                var longer = x ?? y!;
                result.Description =
                    $"recording {(x == null ? "A" : "B")} ends after {result.OutputRecordsCompared} output records (tick {tick}, physics frame {frame}); " +
                    $"the other continues with {longer.Type}";
                return result;
            }

            if (x.Type == RecordType.RigBodies) ReadNames(x, names);
            if (x.Type == RecordType.BodyStates) { tick = x[0].AsInt; frame = x[1].AsInt; }
            if (x.Type == RecordType.FrameEnd) frame = x[0].AsInt;

            if (x.Type != y.Type)
            {
                result.Description =
                    $"output record #{result.OutputRecordsCompared + 1} (tick {tick}, physics frame {frame}): A is {x.Type}, B is {y.Type}";
                return result;
            }
            if (!x.ContentEquals(y))
            {
                result.Description =
                    $"output record #{result.OutputRecordsCompared + 1}, {x.Type}, tick {tick}, physics frame {frame}: " +
                    DescribeDifference(x, y, names);
                return result;
            }
            result.OutputRecordsCompared++;
        }
    }

    // Records that are inputs of the simulation and expected to be equal in two runs of the same
    // recording. The header and wall-clock stamps are not.
    private static bool IsComparableInput(RecordType type) => type is not
        (RecordType.Header or RecordType.End or RecordType.Note or RecordType.Clock);

    private static void ReadNames(RagdollRecord rig, Dictionary<int, string> names)
    {
        names.Clear();
        // count, then per body: name, skeletonIndex, handle, List(n) + n values.
        var at = 1;
        var count = rig[0].AsInt;
        for (var i = 0; i < count && at + 3 < rig.Values.Length; i++)
        {
            var name = rig[at].AsString ?? "?";
            var handle = rig[at + 2].AsInt;
            names[handle] = name;
            at += 3;
            if (rig[at].Tag == ValueTag.List) at += 1 + rig[at].Count;
        }
    }

    private static string DescribeDifference(RagdollRecord x, RagdollRecord y, Dictionary<int, string> names)
    {
        if (x.Type == RecordType.BodyStates)
        {
            var handlesA = x[4];
            var handlesB = y[4];
            if (!handlesA.Equals(handlesB))
                return $"the sets of bodies differ ({handlesA.Count} vs {handlesB.Count} bodies)";
            var stateA = x[6];
            var stateB = y[6];
            var words = Math.Min(stateA.Count, stateB.Count);
            for (var i = 0; i < words; i++)
            {
                if (stateA.WordAt(i) == stateB.WordAt(i)) continue;
                var body = i / RagdollRecordingFormat.BodyStateFloats;
                var handle = body < handlesA.Count ? handlesA.IntAt(body) : -1;
                var field = RagdollRecordingFormat.BodyStateFieldNames[i % RagdollRecordingFormat.BodyStateFloats];
                var name = names.TryGetValue(handle, out var known) ? known : "(not a ragdoll bone)";
                return $"body handle {handle} {name}, field {field}: A = {Float(stateA, i)} B = {Float(stateB, i)}";
            }
            var flagsA = x[5];
            var flagsB = y[5];
            for (var i = 0; i < Math.Min(flagsA.Data.Length, flagsB.Data.Length); i++)
                if (flagsA.Data[i] != flagsB.Data[i])
                {
                    var handle = i < handlesA.Count ? handlesA.IntAt(i) : -1;
                    var name = names.TryGetValue(handle, out var known) ? known : "(not a ragdoll bone)";
                    return $"body handle {handle} {name}, field flags (bit 0 awake, bit 1 kinematic): A = {flagsA.Data[i]} B = {flagsB.Data[i]}";
                }
        }

        var count = Math.Min(x.Values.Length, y.Values.Length);
        if (x.Type == RecordType.PoseOut)
        {
            // actor, purpose, available, position, rotation, scale, drawScaleValid, drawScale, partialCount,
            // then per partial: index, state, [pose block].
            const int firstPartial = 9;
            for (var i = 0; i < Math.Min(firstPartial, count); i++)
                if (!x.Values[i].Equals(y.Values[i]))
                    return $"field {PoseHeaderNames[i]}: A = {Clip(x.Values[i].ToString())} B = {Clip(y.Values[i].ToString())}";
            var at = firstPartial;
            while (at + 1 < count)
            {
                var partial = x.Values[at].AsInt;
                if (!x.Values[at].Equals(y.Values[at]))
                    return $"partial skeleton index: A = {partial} B = {y.Values[at].AsInt}";
                var stateA = x.Values[at + 1].AsInt;
                var stateB = y.Values[at + 1].AsInt;
                if (stateA != stateB)
                    return $"partial skeleton {partial}, field state: A = {stateA} B = {stateB}";
                at += 2;
                if ((stateA & 3) != 0) continue;
                if (at >= count) break;
                var va = x.Values[at];
                var vb = y.Values[at];
                at++;
                if (va.Equals(vb)) continue;
                var words = Math.Min(va.Data.Length, vb.Data.Length) / 4;
                for (var w = 0; w < words; w++)
                {
                    if (va.WordAt(w) == vb.WordAt(w)) continue;
                    return $"partial skeleton {partial}, bone {w / 12}, field {RagdollRecordingFormat.PoseFieldNames[w % 12]}: " +
                           $"A = {Float(va, w)} B = {Float(vb, w)}";
                }
                return $"partial skeleton {partial}: pose blocks have different sizes ({va.Data.Length} vs {vb.Data.Length} bytes)";
            }
        }

        for (var i = 0; i < count; i++)
        {
            var va = x.Values[i];
            var vb = y.Values[i];
            if (va.Equals(vb)) continue;
            return $"value #{i}: A = {Clip(va.ToString())} B = {Clip(vb.ToString())}";
        }
        return $"different number of values ({x.Values.Length} vs {y.Values.Length})";
    }

    private static readonly string[] PoseHeaderNames =
    {
        "actor", "purpose", "available", "skeleton position", "skeleton rotation", "skeleton scale",
        "draw scale valid", "draw scale", "partial skeleton count",
    };

    private static string Float(RecValue value, int word) =>
        value.FloatAt(word).ToString("R", CultureInfo.InvariantCulture) +
        " (0x" + value.WordAt(word).ToString("X8", CultureInfo.InvariantCulture) + ")";

    private static string Clip(string text) => text.Length <= 240 ? text : text[..240] + "...";
}
