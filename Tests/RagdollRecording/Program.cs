// Offline reader and checker for ragdoll recordings (docs/REFACTOR_PLAN.md, stage A0). See README.md.

using System.Globalization;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using CombatSimulator.Animation.Recording;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

if (args.Length >= 2 && args[0] == "dump")
    return Dump(args[1], args.Length > 2 ? long.Parse(args[2]) : 0, args.Length > 3 ? long.Parse(args[3]) : 200);
if (args.Length == 1)
{
    Console.Write(RagdollRecordingAnalysis.Summarize(args[0]).Describe());
    return 0;
}
if (args.Length == 2)
{
    var difference = RagdollRecordingAnalysis.Compare(args[0], args[1]);
    Console.WriteLine(difference.Identical ? difference.Description : "DIFFERENT: " + difference.Description);
    if (difference.InputDifference != null) Console.WriteLine("inputs: " + difference.InputDifference);
    else Console.WriteLine("inputs: no difference found in the part compared");
    return difference.Identical ? 0 : 2;
}

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

var work = Path.Combine(Path.GetTempPath(), "ragdoll-recording-check-" + Environment.ProcessId);
Directory.CreateDirectory(work);
try
{
    ValueRoundTrip();
    Truncation();
    PartialRecords();
    SizeLimits();
    ConfigurationWatch();
    SimulationRecordings();
}
finally
{
    try { Directory.Delete(work, true); } catch { /* leave the temp files */ }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

// ---- 1. every value type survives a round trip, bit for bit ----
void ValueRoundTrip()
{
    var specials = new[]
    {
        0f, -0f, 1f, -1f, float.Epsilon, -float.Epsilon, float.MaxValue, float.MinValue,
        float.PositiveInfinity, float.NegativeInfinity, float.NaN,
        BitConverter.Int32BitsToSingle(0x7FC12345), // quiet NaN with a payload
        BitConverter.Int32BitsToSingle(unchecked((int)0xFFA00001)), // negative NaN, different payload
        BitConverter.Int32BitsToSingle(0x00000123), // denormal
        0.1f, 1f / 3f, MathF.PI,
    };
    var doubles = new[] { 0d, -0d, double.Epsilon, double.NaN, BitConverter.Int64BitsToDouble(0x7FF8000000ABCDEF), 1d / 3d, double.MaxValue };

    var original = new MemoryStream();
    var expected = new List<(RecordType Type, List<RecValue> Values)>();
    using (var writer = new RagdollRecordingWriter(new NonClosing(original)))
    {
        var random = new Random(12345);
        for (var record = 0; record < 200; record++)
        {
            var type = (RecordType)(1 + record % 90);
            var values = new List<RecValue>();
            using (writer.Begin(type))
            {
                var count = record == 0 ? 0 : random.Next(1, 40);
                for (var i = 0; i < count; i++)
                {
                    var f = specials[random.Next(specials.Length)];
                    var g = specials[random.Next(specials.Length)];
                    var h = specials[random.Next(specials.Length)];
                    var k = specials[random.Next(specials.Length)];
                    switch (random.Next(17))
                    {
                        case 0: writer.Null(); values.Add(new RecValue(ValueTag.Null, 0)); break;
                        case 1: { var v = random.Next(2) == 0; writer.Bool(v); values.Add(new RecValue(ValueTag.Bool, v ? 1u : 0u)); break; }
                        case 2: { var v = random.Next(int.MinValue, int.MaxValue); writer.I32(v); values.Add(new RecValue(ValueTag.I32, (uint)v)); break; }
                        case 3: { var v = (uint)random.NextInt64(0, uint.MaxValue + 1L); writer.U32(v); values.Add(new RecValue(ValueTag.U32, v)); break; }
                        case 4: { var v = random.NextInt64(long.MinValue, long.MaxValue); writer.I64(v); values.Add(new RecValue(ValueTag.I64, (ulong)v)); break; }
                        case 5: { var v = (ulong)random.NextInt64(long.MinValue, long.MaxValue); writer.U64(v); values.Add(new RecValue(ValueTag.U64, v)); break; }
                        case 6: writer.F32(f); values.Add(new RecValue(ValueTag.F32, (uint)BitConverter.SingleToInt32Bits(f))); break;
                        case 7: { var d = doubles[random.Next(doubles.Length)]; writer.F64(d); values.Add(new RecValue(ValueTag.F64, (ulong)BitConverter.DoubleToInt64Bits(d))); break; }
                        case 8:
                        {
                            var s = random.Next(4) switch { 0 => string.Empty, 1 => "j_kosi", 2 => "骨盆 ünïcödé \0 end", _ => new string('x', random.Next(1, 300)) };
                            writer.Str(s);
                            values.Add(new RecValue(ValueTag.String, 0, System.Text.Encoding.UTF8.GetBytes(s)));
                            break;
                        }
                        case 9: writer.V2(new Vector2(f, g)); values.Add(new RecValue(ValueTag.Vector2, 0, Bits(f, g))); break;
                        case 10: writer.V3(new Vector3(f, g, h)); values.Add(new RecValue(ValueTag.Vector3, 0, Bits(f, g, h))); break;
                        case 11: writer.Quat(new Quaternion(f, g, h, k)); values.Add(new RecValue(ValueTag.Quaternion, 0, Bits(f, g, h, k))); break;
                        case 12:
                        {
                            var bytes = new byte[random.Next(0, 200)];
                            random.NextBytes(bytes);
                            writer.Bytes(bytes);
                            values.Add(new RecValue(ValueTag.Bytes, 0, bytes));
                            break;
                        }
                        case 13:
                        {
                            var array = new float[random.Next(0, 60)];
                            for (var j = 0; j < array.Length; j++) array[j] = specials[random.Next(specials.Length)];
                            writer.Floats(array);
                            values.Add(new RecValue(ValueTag.F32Array, 0, Bits(array)));
                            break;
                        }
                        case 14:
                        {
                            var array = new int[random.Next(0, 60)];
                            for (var j = 0; j < array.Length; j++) array[j] = random.Next(int.MinValue, int.MaxValue);
                            writer.Ints(array);
                            var bytes = new byte[array.Length * 4];
                            Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);
                            values.Add(new RecValue(ValueTag.I32Array, 0, bytes));
                            break;
                        }
                        case 15:
                        {
                            var array = new Vector3[random.Next(0, 30)];
                            var flat = new float[array.Length * 3];
                            for (var j = 0; j < flat.Length; j++) flat[j] = specials[random.Next(specials.Length)];
                            for (var j = 0; j < array.Length; j++) array[j] = new Vector3(flat[j * 3], flat[j * 3 + 1], flat[j * 3 + 2]);
                            writer.V3s(array);
                            values.Add(new RecValue(ValueTag.Vector3Array, 0, Bits(flat)));
                            break;
                        }
                        default:
                        {
                            var array = new Quaternion[random.Next(0, 20)];
                            var flat = new float[array.Length * 4];
                            for (var j = 0; j < flat.Length; j++) flat[j] = specials[random.Next(specials.Length)];
                            for (var j = 0; j < array.Length; j++) array[j] = new Quaternion(flat[j * 4], flat[j * 4 + 1], flat[j * 4 + 2], flat[j * 4 + 3]);
                            writer.Quats(array);
                            values.Add(new RecValue(ValueTag.QuaternionArray, 0, Bits(flat)));
                            break;
                        }
                    }
                }
                if (record % 7 == 3) { writer.List(2); writer.Str("a"); writer.I32(5); values.Add(new RecValue(ValueTag.List, 2)); values.Add(new RecValue(ValueTag.String, 0, "a"u8.ToArray())); values.Add(new RecValue(ValueTag.I32, 5)); }
            }
            expected.Add((type, values));
        }
        Check(!writer.Failed && writer.RecordCount == 200, "writer: 200 synthetic records written without error");
        Check(writer.BytesWritten == original.Length, $"writer: byte count matches the stream ({original.Length} bytes)");
    }

    var decoded = new List<RagdollRecord>();
    original.Position = 0;
    using (var reader = new RagdollRecordingReader(new NonClosing(original)))
    {
        while (reader.Read() is { } record) decoded.Add(record);
        Check(!reader.Truncated, "reader: reached the end of the file cleanly");
    }

    var equal = decoded.Count == expected.Count;
    var valueCount = 0;
    for (var i = 0; equal && i < decoded.Count; i++)
    {
        equal = decoded[i].Type == expected[i].Type && decoded[i].Values.Length == expected[i].Values.Count;
        for (var j = 0; equal && j < decoded[i].Values.Length; j++, valueCount++)
            equal = decoded[i].Values[j].Equals(expected[i].Values[j]);
    }
    Check(equal, $"reader: {decoded.Count} records, {valueCount} values equal to what was written, bit for bit");

    // Accessors give the exact floats back (the sign of zero and NaN payloads included).
    var probe = new MemoryStream();
    using (var writer = new RagdollRecordingWriter(new NonClosing(probe)))
    using (writer.Begin(RecordType.Note))
    {
        foreach (var f in specials) writer.F32(f);
        writer.Floats(specials);
        writer.V3(new Vector3(specials[1], specials[11], specials[13]));
    }
    probe.Position = 0;
    using (var reader = new RagdollRecordingReader(new NonClosing(probe)))
    {
        var record = reader.Read()!;
        var ok = true;
        for (var i = 0; i < specials.Length; i++)
        {
            ok &= BitConverter.SingleToInt32Bits(record[i].AsFloat) == BitConverter.SingleToInt32Bits(specials[i]);
            ok &= BitConverter.SingleToInt32Bits(record[specials.Length].FloatAt(i)) == BitConverter.SingleToInt32Bits(specials[i]);
        }
        var v = record[specials.Length + 1];
        ok &= v.WordAt(0) == 0x80000000u && v.WordAt(1) == 0x7FC12345u && v.WordAt(2) == 0x00000123u;
        Check(ok, "reader: float accessors return the exact bits (-0, NaN payloads, denormals, infinities)");
    }

    // Re-encoding the decoded records reproduces the file byte for byte.
    var copy = new MemoryStream();
    using (var writer = new RagdollRecordingWriter(new NonClosing(copy)))
        foreach (var record in decoded)
            writer.Record(record);
    Check(original.ToArray().AsSpan().SequenceEqual(copy.ToArray()), "re-encoding the decoded records reproduces the original bytes");
}

// ---- 2. a file cut short is readable up to its last complete record ----
void Truncation()
{
    var stream = new MemoryStream();
    using (var writer = new RagdollRecordingWriter(new NonClosing(stream)))
        for (var i = 0; i < 10; i++)
            using (writer.Begin(RecordType.Note))
            {
                writer.I32(i);
                writer.Str("record " + i);
                writer.Floats(new float[32]);
            }
    var bytes = stream.ToArray();
    var perRecord = (bytes.Length - 8) / 10;
    var cut = new MemoryStream(bytes, 0, 8 + perRecord * 6 + perRecord / 2);
    using var reader = new RagdollRecordingReader(cut);
    var read = 0;
    while (reader.Read() is { } record) if (record[0].AsInt == read) read++;
    Check(read == 6 && reader.Truncated, "a file cut inside the 7th record yields 6 records and is reported as truncated");

    var bad = false;
    try { using var wrong = new RagdollRecordingReader(new MemoryStream(new byte[64])); }
    catch (InvalidDataException) { bad = true; }
    Check(bad, "a file that is not a recording is rejected");
}

// ---- 3. a record whose body throws half-way is not written; a note stands in its place ----
void PartialRecords()
{
    var stream = new MemoryStream();
    var writer = new RagdollRecordingWriter(new NonClosing(stream));
    using (writer.Begin(RecordType.Note)) writer.Str("before");

    // The way the recorder does it: an exception filter marks the open record before its scope is disposed.
    var caught = false;
    try
    {
        using (writer.Begin(RecordType.BodyStates))
        {
            writer.I32(7);
            writer.Floats(new float[13]);
            throw new InvalidOperationException("thrown half-way through a record body");
        }
    }
    catch (InvalidOperationException) when (Abort(writer))
    {
        caught = true;
    }

    // An exception that was caught before the record began must not discard the next record.
    try { throw new InvalidOperationException("outside any record"); }
    catch (InvalidOperationException) when (Abort(writer)) { }
    using (writer.Begin(RecordType.FrameEnd)) { writer.I32(1); writer.I32(2); }

    // The scope that never reaches its caller (RecCall): aborted and disposed by hand, the lock is free again.
    var scope = writer.Begin(RecordType.ApiCall);
    writer.I32(0);
    writer.AbortRecord();
    scope.Dispose();
    var lockFree = Task.Run(() => { using (writer.Begin(RecordType.Note)) writer.Str("from another thread"); }).Wait(5000);
    writer.Dispose();

    var records = new List<RagdollRecord>();
    stream.Position = 0;
    using (var reader = new RagdollRecordingReader(new NonClosing(stream)))
    {
        while (reader.Read() is { } record) records.Add(record);
        Check(!reader.Truncated, "partial records: the file reads to its end cleanly");
    }
    Check(caught && lockFree, "partial records: the exception reached its handler and the writer's lock was released");
    Check(writer.DiscardedRecords == 2 && writer.RecordCount == 5 && writer.BytesWritten == stream.Length,
        $"partial records: writer counts 2 discarded, 5 written, {stream.Length} bytes");
    Check(records.Count == 5 &&
          records[0].Type == RecordType.Note && records[0][0].AsString == "before" &&
          RagdollRecordingAnalysis.IsDiscardedRecord(records[1]) && records[1][0].AsString!.Contains("BodyStates (52), 62 payload bytes") &&
          records[2].Type == RecordType.FrameEnd && records[2].Values.Length == 2 && records[2][1].AsInt == 2 &&
          RagdollRecordingAnalysis.IsDiscardedRecord(records[3]) && records[3][0].AsString!.Contains("ApiCall (80)") &&
          records[4][0].AsString == "from another thread",
        "partial records: a note names each discarded record (BodyStates, ApiCall); the records around them are whole");
    Check(!records.Any(r => r.Type is RecordType.BodyStates or RecordType.ApiCall),
        "partial records: no BodyStates or ApiCall record in the file, so a reader cannot mistake a half-built one for a whole one");

    static bool Abort(RagdollRecordingWriter writer) { writer.AbortRecord(); return true; }
}

// ---- 4. size limits are enforced record by record; the file stays well-formed ----
void SizeLimits()
{
    static void Payload(RagdollRecordingWriter writer, int index)
    {
        using (writer.Begin(RecordType.Note)) { writer.I32(index); writer.Bytes(new byte[1000]); }
    }
    static void End(RagdollRecordingWriter writer)
    {
        using (writer.Begin(RecordType.End)) { writer.Str("size limit"); writer.I32(0); writer.I32(0); writer.I32(0); writer.I64(writer.RecordCount + 1); writer.F64(0); }
    }
    static (int Notes, bool End, bool Truncated, bool InOrder) ReadBack(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new RagdollRecordingReader(new NonClosing(stream));
        var notes = 0;
        var end = false;
        var inOrder = true;
        while (reader.Read() is { } record)
        {
            if (record.Type == RecordType.End) end = true;
            else { inOrder &= !end && record[0].AsInt == notes; notes++; }
        }
        return (notes, end, reader.Truncated, inOrder);
    }

    // One file with its own limit.
    const long fileLimit = 10_000;
    var single = new MemoryStream();
    var writer = new RagdollRecordingWriter(new NonClosing(single), null, fileLimit);
    for (var i = 0; i < 50; i++) Payload(writer, i);
    var beforeEnd = single.Length;
    End(writer);
    Payload(writer, 99); // refused: nothing follows the End record either
    writer.Dispose();
    var read = ReadBack(single);
    Check(writer.FileLimitReached && writer.LimitReached && beforeEnd <= fileLimit && beforeEnd > fileLimit - 1100,
        $"file limit: writing stopped at {beforeEnd} bytes, within the {fileLimit}-byte limit");
    Check(read.Notes == 9 && read.End && !read.Truncated && read.InOrder && single.Length - beforeEnd < 1024 && writer.BytesWritten == single.Length,
        $"file limit: 9 whole records and the End record ({single.Length - beforeEnd} bytes) read back, nothing after the refused record");

    // Two files sharing one session budget: the first refusal stops both.
    const long sessionLimit = 30_000;
    var budget = new RagdollRecordingBudget(sessionLimit);
    var a = new MemoryStream();
    var b = new MemoryStream();
    var first = new RagdollRecordingWriter(new NonClosing(a), budget, 1_000_000);
    var second = new RagdollRecordingWriter(new NonClosing(b), budget, 1_000_000);
    for (var i = 0; i < 50; i++) { Payload(first, i); Payload(second, i); }
    var total = a.Length + b.Length;
    End(first);
    End(second);
    first.Dispose();
    second.Dispose();
    var readA = ReadBack(a);
    var readB = ReadBack(b);
    Check(budget.Exhausted && first.LimitReached && second.LimitReached && !first.FileLimitReached && total <= sessionLimit && total > sessionLimit - 1100,
        $"session limit: two files stopped at {total} bytes in all, within the {sessionLimit}-byte limit");
    Check(readA.End && readB.End && !readA.Truncated && !readB.Truncated && readA.InOrder && readB.InOrder &&
          readA.Notes + readB.Notes == 29 && budget.Used == a.Length + b.Length,
        $"session limit: both files closed with an End record ({readA.Notes} + {readB.Notes} whole records), the budget counts every byte written ({budget.Used})");
}

// ---- 5. watching configuration for changes costs no allocation while nothing changes ----
void ConfigurationWatch()
{
    var settings = new WatchedSettings();
    var properties = typeof(WatchedSettings).GetProperties();
    var watches = properties.Select(RagdollRecordingWatch.For).ToArray();
    var fast = watches.Where(w => w.AllocationFree).ToArray();
    Check(fast.Length == properties.Length - 1 && !watches[Array.FindIndex(properties, p => p.Name == nameof(WatchedSettings.Other))].AllocationFree,
        $"watch: {fast.Length} of {properties.Length} property kinds are watched without reflection (bool, int, float, enum, string, nullable, computed, string list, settings object)");

    var firstLook = watches.Count(w => w.Changed(settings)) == watches.Length;
    var secondLook = watches.Count(w => w.Changed(settings));
    Check(firstLook && secondLook == 0, "watch: the first look reports every property, the second none");

    for (var i = 0; i < 1000; i++) foreach (var watch in fast) watch.Changed(settings); // warm up
    var before = GC.GetAllocatedBytesForCurrentThread();
    var spurious = 0;
    for (var i = 0; i < 10_000; i++)
        foreach (var watch in fast)
            if (watch.Changed(settings)) spurious++;
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocated == 0 && spurious == 0, $"watch: 10000 looks at {fast.Length} unchanged properties allocate {allocated} bytes");

    string Changed()
    {
        var names = new List<string>();
        for (var i = 0; i < watches.Length; i++)
            if (watches[i].Changed(settings)) names.Add(properties[i].Name);
        return string.Join(",", names);
    }
    var seen = new List<string>();
    settings.Flag = true; seen.Add(Changed());
    settings.Ratio = -0.25f; seen.Add(Changed());
    settings.Ratio = float.NaN; seen.Add(Changed()); seen.Add(Changed()); // NaN stays NaN: no change the second time
    settings.Mode = DayOfWeek.Friday; seen.Add(Changed());
    settings.Text = "iv_"; seen.Add(Changed());
    settings.Optional = 2f; seen.Add(Changed());
    settings.Names.Add("j_asi_a_l"); seen.Add(Changed());
    settings.Names[0] = "j_kao"; seen.Add(Changed());
    settings.Names = new List<string>(settings.Names); seen.Add(Changed()); // another list, same items: no change
    settings.Nested.Inner.Depth = 3; seen.Add(Changed());
    settings.Nested = new WatchedNested { Force = 46f, Inner = new WatchedInner { Depth = 3 } }; seen.Add(Changed()); // another object, same values
    settings.Nested.Force = 47f; seen.Add(Changed());
    settings.Nested = null!; seen.Add(Changed());
    settings.Other = new[] { 1, 2 }; seen.Add(Changed());
    settings.Count = 500; seen.Add(Changed());
    var expected = "Flag,Computed|Ratio|Ratio||Mode|Text|Optional|Names|Names||Nested||Nested|Nested|Other|Count";
    Check(string.Join("|", seen) == expected, "watch: each change is reported once, for the property that changed: " + string.Join("|", seen));

    var copy = watches[0].Clone();
    Check(copy.Changed(settings) && !copy.Changed(settings) && !watches[0].Changed(settings), "watch: a clone remembers nothing of the original and does not disturb it");
}

// ---- 6. a real BepuPhysics simulation recorded twice; a changed bit is found ----
void SimulationRecordings()
{
    var a = Path.Combine(work, "a" + RagdollRecordingFormat.FileExtension);
    var b = Path.Combine(work, "b" + RagdollRecordingFormat.FileExtension);
    var bodyChanged = Path.Combine(work, "body" + RagdollRecordingFormat.FileExtension);
    var poseChanged = Path.Combine(work, "pose" + RagdollRecordingFormat.FileExtension);
    var inputChanged = Path.Combine(work, "input" + RagdollRecordingFormat.FileExtension);
    const int frames = 120;
    RecordSimulation(a, frames, Change.None);
    RecordSimulation(b, frames, Change.None);
    RecordSimulation(bodyChanged, frames, Change.Body);
    RecordSimulation(poseChanged, frames, Change.Pose);
    RecordSimulation(inputChanged, frames, Change.Input);

    var summary = RagdollRecordingAnalysis.Summarize(a);
    Check(!summary.Truncated && summary.Header != null && summary.End != null, "summary: header and end records present, not truncated");
    Check(summary.RenderFrames == frames && summary.PhysicsFrames == frames, $"summary: {frames} render frames and physics frames");
    Check(summary.FixedSteps == frames && summary.Bodies == 3 && summary.RigBodies == 2, "summary: one fixed step per frame, 3 bodies, 2 ragdoll bones");
    Check(summary.WorldQueries == frames && summary.ConfigProperties == 2, "summary: one world query per frame, 2 configuration properties");
    Check(Math.Abs(summary.Seconds - frames / 60.0) < 1e-3, $"summary: duration {summary.Seconds:F3} s");
    Check(summary.Header![2].AsBool == false && summary.Header[4].AsString == RagdollRecordingBepu.BepuVersion(),
        $"summary: header carries the build flags and BepuPhysics {RagdollRecordingBepu.BepuVersion()}");

    using (var reader = RagdollRecordingReader.Open(a))
    {
        var activate = reader.ReadAll().First(r => r.Type == RecordType.Activate);
        var at = RagdollRecordingFormat.ActivateCarriedStateIndex(activate);
        Check(reader.FormatVersion == RagdollRecordingFormat.Version && RagdollRecordingFormat.Version == 3 && summary.FormatVersion == 3,
            "format version 3 in the header, the reader and the summary");
        Check(at == 30 && activate[at].Tag == ValueTag.U32 && activate[at].AsUInt == 0x1234ABCDu && activate[at + 1].AsInt == 7 && activate[at + 2].AsInt == 3 &&
              activate[at + 3].Count == 2 && activate[at + 4].AsInt == 11 && activate[at + 5].AsInt == 12 &&
              activate[at + 6].Count == 1 && activate[at + 7].AsULong == 0x99 &&
              activate[at + 8].AsInt == 1 && activate[at + 9].AsULong == 0x77 && activate[at + 19].AsBool,
            "Activate: the carried-over state (impact generator, next grab slot, body generation, struck bodies, strike colliders, traversal requests) is found after the two lists");
    }
    Check(summary.CarriedState == "impactRandomState 0x1234ABCD, nextGrabSlotId 7, externalBodyGeneration 3, 2 body handle(s) already struck, " +
          "1 strike collider(s), 1 pending traversal proxy request(s)" && summary.Describe().Contains("carried over from earlier activations: impactRandomState 0x1234ABCD"),
        "summary: " + summary.CarriedState);
    var old = Path.Combine(work, "v1" + RagdollRecordingFormat.FileExtension);
    using (var writer = new RagdollRecordingWriter(new FileStream(old, FileMode.Create, FileAccess.Write)))
    {
        using (writer.Begin(RecordType.Header)) writer.I32(1);
        WriteActivate(writer, false);
    }
    var oldSummary = RagdollRecordingAnalysis.Summarize(old);
    Check(oldSummary.FormatVersion == 1 && oldSummary.CarriedState == null && oldSummary.Describe().Contains("NOT RECORDED (format version 1"),
        "summary: a version 1 file is reported as lacking the carried-over state");

    var same = RagdollRecordingAnalysis.Compare(a, b);
    Check(same.Identical && same.InputDifference == null, "two recordings of the same simulation: " + same.Description);

    var body = RagdollRecordingAnalysis.Compare(a, bodyChanged);
    Console.WriteLine("      " + body.Description);
    Check(!body.Identical && body.Description.Contains("tick 40") && body.Description.Contains("body handle 1 j_sebo_a") &&
          body.Description.Contains("field linear.y"), "one changed bit in a body state is reported with tick, body and field");

    var pose = RagdollRecordingAnalysis.Compare(a, poseChanged);
    Console.WriteLine("      " + pose.Description);
    Check(!pose.Identical && pose.Description.Contains("PoseOut") && pose.Description.Contains("physics frame 70") &&
          pose.Description.Contains("partial skeleton 1, bone 2, field rotation.w"),
        "one changed bit in a written-back bone is reported with frame, partial skeleton, bone and field");

    var input = RagdollRecordingAnalysis.Compare(a, inputChanged);
    Console.WriteLine("      " + (input.InputDifference ?? "(no input difference)"));
    Check(input.InputDifference != null && input.InputDifference.Contains("Raycast"), "a changed raycast answer is reported as the first differing input");
}

void RecordSimulation(string path, int frames, Change change)
{
    var pool = new BufferPool();
    var simulation = Simulation.Create(pool, new Contacts(), new Gravity(new Vector3(0, -9.8f, 0)), new SolveDescription(4, 2));
    simulation.Statics.Add(new StaticDescription(new Vector3(0, -0.5f, 0), Quaternion.Identity, simulation.Shapes.Add(new Box(50, 1, 50))));
    var capsule = new Capsule(0.1f, 0.4f);
    var shape = simulation.Shapes.Add(capsule);
    var inertia = capsule.ComputeInertia(5f);
    var pelvis = simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(new Vector3(0, 1.0f, 0)), inertia, new CollidableDescription(shape, 0.04f), new BodyActivityDescription(0.01f)));
    var chest = simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(new Vector3(0.05f, 1.6f, 0.02f), Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f)), inertia, new CollidableDescription(shape, 0.04f), new BodyActivityDescription(0.01f)));
    var hand = simulation.Bodies.Add(BodyDescription.CreateKinematic(new RigidPose(new Vector3(1, 0.3f, 0)), new CollidableDescription(shape, 0.04f), new BodyActivityDescription(0.01f)));
    simulation.Solver.Add(pelvis, chest, new BallSocket { LocalOffsetA = new Vector3(0, 0.3f, 0), LocalOffsetB = new Vector3(0, -0.3f, 0), SpringSettings = new SpringSettings(30, 1) });

    var handles = new int[4];
    var flags = new byte[4];
    var states = new float[4 * RagdollRecordingFormat.BodyStateFloats];
    using var writer = new RagdollRecordingWriter(new FileStream(path, FileMode.Create, FileAccess.Write));
    using (writer.Begin(RecordType.Header))
    {
        writer.I32(RagdollRecordingFormat.Version);
        writer.Str("synthetic");
        writer.Bool(false);
        writer.Bool(false);
        writer.Str(RagdollRecordingBepu.BepuVersion());
        writer.Str(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        writer.Str(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        writer.Str(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        writer.I32(Environment.ProcessorCount);
        writer.I32(Vector<float>.Count);
        writer.Str("synthetic");
        writer.Bool(BitConverter.IsLittleEndian);
        writer.I64(DateTime.UtcNow.Ticks);
        writer.I64(System.Diagnostics.Stopwatch.Frequency);
        writer.Str("self-check");
        writer.I32(1);
        writer.I32(1);
        writer.Bool(true);
        writer.U64(0x1234);
        writer.U32(0x10000001);
        writer.I32(0);
        writer.Bool(true);
    }
    using (writer.Begin(RecordType.ConfigSnapshot))
    {
        writer.List(4);
        writer.Str("RagdollMeshVolumes"); writer.Bool(false);
        writer.Str("RagdollActivationDelay"); writer.F32(1f);
    }
    WriteActivate(writer, true);
    using (writer.Begin(RecordType.RigBodies))
    {
        writer.I32(2);
        writer.Str("j_kosi"); writer.I32(1); writer.I32(pelvis.Value); writer.List(2); writer.Str("Mass"); writer.F32(5f);
        writer.Str("j_sebo_a"); writer.I32(2); writer.I32(chest.Value); writer.List(2); writer.Str("Mass"); writer.F32(5f);
    }
    RagdollRecordingBepu.WriteBodyStates(writer, simulation, 0, 0, 0, false, ref handles, ref flags, ref states);

    var pose = new byte[3 * RagdollRecordingFormat.PoseBoneBytes];
    for (var frame = 1; frame <= frames; frame++)
    {
        using (writer.Begin(RecordType.RenderFrame)) { writer.I32(frame); writer.F32(1f / 60f); writer.F32(frame / 60f); writer.Bool(true); writer.F32(0); writer.F32(0); writer.U64(0); }
        using (writer.Begin(RecordType.FrameBegin)) { writer.I32(frame); writer.F32(1f / 60f); writer.F32(0); writer.Bool(false); }
        using (writer.Begin(RecordType.Raycast))
        {
            writer.I32((int)RaycastSite.SkeletonMoved);
            writer.V3(new Vector3(0, 10, 0)); writer.V3(-Vector3.UnitY); writer.F32(80f); writer.Bool(true);
            writer.V3(new Vector3(0, change == Change.Input && frame == 30 ? 0.001f : 0f, 0));
        }

        // The kinematic body is driven like another actor's collider.
        var kinematic = simulation.Bodies.GetBodyReference(hand);
        kinematic.Velocity.Linear = new Vector3(-0.5f, 0, 0);
        kinematic.Awake = true;
        simulation.Timestep(1f / 60f);
        if (change == Change.Body && frame == 40)
        {
            ref var velocity = ref simulation.Bodies.GetBodyReference(chest).Velocity.Linear;
            velocity.Y = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(velocity.Y) ^ 1);
        }
        RagdollRecordingBepu.WriteBodyStates(writer, simulation, frame, frame, 0, false, ref handles, ref flags, ref states);
        using (writer.Begin(RecordType.FrameEnd)) { writer.I32(frame); writer.I32(1); writer.Bool(false); writer.F32(0); writer.F32(1); writer.I32(frame); }

        // "Write back": bone 0 and 1 follow the two dynamic bodies; a second partial skeleton has three bones.
        var p = simulation.Bodies.GetBodyReference(pelvis).Pose;
        var c = simulation.Bodies.GetBodyReference(chest).Pose;
        var main = new float[2 * 12];
        Fill(main, 0, p);
        Fill(main, 12, c);
        var mainBytes = new byte[main.Length * 4];
        Buffer.BlockCopy(main, 0, mainBytes, 0, mainBytes.Length);
        var second = new float[3 * 12];
        Fill(second, 0, p);
        Fill(second, 12, c);
        Fill(second, 24, c);
        if (change == Change.Pose && frame == 70)
            second[2 * 12 + 7] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(second[2 * 12 + 7]) ^ 1);
        Buffer.BlockCopy(second, 0, pose, 0, pose.Length);
        using (writer.Begin(RecordType.PoseOut))
        {
            writer.U64(0x1234); writer.I32((int)PosePurpose.FrameOut); writer.Bool(true);
            writer.V3(Vector3.Zero); writer.Quat(Quaternion.Identity); writer.V3(Vector3.One); writer.Bool(true); writer.V3(Vector3.One);
            writer.I32(3);
            writer.I32(0); writer.I32(0); writer.Bytes(mainBytes);
            writer.I32(1); writer.I32(0); writer.Bytes(pose);
            writer.I32(2); writer.I32(2);
        }
    }
    using (writer.Begin(RecordType.End)) { writer.Str("self-check"); writer.I32(frames); writer.I32(frames); writer.I32(frames); writer.I64(writer.RecordCount + 1); writer.F64(frames / 60.0); }
    simulation.Dispose();
    pool.Clear();
}

// An Activate record laid out as the plugin writes it (RagdollController.Recording.cs, RecBeginActivation).
static void WriteActivate(RagdollRecordingWriter writer, bool carriedState)
{
    using (writer.Begin(RecordType.Activate))
    {
        writer.U64(0x1234); writer.F32(1f); writer.I32(5); writer.F32(0f); writer.V3(new Vector3(1, 2, 3));
        writer.U32(0x10000001); writer.I32(0); writer.Bool(true); writer.Bool(true); writer.Bool(true);
        writer.Bool(false); writer.Bool(true); writer.Bool(false); writer.Bool(false);
        writer.Bool(false); writer.Str(string.Empty); writer.F32(0); writer.F32(0); writer.F32(0); writer.F32(0); writer.F32(0); writer.F32(0);
        writer.Bool(false); writer.Bool(false); writer.Bool(true);
        writer.List(2); writer.Str("j_ude_a_l"); writer.Str("j_asi_a_r");
        writer.List(1); writer.U64(0x55);
        if (!carriedState) return;
        writer.U32(0x1234ABCDu); writer.I32(7); writer.I32(3);
        writer.List(2); writer.I32(11); writer.I32(12);
        writer.List(1); writer.U64(0x99);
        writer.I32(1);
        writer.U64(0x77); writer.Bool(true); writer.V3(Vector3.Zero); writer.V3(Vector3.One); writer.F32(0.5f);
        writer.F32(1f); writer.F32(2f); writer.F32(0.6f); writer.F32(1f); writer.I32(4); writer.Bool(true);
    }
}

static void Fill(float[] target, int at, RigidPose pose)
{
    target[at] = pose.Position.X; target[at + 1] = pose.Position.Y; target[at + 2] = pose.Position.Z; target[at + 3] = 0;
    target[at + 4] = pose.Orientation.X; target[at + 5] = pose.Orientation.Y; target[at + 6] = pose.Orientation.Z; target[at + 7] = pose.Orientation.W;
    target[at + 8] = 1; target[at + 9] = 1; target[at + 10] = 1; target[at + 11] = 0;
}

static byte[] Bits(params float[] values)
{
    var bytes = new byte[values.Length * 4];
    Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
    return bytes;
}

static int Dump(string path, long first, long count)
{
    using var reader = RagdollRecordingReader.Open(path);
    long index = 0;
    while (reader.Read() is { } record)
    {
        if (index >= first && index < first + count)
            Console.WriteLine($"#{index} @{record.Offset} ({record.PayloadBytes} bytes) {record}");
        index++;
        if (index >= first + count) break;
    }
    if (reader.Truncated) Console.WriteLine("(the file is truncated)");
    return 0;
}

enum Change { None, Body, Pose, Input }

// One property of every kind the plugin's configuration holds, for the watch check.
sealed class WatchedSettings
{
    public bool Flag { get; set; }
    public int Count { get; set; } = 5;
    public float Ratio { get; set; } = 0.5f;
    public DayOfWeek Mode { get; set; }
    public string Text { get; set; } = "iv_, ya_";
    public float? Optional { get; set; }
    public bool Computed => Flag || Count > 100;
    public List<string> Names { get; set; } = new() { "j_kosi" };
    public WatchedNested Nested { get; set; } = new();
    public int[] Other { get; set; } = { 1 };
}

sealed class WatchedNested
{
    public bool Enabled { get; set; }
    public float Force { get; set; } = 46f;
    public WatchedInner Inner { get; set; } = new();
}

sealed class WatchedInner
{
    public int Depth { get; set; }
}

/// <summary>Lets a writer or reader be disposed without closing the memory stream under test.</summary>
sealed class NonClosing(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
}

struct Contacts : INarrowPhaseCallbacks
{
    public void Initialize(Simulation simulation) { }
    public bool AllowContactGeneration(int worker, CollidableReference a, CollidableReference b, ref float margin)
        => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
    public bool AllowContactGeneration(int worker, CollidablePair pair, int a, int b) => true;
    public bool ConfigureContactManifold<T>(int worker, CollidablePair pair, ref T manifold, out PairMaterialProperties material) where T : unmanaged, IContactManifold<T>
    {
        material.FrictionCoefficient = 1f;
        material.MaximumRecoveryVelocity = 2f;
        material.SpringSettings = new SpringSettings(30, 1);
        return true;
    }
    public bool ConfigureContactManifold(int worker, CollidablePair pair, int a, int b, ref ConvexContactManifold manifold) => true;
    public void Dispose() { }
}

struct Gravity(Vector3 gravity) : IPoseIntegratorCallbacks
{
    private Vector3Wide gravityDt;
    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;
    public void Initialize(Simulation simulation) { }
    public void PrepareForIntegration(float dt) => gravityDt = Vector3Wide.Broadcast(gravity * dt);
    public void IntegrateVelocity(Vector<int> indices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide inertia,
        Vector<int> mask, int worker, Vector<float> dt, ref BodyVelocityWide velocity)
        => velocity.Linear += gravityDt;
}
