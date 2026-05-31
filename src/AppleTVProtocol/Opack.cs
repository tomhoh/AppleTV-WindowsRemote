// SPDX-License-Identifier: MIT
//
// Ported from pyatv (pyatv/support/opack.py, MIT) by Pierre Ståhl.
//
// OPACK is Apple's internal binary serialization format used by the Companion
// protocol. It's a tagged-byte format with built-in object referencing —
// repeated values are interned into an object table and reused via short
// references (0xA0–0xC4), keeping payloads compact.
//
// This port matches pyatv's behaviour exactly. The one limitation we inherit
// is that absolute time (tag 0x06) decodes as a plain integer and cannot
// be re-encoded — neither Apple's own use of the format nor any payload we
// need to send relies on it.

using System.Buffers.Binary;
using System.Text;

namespace AppleTVProtocol;

/// <summary>
/// Wrapper for integers that should be re-encoded at a specific byte width
/// instead of the smallest one that fits. <see cref="Opack.Unpack"/> returns
/// these for explicit-width int encodings (0x30..0x33); use the constructor
/// when packing if you need to preserve a known width.
/// </summary>
public readonly record struct SizedInt(long Value, int SizeBytes);

/// <summary>
/// Encoder/decoder for Apple's OPACK binary serialization format.
/// </summary>
public static class Opack
{
    // ────────────────────────────────────────────────────────────────────
    // Pack
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Encode an OPACK-compatible value to bytes.</summary>
    /// <param name="data">
    /// One of: <c>null</c>, <see cref="bool"/>, <see cref="int"/> / <see cref="long"/>,
    /// <see cref="SizedInt"/>, <see cref="float"/>, <see cref="double"/>,
    /// <see cref="string"/>, <see cref="byte"/>[], <see cref="Guid"/>, an
    /// <see cref="System.Collections.Generic.IList{T}"/> of OPACK values, or
    /// an <see cref="System.Collections.Generic.IDictionary{TKey, TValue}"/>
    /// of OPACK values.
    /// </param>
    /// <exception cref="ArgumentException">Thrown for unsupported types.</exception>
    public static byte[] Pack(object? data)
    {
        var objectList = new List<byte[]>();
        var ms = new MemoryStream();
        PackInternal(data, ms, objectList);
        return ms.ToArray();
    }

    private static void PackInternal(object? data, MemoryStream output, List<byte[]> objectList)
    {
        byte[] packed = EncodeValue(data, objectList);

        // Object referencing: if this exact byte sequence was already emitted,
        // replace it with a back-reference. Only forms longer than 1 byte are
        // worth interning — the 1-byte forms (bool/null/inline int) are
        // already smaller than any reference.
        int existing = FindExisting(objectList, packed);
        if (existing >= 0)
        {
            packed = EncodeReference(existing);
        }
        else if (packed.Length > 1)
        {
            objectList.Add(packed);
        }

        output.Write(packed, 0, packed.Length);
    }

    private static byte[] EncodeValue(object? data, List<byte[]> objectList)
    {
        switch (data)
        {
            case null:
                return new byte[] { 0x04 };

            case bool b:
                return new byte[] { b ? (byte)0x01 : (byte)0x02 };

            case Guid g:
            {
                var bytes = new byte[17];
                bytes[0] = 0x05;
                g.ToByteArray().CopyTo(bytes, 1);
                return bytes;
            }

            case SizedInt si:
                return EncodeInt(si.Value, si.SizeBytes);

            case int i:
                return EncodeInt(i, null);
            case long l:
                return EncodeInt(l, null);
            case uint ui:
                return EncodeInt(ui, null);
            case ulong ul when ul <= long.MaxValue:
                return EncodeInt((long)ul, null);
            case byte bt:
                return EncodeInt(bt, null);
            case short s:
                return EncodeInt(s, null);
            case ushort us:
                return EncodeInt(us, null);

            case float f:
            {
                var bytes = new byte[5];
                bytes[0] = 0x35;
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(1), f);
                return bytes;
            }

            case double d:
            {
                var bytes = new byte[9];
                bytes[0] = 0x36;
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(1), d);
                return bytes;
            }

            case string str:
                return EncodeString(str);

            case byte[] ba:
                return EncodeBytes(ba);

            case System.Collections.IDictionary dict:
                return EncodeDictionary(dict, objectList);

            // IList comes after IDictionary because Dictionary<,> also implements
            // ICollection and IEnumerable; checking IDictionary first avoids
            // mis-classifying it as a list.
            case System.Collections.IEnumerable enumerable:
                return EncodeList(enumerable, objectList);

            default:
                throw new ArgumentException(
                    $"OPACK can't pack value of type {data.GetType().FullName}");
        }
    }

    private static byte[] EncodeInt(long value, int? sizeHint)
    {
        // Inline 0..0x27 → single byte (only when caller didn't request a width).
        if (sizeHint is null && value >= 0 && value < 0x28)
        {
            return new byte[] { (byte)(value + 8) };
        }

        // Pick the smallest width that fits, unless explicitly overridden.
        int width;
        if (sizeHint is not null)
        {
            width = sizeHint.Value;
        }
        else if (value >= 0 && value <= 0xFF)        width = 1;
        else if (value >= 0 && value <= 0xFFFF)      width = 2;
        else if (value >= 0 && value <= 0xFFFFFFFF)  width = 4;
        else                                         width = 8;

        byte tag = width switch
        {
            1 => 0x30,
            2 => 0x31,
            4 => 0x32,
            8 => 0x33,
            _ => throw new ArgumentException($"OPACK int width must be 1/2/4/8, got {width}")
        };
        var bytes = new byte[width + 1];
        bytes[0] = tag;
        WriteIntLittleEndian(value, bytes.AsSpan(1, width));
        return bytes;
    }

    private static byte[] EncodeString(string str)
    {
        var encoded = Encoding.UTF8.GetBytes(str);
        return EncodeLengthPrefixed(
            encoded,
            inlineTagBase:    0x40,
            inlineMaxLength:  0x20,
            extendedTagBase:  0x60);
    }

    private static byte[] EncodeBytes(byte[] data)
    {
        return EncodeLengthPrefixed(
            data,
            inlineTagBase:    0x70,
            inlineMaxLength:  0x20,
            extendedTagBase:  0x90);
    }

    /// <summary>
    /// Shared encoder for the string/bytes forms — they have identical layout
    /// modulo the tag base.
    /// </summary>
    private static byte[] EncodeLengthPrefixed(byte[] payload, byte inlineTagBase,
                                               int inlineMaxLength, byte extendedTagBase)
    {
        int len = payload.Length;
        if (len <= inlineMaxLength)
        {
            var bytes = new byte[len + 1];
            bytes[0] = (byte)(inlineTagBase + len);
            payload.CopyTo(bytes, 1);
            return bytes;
        }
        // Extended forms: tag = base | width-class (1/2/3/4 for string,
        // 1/2/4/8 for bytes — encoded via the tag's low nibble).
        int widthBytes;
        byte tag;
        // `len` is an int (max 2^31-1), so the 4-byte and 8-byte forms are
        // never actually selected from C#-managed inputs — int.MaxValue
        // always fits in 4 bytes. The branches are kept symmetric with
        // pyatv anyway in case we ever surface a chunked/streamed API.
        if (extendedTagBase == 0x60)
        {
            // String length nibble = 1..4 → 1..4 bytes literal.
            if      (len <= 0xFF)     { widthBytes = 1; tag = 0x61; }
            else if (len <= 0xFFFF)   { widthBytes = 2; tag = 0x62; }
            else if (len <= 0xFFFFFF) { widthBytes = 3; tag = 0x63; }
            else                      { widthBytes = 4; tag = 0x64; }
        }
        else
        {
            // Bytes length nibble n → 2^(n-1) bytes (1, 2, 4, 8).
            if      (len <= 0xFF)   { widthBytes = 1; tag = 0x91; }
            else if (len <= 0xFFFF) { widthBytes = 2; tag = 0x92; }
            else                    { widthBytes = 4; tag = 0x93; }
        }
        var output = new byte[1 + widthBytes + len];
        output[0] = tag;
        WriteIntLittleEndian(len, output.AsSpan(1, widthBytes));
        payload.CopyTo(output, 1 + widthBytes);
        return output;
    }

    private static byte[] EncodeList(System.Collections.IEnumerable items, List<byte[]> objectList)
    {
        // Materialise so we can count.
        var packedItems = new List<byte[]>();
        var bodyStream = new MemoryStream();
        foreach (var item in items)
        {
            PackInternal(item, bodyStream, objectList);
            packedItems.Add(Array.Empty<byte>()); // dummy — bodyStream already contains
        }
        int count = packedItems.Count;
        byte tag = (byte)(0xD0 + Math.Min(count, 0xF));
        byte[] body = bodyStream.ToArray();
        bool endless = count >= 0xF;
        var output = new byte[1 + body.Length + (endless ? 1 : 0)];
        output[0] = tag;
        body.CopyTo(output, 1);
        if (endless) output[output.Length - 1] = 0x03;
        return output;
    }

    private static byte[] EncodeDictionary(System.Collections.IDictionary dict, List<byte[]> objectList)
    {
        var bodyStream = new MemoryStream();
        int count = 0;
        foreach (System.Collections.DictionaryEntry kv in dict)
        {
            PackInternal(kv.Key,   bodyStream, objectList);
            PackInternal(kv.Value, bodyStream, objectList);
            count++;
        }
        byte tag = (byte)(0xE0 + Math.Min(count, 0xF));
        byte[] body = bodyStream.ToArray();
        bool endless = count >= 0xF;
        var output = new byte[1 + body.Length + (endless ? 1 : 0)];
        output[0] = tag;
        body.CopyTo(output, 1);
        if (endless) output[output.Length - 1] = 0x03;
        return output;
    }

    private static int FindExisting(List<byte[]> objectList, byte[] packed)
    {
        for (int i = 0; i < objectList.Count; i++)
        {
            if (objectList[i].AsSpan().SequenceEqual(packed))
            {
                return i;
            }
        }
        return -1;
    }

    private static byte[] EncodeReference(int index)
    {
        if (index < 0x21)
            return new byte[] { (byte)(0xA0 + index) };
        if (index <= 0xFF)
            return new byte[] { 0xC1, (byte)index };
        if (index <= 0xFFFF)
        {
            var b = new byte[3]; b[0] = 0xC2;
            WriteIntLittleEndian(index, b.AsSpan(1, 2));
            return b;
        }
        // 4-byte form (int max < uint max, so always reachable via this branch).
        var bytes = new byte[5]; bytes[0] = 0xC3;
        WriteIntLittleEndian(index, bytes.AsSpan(1, 4));
        return bytes;
    }

    // ────────────────────────────────────────────────────────────────────
    // Unpack
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decode a single OPACK value from <paramref name="data"/>. Returns the
    /// value and the number of input bytes consumed.
    /// </summary>
    public static (object? Value, int Consumed) Unpack(ReadOnlySpan<byte> data)
    {
        var objectList = new List<object?>();
        int consumed = UnpackInternal(data, 0, objectList, out var value);
        return (value, consumed);
    }

    private static int UnpackInternal(ReadOnlySpan<byte> data, int pos,
                                       List<object?> objectList, out object? value)
    {
        if (pos >= data.Length)
            throw new ArgumentException("OPACK: truncated input");
        byte tag = data[pos];
        int consumed;
        bool addToObjectList = true;

        if (tag == 0x01)          { value = true;  consumed = 1; addToObjectList = false; }
        else if (tag == 0x02)     { value = false; consumed = 1; addToObjectList = false; }
        else if (tag == 0x04)     { value = null;  consumed = 1; addToObjectList = false; }
        else if (tag == 0x05)
        {
            Need(data, pos, 17);
            value = new Guid(data.Slice(pos + 1, 16).ToArray());
            consumed = 17;
        }
        else if (tag == 0x06)
        {
            // Absolute time (CFAbsoluteTime) — pyatv decodes as a raw int; we
            // do the same. Can't be re-packed.
            Need(data, pos, 9);
            value = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(pos + 1, 8));
            consumed = 9;
        }
        else if (tag >= 0x08 && tag <= 0x2F)
        {
            value = (long)(tag - 8);
            consumed = 1;
            addToObjectList = false;
        }
        else if (tag == 0x35)
        {
            Need(data, pos, 5);
            value = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(pos + 1, 4));
            consumed = 5;
        }
        else if (tag == 0x36)
        {
            Need(data, pos, 9);
            value = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(pos + 1, 8));
            consumed = 9;
        }
        else if ((tag & 0xF0) == 0x30)
        {
            int width = 1 << (tag & 0x0F);
            Need(data, pos, 1 + width);
            long v = ReadIntLittleEndian(data.Slice(pos + 1, width));
            value = new SizedInt(v, width);
            consumed = 1 + width;
        }
        else if (tag >= 0x40 && tag <= 0x60)
        {
            int len = tag - 0x40;
            Need(data, pos, 1 + len);
            value = Encoding.UTF8.GetString(data.Slice(pos + 1, len));
            consumed = 1 + len;
        }
        else if (tag > 0x60 && tag <= 0x64)
        {
            int widthBytes = tag & 0x0F;
            Need(data, pos, 1 + widthBytes);
            int len = (int)ReadIntLittleEndian(data.Slice(pos + 1, widthBytes));
            Need(data, pos, 1 + widthBytes + len);
            value = Encoding.UTF8.GetString(data.Slice(pos + 1 + widthBytes, len));
            consumed = 1 + widthBytes + len;
        }
        else if (tag >= 0x70 && tag <= 0x90)
        {
            int len = tag - 0x70;
            Need(data, pos, 1 + len);
            value = data.Slice(pos + 1, len).ToArray();
            consumed = 1 + len;
        }
        else if (tag >= 0x91 && tag <= 0x94)
        {
            int widthBytes = 1 << ((tag & 0x0F) - 1);
            Need(data, pos, 1 + widthBytes);
            int len = (int)ReadIntLittleEndian(data.Slice(pos + 1, widthBytes));
            Need(data, pos, 1 + widthBytes + len);
            value = data.Slice(pos + 1 + widthBytes, len).ToArray();
            consumed = 1 + widthBytes + len;
        }
        else if ((tag & 0xF0) == 0xD0)
        {
            int count = tag & 0x0F;
            var list = new List<object?>();
            int ptr = pos + 1;
            if (count == 0xF)
            {
                while (true)
                {
                    if (ptr >= data.Length)
                        throw new ArgumentException("OPACK: unterminated endless list");
                    if (data[ptr] == 0x03) { ptr++; break; }
                    ptr += UnpackInternal(data, ptr, objectList, out var item);
                    list.Add(item);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    ptr += UnpackInternal(data, ptr, objectList, out var item);
                    list.Add(item);
                }
            }
            value = list;
            consumed = ptr - pos;
            addToObjectList = false;
        }
        else if ((tag & 0xF0) == 0xE0)
        {
            int count = tag & 0x0F;
            var dict = new Dictionary<object, object?>();
            int ptr = pos + 1;
            if (count == 0xF)
            {
                while (true)
                {
                    if (ptr >= data.Length)
                        throw new ArgumentException("OPACK: unterminated endless dict");
                    if (data[ptr] == 0x03) { ptr++; break; }
                    ptr += UnpackInternal(data, ptr, objectList, out var k);
                    ptr += UnpackInternal(data, ptr, objectList, out var v);
                    dict[k ?? throw new ArgumentException("OPACK: null dict key")] = v;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    ptr += UnpackInternal(data, ptr, objectList, out var k);
                    ptr += UnpackInternal(data, ptr, objectList, out var v);
                    dict[k ?? throw new ArgumentException("OPACK: null dict key")] = v;
                }
            }
            value = dict;
            consumed = ptr - pos;
            addToObjectList = false;
        }
        else if (tag >= 0xA0 && tag <= 0xC0)
        {
            int idx = tag - 0xA0;
            if (idx >= objectList.Count)
                throw new ArgumentException($"OPACK: reference 0x{idx:X} out of range");
            value = objectList[idx];
            consumed = 1;
        }
        else if (tag >= 0xC1 && tag <= 0xC4)
        {
            int width = tag - 0xC0;
            Need(data, pos, 1 + width);
            int idx = (int)ReadIntLittleEndian(data.Slice(pos + 1, width));
            if (idx >= objectList.Count)
                throw new ArgumentException($"OPACK: reference 0x{idx:X} out of range");
            value = objectList[idx];
            consumed = 1 + width;
        }
        else
        {
            throw new ArgumentException($"OPACK: unknown tag 0x{tag:X2}");
        }

        if (addToObjectList && !ContainsValue(objectList, value))
        {
            objectList.Add(value);
        }
        return consumed;
    }

    private static bool ContainsValue(List<object?> list, object? value)
    {
        foreach (var item in list)
        {
            if (Equals(item, value)) return true;
            // byte[] needs sequence equality
            if (item is byte[] a && value is byte[] b && a.AsSpan().SequenceEqual(b))
                return true;
        }
        return false;
    }

    // ────────────────────────────────────────────────────────────────────
    // Little-endian helpers
    // ────────────────────────────────────────────────────────────────────

    private static void WriteIntLittleEndian(long value, Span<byte> dest)
    {
        ulong u = (ulong)value;
        for (int i = 0; i < dest.Length; i++)
        {
            dest[i] = (byte)(u & 0xFF);
            u >>= 8;
        }
    }

    private static long ReadIntLittleEndian(ReadOnlySpan<byte> source)
    {
        ulong u = 0;
        for (int i = source.Length - 1; i >= 0; i--)
        {
            u = (u << 8) | source[i];
        }
        return (long)u;
    }

    private static void Need(ReadOnlySpan<byte> data, int pos, int bytes)
    {
        if (pos + bytes > data.Length)
            throw new ArgumentException(
                $"OPACK: truncated value at offset {pos}: need {bytes}, have {data.Length - pos}");
    }
}
