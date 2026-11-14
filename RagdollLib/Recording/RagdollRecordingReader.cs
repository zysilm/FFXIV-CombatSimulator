// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace CombatSimulator.Animation.Recording;

/// <summary>One decoded value of a record. Scalars live in <see cref="Bits"/> (floats as their raw
/// bits); strings, byte blocks, vectors and arrays live in <see cref="Data"/> as stored.</summary>
public readonly struct RecValue : IEquatable<RecValue>
{
    private static readonly byte[] NoData = Array.Empty<byte>();

    public RecValue(ValueTag tag, ulong bits, byte[]? data = null)
    {
        Tag = tag;
        Bits = bits;
        this.data = data;
    }

    public ValueTag Tag { get; }
    public ulong Bits { get; }
    private readonly byte[]? data;
    public byte[] Data => data ?? NoData;

    public static int ElementBytes(ValueTag tag) => tag switch
    {
        ValueTag.F32Array or ValueTag.I32Array => 4,
        ValueTag.Vector3Array => 12,
        ValueTag.QuaternionArray => 16,
        _ => 1,
    };

    /// <summary>Elements of an array value, items of a list, bytes of a block.</summary>
    public int Count => Tag == ValueTag.List ? (int)Bits : Data.Length / ElementBytes(Tag);

    public bool IsNull => Tag == ValueTag.Null;
    public bool AsBool => Bits != 0;
    public int AsInt => (int)(uint)Bits;
    public uint AsUInt => (uint)Bits;
    public long AsLong => Tag is ValueTag.I32 ? (int)(uint)Bits : (long)Bits;
    public ulong AsULong => Bits;
    public float AsFloat => BitConverter.Int32BitsToSingle((int)(uint)Bits);
    public double AsDouble => Tag == ValueTag.F32 ? AsFloat : BitConverter.Int64BitsToDouble((long)Bits);
    public string? AsString => Tag == ValueTag.String ? Encoding.UTF8.GetString(Data) : null;

    public Vector3 AsVector3 => new(FloatAt(0), FloatAt(1), FloatAt(2));
    public Quaternion AsQuaternion => new(FloatAt(0), FloatAt(1), FloatAt(2), FloatAt(3));

    /// <summary>The i-th 32-bit float of the data block (vectors, float arrays, vector arrays).</summary>
    public float FloatAt(int index) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(index * 4, 4)));

    /// <summary>The raw bits of the i-th 32-bit element of the data block.</summary>
    public uint WordAt(int index) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(index * 4, 4));

    public int IntAt(int index) => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(index * 4, 4));

    public bool Equals(RecValue other) =>
        Tag == other.Tag && Bits == other.Bits && Data.AsSpan().SequenceEqual(other.Data);

    public override bool Equals(object? obj) => obj is RecValue other && Equals(other);
    public override int GetHashCode() => HashCode.Combine((int)Tag, Bits, Data.Length);

    public override string ToString() => Tag switch
    {
        ValueTag.Null => "null",
        ValueTag.Bool => AsBool ? "true" : "false",
        ValueTag.I32 => AsInt.ToString(CultureInfo.InvariantCulture),
        ValueTag.U32 => AsUInt.ToString(CultureInfo.InvariantCulture),
        ValueTag.I64 => ((long)Bits).ToString(CultureInfo.InvariantCulture),
        ValueTag.U64 => "0x" + Bits.ToString("X", CultureInfo.InvariantCulture),
        ValueTag.F32 => AsFloat.ToString("R", CultureInfo.InvariantCulture),
        ValueTag.F64 => AsDouble.ToString("R", CultureInfo.InvariantCulture),
        ValueTag.String => "\"" + AsString + "\"",
        ValueTag.Vector2 => $"({Format(0)}, {Format(1)})",
        ValueTag.Vector3 => $"({Format(0)}, {Format(1)}, {Format(2)})",
        ValueTag.Quaternion => $"({Format(0)}, {Format(1)}, {Format(2)}, {Format(3)})",
        ValueTag.List => $"list[{Count}]",
        _ => $"{Tag}[{Count}]",
    };

    private string Format(int index) => FloatAt(index).ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>One decoded record.</summary>
public sealed class RagdollRecord
{
    public RagdollRecord(RecordType type, long offset, int payloadBytes, RecValue[] values)
    {
        Type = type;
        Offset = offset;
        PayloadBytes = payloadBytes;
        Values = values;
    }

    public RecordType Type { get; }
    /// <summary>Byte offset of the record in the file.</summary>
    public long Offset { get; }
    public int PayloadBytes { get; }
    public RecValue[] Values { get; }

    /// <summary>The value at an index, or a null value when the record is shorter (older or truncated).</summary>
    public RecValue this[int index] => index >= 0 && index < Values.Length ? Values[index] : default;

    public bool ContentEquals(RagdollRecord other)
    {
        if (Type != other.Type || Values.Length != other.Values.Length) return false;
        for (var i = 0; i < Values.Length; i++)
            if (!Values[i].Equals(other.Values[i])) return false;
        return true;
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(Type).Append(':');
        for (var i = 0; i < Values.Length && i < 24; i++)
            text.Append(' ').Append(Values[i].ToString());
        if (Values.Length > 24) text.Append(" ...");
        return text.ToString();
    }
}

/// <summary>Reads a ragdoll recording record by record.</summary>
public sealed class RagdollRecordingReader : IDisposable
{
    private readonly Stream stream;
    private readonly byte[] header = new byte[6];
    private long position;

    public RagdollRecordingReader(Stream stream)
    {
        this.stream = stream;
        Span<byte> magic = stackalloc byte[RagdollRecordingFormat.Magic.Length];
        var read = ReadFully(magic);
        if (read != magic.Length || !magic.SequenceEqual(RagdollRecordingFormat.Magic))
            throw new InvalidDataException("Not a ragdoll recording (bad magic).");
        position = magic.Length;
    }

    public static RagdollRecordingReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan));

    /// <summary>The format version from the header record, or 0 until one was read.</summary>
    public int FormatVersion { get; private set; }

    /// <summary>True when the file ended in the middle of a record (the recording was cut short).</summary>
    public bool Truncated { get; private set; }

    /// <summary>The next record, or null at the end of the file.</summary>
    public RagdollRecord? Read()
    {
        var offset = position;
        var got = ReadFully(header);
        if (got == 0) return null;
        if (got < header.Length) { Truncated = true; return null; }
        var type = (RecordType)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        var payloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
        if (payloadBytes > int.MaxValue) { Truncated = true; return null; }
        var payload = new byte[payloadBytes];
        if (ReadFully(payload) < payload.Length) { Truncated = true; return null; }
        position += header.Length + payload.Length;
        var record = new RagdollRecord(type, offset, payload.Length, Decode(payload));
        if (type == RecordType.Header && FormatVersion == 0) FormatVersion = record[0].AsInt;
        return record;
    }

    public IEnumerable<RagdollRecord> ReadAll()
    {
        while (Read() is { } record)
            yield return record;
    }

    public void Dispose() => stream.Dispose();

    private int ReadFully(Span<byte> target)
    {
        var total = 0;
        while (total < target.Length)
        {
            var n = stream.Read(target[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Decodes a payload. A payload that ends inside a value yields the values before it.</summary>
    public static RecValue[] Decode(ReadOnlySpan<byte> payload)
    {
        var values = new List<RecValue>();
        var at = 0;
        while (at < payload.Length)
        {
            var tag = (ValueTag)payload[at++];
            int fixedBytes;
            switch (tag)
            {
                case ValueTag.Null:
                    values.Add(new RecValue(tag, 0));
                    continue;
                case ValueTag.Bool:
                    if (at + 1 > payload.Length) return values.ToArray();
                    values.Add(new RecValue(tag, payload[at]));
                    at += 1;
                    continue;
                case ValueTag.I32:
                case ValueTag.U32:
                case ValueTag.F32:
                case ValueTag.List:
                    if (at + 4 > payload.Length) return values.ToArray();
                    values.Add(new RecValue(tag, BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(at, 4))));
                    at += 4;
                    continue;
                case ValueTag.I64:
                case ValueTag.U64:
                case ValueTag.F64:
                    if (at + 8 > payload.Length) return values.ToArray();
                    values.Add(new RecValue(tag, BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(at, 8))));
                    at += 8;
                    continue;
                case ValueTag.Vector2: fixedBytes = 8; break;
                case ValueTag.Vector3: fixedBytes = 12; break;
                case ValueTag.Quaternion: fixedBytes = 16; break;
                case ValueTag.String:
                case ValueTag.Bytes:
                case ValueTag.F32Array:
                case ValueTag.I32Array:
                case ValueTag.Vector3Array:
                case ValueTag.QuaternionArray:
                {
                    if (at + 4 > payload.Length) return values.ToArray();
                    var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(at, 4));
                    at += 4;
                    var bytes = (long)count * RecValue.ElementBytes(tag);
                    if (count < 0 || at + bytes > payload.Length) return values.ToArray();
                    values.Add(new RecValue(tag, 0, payload.Slice(at, (int)bytes).ToArray()));
                    at += (int)bytes;
                    continue;
                }
                default:
                    // An unknown tag cannot be skipped (its size is unknown): stop here.
                    return values.ToArray();
            }
            if (at + fixedBytes > payload.Length) return values.ToArray();
            values.Add(new RecValue(tag, 0, payload.Slice(at, fixedBytes).ToArray()));
            at += fixedBytes;
        }
        return values.ToArray();
    }
}
