// SPDX-License-Identifier: MIT
//
// Minimal binary plist (bplist00) writer + reader. Direct port of the
// Swift sibling's BinaryPlist.swift writer, plus a small reader for the
// inbound RTSP/DataStream plist bodies (which use only dict/string/int/
// data/bool/real — no NSKeyedArchiver UID graphs on the read path).
//
// We hand-roll this because PropertyListSerialization-equivalent APIs
// aren't available cross-platform in .NET, and the AirPlay/RTSP bodies
// are simple flat-ish dictionaries.

using System.Buffers.Binary;
using System.Text;

namespace AppleTVProtocol.AirPlay;

/// <summary>
/// Builds a bplist00 document. Objects are added bottom-up (scalars first,
/// then the containers that reference them); <see cref="Build"/> serializes
/// with the given root.
/// </summary>
public sealed class BinaryPlistWriter
{
    public readonly record struct ObjRef(int Index);

    private abstract record Obj;
    private sealed record RawObj(byte[] Bytes) : Obj;
    private sealed record ArrayObj(int[] Indices) : Obj;
    private sealed record DictObj(int[] Keys, int[] Values) : Obj;

    private readonly List<Obj> _objects = new();

    public ObjRef AddInt(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "negative ints not supported");
        byte[] bytes;
        if (value <= 0xFF)
            bytes = new byte[] { 0x10, (byte)value };
        else if (value <= 0xFFFF)
            bytes = new byte[] { 0x11, (byte)(value >> 8), (byte)(value & 0xFF) };
        else if (value <= 0xFFFFFFFF)
            bytes = new byte[] { 0x12, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        else
        {
            bytes = new byte[9];
            bytes[0] = 0x13;
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1), (ulong)value);
        }
        return Append(new RawObj(bytes));
    }

    public ObjRef AddBool(bool value) => Append(new RawObj(new byte[] { value ? (byte)0x09 : (byte)0x08 }));

    /// <summary>Append the "$null" sentinel string used in NSKeyedArchiver archives.</summary>
    public ObjRef AddNull() => AddString("$null");

    /// <summary>
    /// Encode a UID reference — the binary plist <c>0x8N</c> type that
    /// NSKeyedArchiver / RTIKeyedArchiver uses for object cross-references.
    /// Required by <see cref="RTITextOperations"/> for _tiC payloads.
    /// </summary>
    public ObjRef AddUid(uint value)
    {
        byte[] bytes;
        if (value <= 0xFF)
            bytes = new byte[] { 0x80, (byte)value };
        else if (value <= 0xFFFF)
            bytes = new byte[] { 0x81, (byte)(value >> 8), (byte)(value & 0xFF) };
        else
            bytes = new byte[] { 0x83,
                (byte)(value >> 24), (byte)((value >> 16) & 0xFF),
                (byte)((value >>  8) & 0xFF), (byte)(value & 0xFF) };
        return Append(new RawObj(bytes));
    }

    public ObjRef AddString(string s)
    {
        bool ascii = s.All(c => c < 128);
        if (ascii)
        {
            var payload = Encoding.ASCII.GetBytes(s);
            return Append(new RawObj(LengthPrefixed(0x50, payload.Length).Concat(payload).ToArray()));
        }
        // UTF-16BE
        var utf16 = new List<byte>();
        foreach (var ch in s)
        {
            utf16.Add((byte)(ch >> 8));
            utf16.Add((byte)(ch & 0xFF));
        }
        int charCount = utf16.Count / 2;
        return Append(new RawObj(LengthPrefixed(0x60, charCount).Concat(utf16).ToArray()));
    }

    public ObjRef AddData(byte[] d)
        => Append(new RawObj(LengthPrefixed(0x40, d.Length).Concat(d).ToArray()));

    public ObjRef AddArray(IEnumerable<ObjRef> refs)
        => Append(new ArrayObj(refs.Select(r => r.Index).ToArray()));

    public ObjRef AddDict(IEnumerable<(ObjRef Key, ObjRef Value)> pairs)
    {
        var list = pairs.ToList();
        return Append(new DictObj(list.Select(p => p.Key.Index).ToArray(),
                                  list.Select(p => p.Value.Index).ToArray()));
    }

    public byte[] Build(ObjRef topObject)
    {
        if (_objects.Count > 0xFFFF) throw new InvalidOperationException("too many plist objects");
        int refSize = _objects.Count <= 0xFF ? 1 : 2;

        var body = new List<byte>();
        var offsets = new List<int>();
        const int headerLen = 8; // "bplist00"

        foreach (var obj in _objects)
        {
            offsets.Add(headerLen + body.Count);
            switch (obj)
            {
                case RawObj r:
                    body.AddRange(r.Bytes);
                    break;
                case ArrayObj a:
                    body.AddRange(LengthPrefixed(0xA0, a.Indices.Length));
                    foreach (var idx in a.Indices) body.AddRange(RefBytes(idx, refSize));
                    break;
                case DictObj d:
                    body.AddRange(LengthPrefixed(0xD0, d.Keys.Length));
                    foreach (var idx in d.Keys)   body.AddRange(RefBytes(idx, refSize));
                    foreach (var idx in d.Values) body.AddRange(RefBytes(idx, refSize));
                    break;
            }
        }

        int offsetTableStart = headerLen + body.Count;
        int offsetSize = offsetTableStart <= 0xFF ? 1 : (offsetTableStart <= 0xFFFF ? 2 : 4);

        var result = new List<byte>(Encoding.ASCII.GetBytes("bplist00"));
        result.AddRange(body);
        foreach (var off in offsets)
            for (int shift = (offsetSize - 1) * 8; shift >= 0; shift -= 8)
                result.Add((byte)((off >> shift) & 0xFF));

        result.AddRange(new byte[] { 0, 0, 0, 0, 0, 0 });
        result.Add((byte)offsetSize);
        result.Add((byte)refSize);
        AppendUInt64(result, (ulong)_objects.Count);
        AppendUInt64(result, (ulong)topObject.Index);
        AppendUInt64(result, (ulong)offsetTableStart);
        return result.ToArray();
    }

    private ObjRef Append(Obj obj)
    {
        _objects.Add(obj);
        return new ObjRef(_objects.Count - 1);
    }

    private static byte[] RefBytes(int idx, int size)
        => size == 1 ? new[] { (byte)idx } : new[] { (byte)(idx >> 8), (byte)(idx & 0xFF) };

    private static byte[] LengthPrefixed(byte tag, int count)
    {
        if (count < 15) return new[] { (byte)(tag | (byte)count) };
        var header = new List<byte> { (byte)(tag | 0x0F) };
        if (count <= 0xFF) { header.Add(0x10); header.Add((byte)count); }
        else if (count <= 0xFFFF) { header.Add(0x11); header.Add((byte)(count >> 8)); header.Add((byte)(count & 0xFF)); }
        else { header.Add(0x12); header.Add((byte)(count >> 24)); header.Add((byte)(count >> 16)); header.Add((byte)(count >> 8)); header.Add((byte)count); }
        return header.ToArray();
    }

    private static void AppendUInt64(List<byte> data, ulong value)
    {
        for (int shift = 56; shift >= 0; shift -= 8)
            data.Add((byte)((value >> shift) & 0xFF));
    }
}

/// <summary>
/// Opaque UID reference inside an NSKeyedArchiver / RTIKeyedArchiver
/// document. `<see cref="Index"/>` is the position in the archive's
/// <c>$objects</c> array. Distinct from a plain <see cref="long"/> so
/// walkers can tell references from actual integers.
/// </summary>
public readonly record struct PlistUid(uint Index);

/// <summary>
/// Reader for inbound bplist00 documents. Returns nested dictionaries as
/// <c>Dictionary&lt;string, object?&gt;</c>; values may be string, long,
/// double, bool, byte[], object?[] (array), nested dict, or
/// <see cref="PlistUid"/> for archiver cross-references.
/// </summary>
public static class BinaryPlistReader
{
    public static object? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || !data.Slice(0, 8).SequenceEqual("bplist00"u8))
            throw new InvalidOperationException("not a bplist00 document");

        // Trailer is the last 32 bytes.
        var trailer = data.Slice(data.Length - 32);
        int offsetSize = trailer[6];
        int refSize    = trailer[7];
        int numObjects = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(8, 8));
        int topObject  = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(16, 8));
        int offsetTableStart = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(24, 8));

        var offsets = new int[numObjects];
        for (int i = 0; i < numObjects; i++)
        {
            int p = offsetTableStart + i * offsetSize;
            int off = 0;
            for (int b = 0; b < offsetSize; b++) off = (off << 8) | data[p + b];
            offsets[i] = off;
        }

        return ParseObject(data, offsets, refSize, topObject);
    }

    private static object? ParseObject(ReadOnlySpan<byte> data, int[] offsets, int refSize, int index)
    {
        int p = offsets[index];
        byte marker = data[p];
        byte hi = (byte)(marker & 0xF0);
        byte lo = (byte)(marker & 0x0F);

        switch (hi)
        {
            case 0x00:
                return marker switch { 0x08 => false, 0x09 => true, _ => (object?)null };
            case 0x10: // int
            {
                int byteCount = 1 << lo;
                ulong v = 0;
                for (int i = 0; i < byteCount; i++) v = (v << 8) | data[p + 1 + i];
                return (long)v;
            }
            case 0x20: // real
            {
                int byteCount = 1 << lo;
                if (byteCount == 8) return BinaryPrimitives.ReadDoubleBigEndian(data.Slice(p + 1, 8));
                if (byteCount == 4) return (double)BinaryPrimitives.ReadSingleBigEndian(data.Slice(p + 1, 4));
                return 0.0;
            }
            case 0x40: // data
            {
                var (count, headerLen) = ReadLength(data, p, lo);
                return data.Slice(p + headerLen, count).ToArray();
            }
            case 0x50: // ASCII string
            {
                var (count, headerLen) = ReadLength(data, p, lo);
                return Encoding.ASCII.GetString(data.Slice(p + headerLen, count));
            }
            case 0x60: // UTF-16BE string
            {
                var (count, headerLen) = ReadLength(data, p, lo);
                var bytes = data.Slice(p + headerLen, count * 2).ToArray();
                return Encoding.BigEndianUnicode.GetString(bytes);
            }
            case 0xA0: // array
            {
                var (count, headerLen) = ReadLength(data, p, lo);
                var arr = new object?[count];
                for (int i = 0; i < count; i++)
                {
                    int refIdx = ReadRef(data, p + headerLen + i * refSize, refSize);
                    arr[i] = ParseObject(data, offsets, refSize, refIdx);
                }
                return arr;
            }
            case 0x80: // UID reference (NSKeyedArchiver cross-ref)
            {
                int byteCount = lo + 1;
                uint v = 0;
                for (int i = 0; i < byteCount; i++) v = (v << 8) | data[p + 1 + i];
                return new PlistUid(v);
            }
            case 0xD0: // dict
            {
                var (count, headerLen) = ReadLength(data, p, lo);
                var dict = new Dictionary<string, object?>(count);
                int keysStart = p + headerLen;
                int valsStart = keysStart + count * refSize;
                for (int i = 0; i < count; i++)
                {
                    int kIdx = ReadRef(data, keysStart + i * refSize, refSize);
                    int vIdx = ReadRef(data, valsStart + i * refSize, refSize);
                    var key = ParseObject(data, offsets, refSize, kIdx)?.ToString() ?? "";
                    dict[key] = ParseObject(data, offsets, refSize, vIdx);
                }
                return dict;
            }
            default:
                return null;
        }
    }

    private static (int count, int headerLen) ReadLength(ReadOnlySpan<byte> data, int p, byte lo)
    {
        if (lo != 0x0F) return (lo, 1);
        // Next is an int object giving the real count.
        byte intMarker = data[p + 1];
        int byteCount = 1 << (intMarker & 0x0F);
        int count = 0;
        for (int i = 0; i < byteCount; i++) count = (count << 8) | data[p + 2 + i];
        return (count, 2 + byteCount);
    }

    private static int ReadRef(ReadOnlySpan<byte> data, int p, int refSize)
    {
        int v = 0;
        for (int i = 0; i < refSize; i++) v = (v << 8) | data[p + i];
        return v;
    }
}
