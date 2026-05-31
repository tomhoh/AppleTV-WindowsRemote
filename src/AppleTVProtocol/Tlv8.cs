// SPDX-License-Identifier: MIT
//
// Ported from pyatv (pyatv/auth/hap_tlv8.py, MIT) by Pierre Ståhl.
// Two intentional fixes relative to upstream:
//   1. Chunk size for >255-byte values uses the remaining length instead of
//      a constant — pyatv's `size = min(length, 255)` writes invalid frames
//      when the value exceeds 255 bytes because `length` never decrements.
//   2. Empty-value entries (zero-length values) are still written as
//      `tag 00` — pyatv's `while pos < len(value)` loop drops them entirely,
//      which loses information on round-trip.

namespace AppleTVProtocol;

/// <summary>
/// TLV8 (Type-Length-Value with an 8-bit length byte) — Apple's HomeKit
/// pairing wire format. Each entry is one byte of tag, one byte of length,
/// and N bytes of value. Values longer than 255 bytes are split into
/// chunks of up to 255 bytes that share the same tag; <see cref="Read"/>
/// concatenates them on the way in, <see cref="Write"/> re-splits on the
/// way out.
///
/// Only one level of value is supported (no dicts-in-dicts).
/// </summary>
public static class Tlv8
{
    public const int MaxChunk = 255;

    /// <summary>
    /// Parse a TLV8 byte sequence into a tag→value dictionary. Repeated tags
    /// are concatenated in the order they appear (this is how values
    /// longer than 255 bytes are encoded).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if the input is truncated mid-header or mid-value.
    /// </exception>
    public static Dictionary<byte, byte[]> Read(ReadOnlySpan<byte> data)
    {
        // Accumulate into List<byte> to keep concatenation O(1) amortised.
        var pending = new Dictionary<byte, List<byte>>();
        var order = new List<byte>(); // preserve first-seen tag order on round-trip
        int pos = 0;

        while (pos < data.Length)
        {
            if (pos + 2 > data.Length)
            {
                throw new ArgumentException(
                    $"Truncated TLV8 header at offset {pos}: need 2 bytes, have {data.Length - pos}");
            }
            byte tag = data[pos];
            byte length = data[pos + 1];
            int valueStart = pos + 2;
            if (valueStart + length > data.Length)
            {
                throw new ArgumentException(
                    $"Truncated TLV8 value at offset {pos}: declared length {length}, " +
                    $"available {data.Length - valueStart}");
            }

            if (!pending.TryGetValue(tag, out var bytes))
            {
                bytes = new List<byte>(length);
                pending[tag] = bytes;
                order.Add(tag);
            }
            for (int i = 0; i < length; i++)
            {
                bytes.Add(data[valueStart + i]);
            }
            pos = valueStart + length;
        }

        // Materialise in first-seen-tag order for predictable enumeration.
        var result = new Dictionary<byte, byte[]>(order.Count);
        foreach (var tag in order)
        {
            result[tag] = pending[tag].ToArray();
        }
        return result;
    }

    /// <summary>
    /// Encode a tag→value dictionary as TLV8 bytes. Values longer than
    /// 255 bytes are split across multiple consecutive entries sharing
    /// the same tag.
    /// </summary>
    public static byte[] Write(IEnumerable<KeyValuePair<byte, byte[]>> values)
    {
        // Pre-compute the output size to avoid List<byte> growth.
        int total = 0;
        foreach (var kv in values)
        {
            int len = kv.Value.Length;
            if (len == 0)
            {
                total += 2;  // one header, no value
            }
            else
            {
                int chunks = (len + MaxChunk - 1) / MaxChunk;
                total += chunks * 2 + len;
            }
        }

        var output = new byte[total];
        int pos = 0;
        foreach (var (tag, value) in values)
        {
            if (value.Length == 0)
            {
                output[pos++] = tag;
                output[pos++] = 0;
                continue;
            }
            int valuePos = 0;
            while (valuePos < value.Length)
            {
                int chunkSize = Math.Min(value.Length - valuePos, MaxChunk);
                output[pos++] = tag;
                output[pos++] = (byte)chunkSize;
                Array.Copy(value, valuePos, output, pos, chunkSize);
                pos += chunkSize;
                valuePos += chunkSize;
            }
        }
        return output;
    }
}

// ──────────────────────────────────────────────────────────────────────────
// HAP-defined tag and enum constants. Same values + meanings as pyatv;
// surfaced here as C# enums so call sites can use names rather than magic
// hex bytes. (`Tlv8.Read` returns dictionaries keyed by raw byte so the
// caller can opt in or out of enum cast.)
// ──────────────────────────────────────────────────────────────────────────

/// <summary>HAP standardised TLV8 tag values.</summary>
public enum Tlv8Tag : byte
{
    Method         = 0x00,
    Identifier     = 0x01,
    Salt           = 0x02,
    PublicKey      = 0x03,
    Proof          = 0x04,
    EncryptedData  = 0x05,
    SeqNo          = 0x06,
    Error          = 0x07,
    BackOff        = 0x08,
    Certificate    = 0x09,
    Signature      = 0x0A,
    Permissions    = 0x0B,
    FragmentData   = 0x0C,
    FragmentLast   = 0x0D,
    // Apple-internal (undocumented by Apple, observed in the wild):
    Name           = 0x11,
    Flags          = 0x13,
}

/// <summary>Pairing method, encoded as the value of a <see cref="Tlv8Tag.Method"/> entry.</summary>
public enum Tlv8Method : byte
{
    PairSetup         = 0x00,
    PairSetupWithAuth = 0x01,
    PairVerify        = 0x02,
    AddPairing        = 0x03,
    RemovePairing     = 0x04,
    ListPairing       = 0x05,
}

/// <summary>HAP pairing-state machine step, encoded as the value of a <see cref="Tlv8Tag.SeqNo"/> entry.</summary>
public enum Tlv8State : byte
{
    M1 = 0x01, M2 = 0x02, M3 = 0x03, M4 = 0x04, M5 = 0x05, M6 = 0x06,
}

/// <summary>HAP error code, encoded as the value of a <see cref="Tlv8Tag.Error"/> entry.</summary>
public enum Tlv8ErrorCode : byte
{
    Unknown        = 0x01,
    Authentication = 0x02,
    BackOff        = 0x03,
    MaxPeers       = 0x04,
    MaxTries       = 0x05,
    Unavailable    = 0x06,
    Busy           = 0x07,
}

/// <summary>Bit flags carried in a <see cref="Tlv8Tag.Flags"/> entry.</summary>
[Flags]
public enum Tlv8Flag : byte
{
    None             = 0,
    TransientPairing = 0x10,
}
