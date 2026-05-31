// SPDX-License-Identifier: MIT
//
// Companion-protocol framing layer (binary, over raw TCP — not HTTP).
//
// Each frame on the wire is:
//
//   ┌──────────┬──────────────────────┬─────────────────────────┐
//   │ 1 byte   │ 3 bytes (big-endian) │ N bytes                  │
//   │ FrameTyp │ payload length       │ payload                  │
//   └──────────┴──────────────────────┴─────────────────────────┘
//
// When the connection is encrypted (after pair-verify), the 4-byte header
// is fed in as AAD and the payload is ChaCha20-Poly1305 ciphertext
// followed by the 16-byte Poly1305 tag (i.e. payload_length includes the
// 16-byte tag — that's what `CompanionConnection.Send` accounts for).
//
// This file is pure data: it encodes and decodes frames against in-memory
// byte buffers and has no IO. The actual TCP socket lives in
// `CompanionConnection`.

namespace AppleTVProtocol;

/// <summary>
/// Companion-protocol frame types. Values match pyatv's <c>FrameType</c>
/// enum byte-for-byte.
/// </summary>
public enum CompanionFrameType : byte
{
    Unknown = 0,
    NoOp = 1,
    PS_Start = 3,                    // Pair-Setup, first frame
    PS_Next = 4,                     // Pair-Setup, subsequent frames + responses
    PV_Start = 5,                    // Pair-Verify, first frame
    PV_Next = 6,                     // Pair-Verify, subsequent frames + responses
    U_OPACK = 7,                     // OPACK over the unencrypted channel
    E_OPACK = 8,                     // OPACK over the encrypted channel (most app traffic)
    P_OPACK = 9,                     // (rare) OPACK with pairing-data envelope
    PA_Req = 10,
    PA_Rsp = 11,
    SessionStartRequest = 16,
    SessionStartResponse = 17,
    SessionData = 18,
    FamilyIdentityRequest = 32,
    FamilyIdentityResponse = 33,
    FamilyIdentityUpdate = 34,
}

/// <summary>
/// A decoded Companion frame: a 1-byte type and an opaque payload buffer.
/// </summary>
public readonly record struct CompanionFrame(CompanionFrameType Type, byte[] Payload)
{
    public const int HeaderLength = 4;
    public const int AuthTagLength = 16;

    /// <summary>
    /// Encode a plaintext (unencrypted) frame: header + raw payload.
    /// </summary>
    public byte[] Encode() => EncodeFrame(Type, Payload);

    /// <summary>
    /// Encode a frame header for an arbitrary <paramref name="payloadLength"/>
    /// value (so callers that need the header as AAD before encrypting
    /// don't have to construct the full frame first).
    /// </summary>
    public static byte[] EncodeHeader(CompanionFrameType type, int payloadLength)
    {
        if ((uint)payloadLength > 0xFF_FFFF)
            throw new ArgumentOutOfRangeException(nameof(payloadLength),
                "Companion frame payload length is a 24-bit field; max is 16_777_215.");
        return new byte[]
        {
            (byte)type,
            (byte)((payloadLength >> 16) & 0xFF),
            (byte)((payloadLength >>  8) & 0xFF),
            (byte) (payloadLength        & 0xFF),
        };
    }

    /// <summary>
    /// Encode a complete frame (header + payload) as a single buffer.
    /// </summary>
    public static byte[] EncodeFrame(CompanionFrameType type, byte[] payload)
    {
        var header = EncodeHeader(type, payload.Length);
        var output = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header,  0, output, 0,              header.Length);
        Buffer.BlockCopy(payload, 0, output, header.Length,  payload.Length);
        return output;
    }

    /// <summary>
    /// Try to pull a single frame out of <paramref name="buffer"/>. Returns
    /// <c>null</c> if there isn't a complete frame yet. On success,
    /// <paramref name="consumed"/> is the number of bytes that should be
    /// removed from the head of the caller's buffer.
    /// </summary>
    public static CompanionFrame? TryDecode(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.Length < HeaderLength) return null;

        int payloadLength = (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
        int total = HeaderLength + payloadLength;
        if (buffer.Length < total) return null;

        var type = (CompanionFrameType)buffer[0];
        var payload = buffer.Slice(HeaderLength, payloadLength).ToArray();
        consumed = total;
        return new CompanionFrame(type, payload);
    }
}
