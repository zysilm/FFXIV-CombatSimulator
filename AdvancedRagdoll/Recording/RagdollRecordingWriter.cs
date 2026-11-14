// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CombatSimulator.Animation.Recording;

/// <summary>
/// Writes a ragdoll recording (see <see cref="RagdollRecordingFormat"/>). A record is built between
/// <see cref="Begin"/> and the disposal of the scope it returns:
/// <code>using (writer.Begin(RecordType.Note)) writer.Str("text");</code>
/// The writer holds a lock for the duration of a record, so records from different threads never
/// interleave. It never throws into the caller: an I/O failure marks it <see cref="Failed"/> and
/// later records are dropped.
///
/// A record whose body did not run to its end is never written as if it were whole: call
/// <see cref="AbortRecord"/> while the record is open (the recorder does so from an exception filter)
/// and the scope writes a <see cref="RecordType.Note"/> naming the discarded record instead.
///
/// Size limits are enforced here, per record: a record that would take the file past
/// <see cref="ByteLimit"/>, or the shared <see cref="RagdollRecordingBudget"/> past its limit, is not
/// written, and neither is anything after it except the closing <see cref="RecordType.End"/> record,
/// so a file never has a hole in the middle.
/// </summary>
public sealed class RagdollRecordingWriter : IDisposable
{
    private const int HeaderBytes = 6;

    private readonly object gate = new();
    private readonly long[] countByType = new long[256];
    private Stream? stream;
    private byte[] buffer = new byte[64 * 1024];
    private int length;
    private readonly RagdollRecordingBudget? budget;
    private RecordType current;
    private bool open;
    private bool aborted;

    /// <param name="budget">Bytes shared with other writers (one recording session), or null for no shared limit.</param>
    /// <param name="byteLimit">The most this file may hold, not counting its closing End record.</param>
    public RagdollRecordingWriter(Stream stream, RagdollRecordingBudget? budget = null, long byteLimit = long.MaxValue)
    {
        this.stream = stream;
        this.budget = budget;
        ByteLimit = byteLimit;
        try
        {
            stream.Write(RagdollRecordingFormat.Magic);
            BytesWritten = RagdollRecordingFormat.Magic.Length;
            budget?.Take(BytesWritten);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public long BytesWritten { get; private set; }
    public long RecordCount { get; private set; }
    public bool Failed { get; private set; }
    public string? FailureMessage { get; private set; }
    public bool IsOpen => stream != null && !Failed;

    /// <summary>The most this file may hold, not counting its closing End record.</summary>
    public long ByteLimit { get; }

    /// <summary>True once a record was refused because it would have taken this file past <see cref="ByteLimit"/>.</summary>
    public bool FileLimitReached { get; private set; }

    /// <summary>True once a size limit (this file's or the shared budget's) stopped this writer. Only the End record is still accepted.</summary>
    public bool LimitReached => FileLimitReached || (budget != null && budget.Exhausted);

    /// <summary>Records that were replaced by a note because their body did not finish (<see cref="AbortRecord"/>).</summary>
    public long DiscardedRecords { get; private set; }

    public long Count(RecordType type) => (int)type < countByType.Length ? countByType[(int)type] : 0;

    /// <summary>Starts a record. Dispose the returned scope to finish it.</summary>
    public Scope Begin(RecordType type)
    {
        Monitor.Enter(gate);
        current = type;
        length = HeaderBytes;
        open = true;
        aborted = false;
        return new Scope(this);
    }

    /// <summary>
    /// Marks the record this thread is building as incomplete: its scope then writes a note in its
    /// place. Does nothing when this thread has no record open, so it is safe to call from an
    /// exception filter around any recorder code (a filter runs before the scope is disposed).
    /// </summary>
    public void AbortRecord()
    {
        if (Monitor.IsEntered(gate) && open) aborted = true;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly RagdollRecordingWriter writer;
        internal Scope(RagdollRecordingWriter writer) => this.writer = writer;
        public void Dispose() => writer.End();
    }

    private void End()
    {
        try
        {
            if (!open) return;
            open = false;
            if (stream == null || Failed) return;
            if (aborted)
            {
                // The body stopped half-way: what is in the buffer is not a whole record. Say so instead.
                aborted = false;
                DiscardedRecords++;
                var partial = current;
                var partialBytes = length - HeaderBytes;
                current = RecordType.Note;
                length = HeaderBytes;
                try
                {
                    Str(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{RagdollRecordingFormat.DiscardedRecordNote}{partial} ({(int)partial}), {partialBytes} payload bytes built"));
                }
                catch (Exception ex)
                {
                    Fail(ex);
                    return;
                }
            }
            if (current != RecordType.End)
            {
                // Once a limit refuses a record nothing but the End record follows, so the file has no hole.
                if (LimitReached) return;
                if (BytesWritten + length > ByteLimit)
                {
                    FileLimitReached = true;
                    return;
                }
                if (budget != null && !budget.TryTake(length)) return;
            }
            else budget?.Take(length);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), (ushort)current);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), (uint)(length - HeaderBytes));
            try
            {
                stream.Write(buffer, 0, length);
                BytesWritten += length;
                RecordCount++;
                if ((int)current < countByType.Length) countByType[(int)current]++;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }

    private void Fail(Exception ex)
    {
        Failed = true;
        FailureMessage = ex.GetType().Name + ": " + ex.Message;
    }

    /// <summary>Flushes and closes the underlying stream. Safe to call more than once.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            var s = stream;
            stream = null;
            if (s == null) return;
            try { s.Flush(); } catch (Exception ex) { Fail(ex); }
            try { s.Dispose(); } catch (Exception ex) { Fail(ex); }
        }
    }

    public void Flush()
    {
        lock (gate)
        {
            try { stream?.Flush(); } catch (Exception ex) { Fail(ex); }
        }
    }

    // ---- values ----

    private Span<byte> Take(int bytes)
    {
        if (length + bytes > buffer.Length)
        {
            var size = buffer.Length;
            while (size < length + bytes) size *= 2;
            try
            {
                Array.Resize(ref buffer, size);
            }
            catch
            {
                aborted = true; // the value cannot be stored: the record is incomplete
                throw;
            }
        }
        var span = buffer.AsSpan(length, bytes);
        length += bytes;
        return span;
    }

    private void Tag(ValueTag tag) => Take(1)[0] = (byte)tag;

    public void Null() => Tag(ValueTag.Null);

    public void Bool(bool value)
    {
        Tag(ValueTag.Bool);
        Take(1)[0] = value ? (byte)1 : (byte)0;
    }

    public void I32(int value)
    {
        Tag(ValueTag.I32);
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);
    }

    public void U32(uint value)
    {
        Tag(ValueTag.U32);
        BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);
    }

    public void I64(long value)
    {
        Tag(ValueTag.I64);
        BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);
    }

    public void U64(ulong value)
    {
        Tag(ValueTag.U64);
        BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);
    }

    public void F32(float value)
    {
        Tag(ValueTag.F32);
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), BitConverter.SingleToInt32Bits(value));
    }

    public void F64(double value)
    {
        Tag(ValueTag.F64);
        BinaryPrimitives.WriteInt64LittleEndian(Take(8), BitConverter.DoubleToInt64Bits(value));
    }

    public void Str(string? value)
    {
        if (value == null) { Null(); return; }
        Tag(ValueTag.String);
        var bytes = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), bytes);
        Encoding.UTF8.GetBytes(value, Take(bytes));
    }

    public void V2(Vector2 value)
    {
        Tag(ValueTag.Vector2);
        Raw(Take(8), value.X, value.Y);
    }

    public void V3(Vector3 value)
    {
        Tag(ValueTag.Vector3);
        Raw(Take(12), value.X, value.Y, value.Z);
    }

    public void Quat(Quaternion value)
    {
        Tag(ValueTag.Quaternion);
        Raw(Take(16), value.X, value.Y, value.Z, value.W);
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        Tag(ValueTag.Bytes);
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value.Length);
        value.CopyTo(Take(value.Length));
    }

    public void Floats(ReadOnlySpan<float> value)
    {
        Tag(ValueTag.F32Array);
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value.Length);
        CopyFloats(value, Take(value.Length * 4));
    }

    public void Ints(ReadOnlySpan<int> value)
    {
        Tag(ValueTag.I32Array);
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value.Length);
        var target = Take(value.Length * 4);
        if (BitConverter.IsLittleEndian) MemoryMarshal.AsBytes(value).CopyTo(target);
        else for (var i = 0; i < value.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(target.Slice(i * 4, 4), value[i]);
    }

    public void V3s(ReadOnlySpan<Vector3> value)
    {
        Tag(ValueTag.Vector3Array);
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value.Length);
        CopyFloats(MemoryMarshal.Cast<Vector3, float>(value), Take(value.Length * 12));
    }

    public void Quats(ReadOnlySpan<Quaternion> value)
    {
        Tag(ValueTag.QuaternionArray);
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value.Length);
        CopyFloats(MemoryMarshal.Cast<Quaternion, float>(value), Take(value.Length * 16));
    }

    /// <summary>Announces that the next <paramref name="count"/> values form one list.</summary>
    public void List(int count)
    {
        Tag(ValueTag.List);
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), count);
    }

    /// <summary>Writes one already decoded value back out, bit for bit.</summary>
    public void Value(in RecValue value)
    {
        Tag(value.Tag);
        switch (value.Tag)
        {
            case ValueTag.Null:
                break;
            case ValueTag.Bool:
                Take(1)[0] = (byte)value.Bits;
                break;
            case ValueTag.I32:
            case ValueTag.U32:
            case ValueTag.F32:
            case ValueTag.List:
                BinaryPrimitives.WriteUInt32LittleEndian(Take(4), (uint)value.Bits);
                break;
            case ValueTag.I64:
            case ValueTag.U64:
            case ValueTag.F64:
                BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value.Bits);
                break;
            case ValueTag.Vector2:
            case ValueTag.Vector3:
            case ValueTag.Quaternion:
                value.Data.CopyTo(Take(value.Data.Length));
                break;
            case ValueTag.String:
            case ValueTag.Bytes:
                BinaryPrimitives.WriteInt32LittleEndian(Take(4), value.Data.Length);
                value.Data.CopyTo(Take(value.Data.Length));
                break;
            case ValueTag.F32Array:
            case ValueTag.I32Array:
            case ValueTag.Vector3Array:
            case ValueTag.QuaternionArray:
                BinaryPrimitives.WriteInt32LittleEndian(Take(4), value.Data.Length / RecValue.ElementBytes(value.Tag));
                value.Data.CopyTo(Take(value.Data.Length));
                break;
            default:
                throw new InvalidOperationException($"Unknown value tag {value.Tag}");
        }
    }

    /// <summary>Writes a whole decoded record back out (used by the round-trip check).</summary>
    public void Record(RagdollRecord record)
    {
        using (Begin(record.Type))
            foreach (var value in record.Values)
                Value(value);
    }

    private static void Raw(Span<byte> target, float a, float b)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(a));
        BinaryPrimitives.WriteInt32LittleEndian(target[4..], BitConverter.SingleToInt32Bits(b));
    }

    private static void Raw(Span<byte> target, float a, float b, float c)
    {
        Raw(target, a, b);
        BinaryPrimitives.WriteInt32LittleEndian(target[8..], BitConverter.SingleToInt32Bits(c));
    }

    private static void Raw(Span<byte> target, float a, float b, float c, float d)
    {
        Raw(target, a, b, c);
        BinaryPrimitives.WriteInt32LittleEndian(target[12..], BitConverter.SingleToInt32Bits(d));
    }

    private static void CopyFloats(ReadOnlySpan<float> source, Span<byte> target)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(source).CopyTo(target);
            return;
        }
        for (var i = 0; i < source.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(i * 4, 4), BitConverter.SingleToInt32Bits(source[i]));
    }
}

/// <summary>
/// A byte allowance shared by the writers of one recording session. A record is either taken whole
/// or refused; after the first refusal the budget stays <see cref="Exhausted"/>, so every writer
/// that shares it stops at its next record.
/// </summary>
public sealed class RagdollRecordingBudget
{
    private long used;
    private volatile bool exhausted;

    public RagdollRecordingBudget(long limit) => Limit = limit;

    public long Limit { get; }
    public long Used => Interlocked.Read(ref used);
    public bool Exhausted => exhausted;

    /// <summary>Counts bytes that are written regardless of the limit (a file's magic and its End record).</summary>
    public void Take(long bytes) => Interlocked.Add(ref used, bytes);

    /// <summary>Takes the bytes if they fit under the limit; otherwise takes nothing and marks the budget exhausted.</summary>
    public bool TryTake(long bytes)
    {
        if (exhausted) return false;
        if (Interlocked.Add(ref used, bytes) <= Limit) return true;
        Interlocked.Add(ref used, -bytes);
        exhausted = true;
        return false;
    }
}
