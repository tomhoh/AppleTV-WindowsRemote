// SPDX-License-Identifier: MIT
//
// Minimal hand-rolled protobuf (proto2 wire format) reader + writer.
//
// We only need to READ a handful of scalar fields out of the Apple TV's
// MediaRemote ProtocolMessage frames (title, artist, album, bundle id,
// playback state, elapsed time) and WRITE four tiny fixed init messages.
// Pulling in Google.Protobuf + compiling pyatv's ~50 interdependent .proto
// files would be far more machinery than this needs, so we walk the wire
// format directly.
//
// Wire types we handle:
//   0  Varint            (int32/64, uint, enum, bool)
//   1  64-bit            (double, fixed64)
//   2  Length-delimited  (string, bytes, embedded message)
//   5  32-bit            (float, fixed32)

using System.Buffers.Binary;
using System.Text;

namespace AppleTVProtocol.AirPlay;

/// <summary>Forward-only protobuf field reader over a byte span.</summary>
public ref struct ProtobufReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ProtobufReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public bool End => _pos >= _data.Length;

    /// <summary>
    /// Advance to the next field. Returns false at end of buffer.
    /// On success, <paramref name="fieldNumber"/> and <paramref name="wireType"/>
    /// describe the field; call the matching Read*Value to consume its value.
    /// </summary>
    public bool ReadTag(out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (End) return false;
        ulong tag = ReadVarintRaw();
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);
        return true;
    }

    public ulong ReadVarintValue() => ReadVarintRaw();

    public bool ReadBoolValue() => ReadVarintRaw() != 0;

    public long ReadInt64Value() => (long)ReadVarintRaw();

    public double ReadDoubleValue()
    {
        var slice = _data.Slice(_pos, 8);
        _pos += 8;
        return BinaryPrimitives.ReadDoubleLittleEndian(slice);
    }

    public float ReadFloatValue()
    {
        var slice = _data.Slice(_pos, 4);
        _pos += 4;
        return BinaryPrimitives.ReadSingleLittleEndian(slice);
    }

    /// <summary>Consume a length-delimited value and return its raw bytes.</summary>
    public ReadOnlySpan<byte> ReadBytesValue()
    {
        int len = (int)ReadVarintRaw();
        var slice = _data.Slice(_pos, len);
        _pos += len;
        return slice;
    }

    public string ReadStringValue() => Encoding.UTF8.GetString(ReadBytesValue());

    /// <summary>Skip a field's value given its wire type (for fields we don't care about).</summary>
    public void SkipValue(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarintRaw(); break;
            case 1: _pos += 8; break;
            case 2: { int len = (int)ReadVarintRaw(); _pos += len; break; }
            case 5: _pos += 4; break;
            default: throw new InvalidOperationException($"protobuf: unknown wire type {wireType}");
        }
    }

    private ulong ReadVarintRaw()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift >= 64) throw new InvalidOperationException("protobuf: varint too long");
        }
        return result;
    }
}

/// <summary>Append-only protobuf writer.</summary>
public sealed class ProtobufWriter
{
    private readonly System.IO.MemoryStream _ms = new();

    public byte[] ToArray() => _ms.ToArray();

    public void WriteVarintField(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, 0);
        WriteVarintRaw(value);
    }

    public void WriteBoolField(int fieldNumber, bool value)
        => WriteVarintField(fieldNumber, value ? 1UL : 0UL);

    public void WriteStringField(int fieldNumber, string value)
        => WriteBytesField(fieldNumber, Encoding.UTF8.GetBytes(value));

    public void WriteBytesField(int fieldNumber, byte[] value)
    {
        WriteTag(fieldNumber, 2);
        WriteVarintRaw((ulong)value.Length);
        _ms.Write(value, 0, value.Length);
    }

    /// <summary>Write an embedded message (already serialized) as a length-delimited field.</summary>
    public void WriteMessageField(int fieldNumber, ProtobufWriter inner)
        => WriteBytesField(fieldNumber, inner.ToArray());

    private void WriteTag(int fieldNumber, int wireType)
        => WriteVarintRaw(((ulong)fieldNumber << 3) | (uint)wireType);

    private void WriteVarintRaw(ulong value)
    {
        while (value >= 0x80)
        {
            _ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _ms.WriteByte((byte)value);
    }

    /// <summary>Encode a varint to a standalone byte array (for the MRP frame length prefix).</summary>
    public static byte[] EncodeVarint(ulong value)
    {
        var w = new ProtobufWriter();
        w.WriteVarintRaw(value);
        return w.ToArray();
    }
}

/// <summary>
/// Field-scan helpers that match the Swift port's <c>protobufBytesField</c>
/// / <c>protobufVarintField</c> / <c>protobufDoubleField</c> /
/// <c>protobufStringField</c> accessors: scan a serialized message for the
/// first field with the given number and return its decoded value.
/// </summary>
public static class Protobuf
{
    /// <summary>First length-delimited (wire type 2) field with this number, or null.</summary>
    public static byte[]? BytesField(ReadOnlySpan<byte> data, int fieldNumber)
    {
        var r = new ProtobufReader(data);
        while (r.ReadTag(out int fn, out int wt))
        {
            if (fn == fieldNumber && wt == 2)
                return r.ReadBytesValue().ToArray();
            r.SkipValue(wt);
        }
        return null;
    }

    /// <summary>First varint (wire type 0) field with this number, or null.</summary>
    public static ulong? VarintField(ReadOnlySpan<byte> data, int fieldNumber)
    {
        var r = new ProtobufReader(data);
        while (r.ReadTag(out int fn, out int wt))
        {
            if (fn == fieldNumber && wt == 0)
                return r.ReadVarintValue();
            r.SkipValue(wt);
        }
        return null;
    }

    /// <summary>First 64-bit (wire type 1) field with this number decoded as double, or null.</summary>
    public static double? DoubleField(ReadOnlySpan<byte> data, int fieldNumber)
    {
        var r = new ProtobufReader(data);
        while (r.ReadTag(out int fn, out int wt))
        {
            if (fn == fieldNumber && wt == 1)
                return r.ReadDoubleValue();
            r.SkipValue(wt);
        }
        return null;
    }

    /// <summary>First length-delimited field with this number decoded as UTF-8, or null if absent/empty.</summary>
    public static string? StringField(ReadOnlySpan<byte> data, int fieldNumber)
    {
        var bytes = BytesField(data, fieldNumber);
        if (bytes is null || bytes.Length == 0) return null;
        var s = Encoding.UTF8.GetString(bytes);
        return s.Length == 0 ? null : s;
    }
}
