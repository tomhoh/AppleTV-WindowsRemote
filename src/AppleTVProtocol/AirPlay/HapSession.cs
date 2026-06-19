// SPDX-License-Identifier: MIT
//
// Bidirectional ChaCha20-Poly1305 frame codec used over the AirPlay
// control / event / data TCP sockets once pair-verify has derived
// per-direction keys. Direct port of the Swift sibling's HAPSession.swift.
//
//   one frame = [2-byte length LE] [ciphertext] [16-byte Poly1305 tag]
//     - AAD is the 2-byte length header itself
//     - nonce is an 8-byte little-endian counter LEFT-padded with 4 zero
//       bytes to a 12-byte ChaCha20 nonce
//     - counters are per-direction and never reset
//     - plaintext capped at 1024 B per frame; larger inputs are split
//
// Reference: pyatv/auth/hap_session.py + pyatv/support/chacha20.py.

using System.Security.Cryptography;

namespace AppleTVProtocol.AirPlay;

public sealed class HapSession
{
    /// <summary>Max plaintext bytes per frame — larger inputs are split.</summary>
    public const int MaxFramePlaintext = 1024;

    private readonly ChaCha20Poly1305 _write;
    private readonly ChaCha20Poly1305 _read;
    private ulong _writeCounter;
    private ulong _readCounter;

    // Inbound byte buffer — raw socket bytes accumulate here; Feed returns
    // whatever complete plaintext frames are now decryptable.
    private readonly List<byte> _inbound = new();

    public HapSession(byte[] writeKey, byte[] readKey)
    {
        if (writeKey is null || writeKey.Length != 32) throw new ArgumentException("writeKey must be 32 bytes", nameof(writeKey));
        if (readKey  is null || readKey.Length  != 32) throw new ArgumentException("readKey must be 32 bytes",  nameof(readKey));
        _write = new ChaCha20Poly1305(writeKey);
        _read  = new ChaCha20Poly1305(readKey);
    }

    private static byte[] Nonce(ulong counter)
    {
        // 8-byte counter little-endian, LEFT-padded to 12 bytes with zeros.
        var nonce = new byte[12];
        for (int i = 0; i < 8; i++)
            nonce[4 + i] = (byte)((counter >> (8 * i)) & 0xFF);
        return nonce;
    }

    /// <summary>Encrypt plaintext into one or more framed ciphertext chunks.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var output = new List<byte>(plaintext.Length + 32);
        int offset = 0;
        while (offset < plaintext.Length)
        {
            int chunkLen = Math.Min(plaintext.Length - offset, MaxFramePlaintext);
            var chunk = plaintext.Slice(offset, chunkLen);
            offset += chunkLen;

            var header = new byte[] { (byte)(chunkLen & 0xFF), (byte)((chunkLen >> 8) & 0xFF) };
            var cipher = new byte[chunkLen];
            var tag    = new byte[16];
            _write.Encrypt(Nonce(_writeCounter), chunk, cipher, tag, header);
            _writeCounter++;

            output.AddRange(header);
            output.AddRange(cipher);
            output.AddRange(tag);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Feed raw socket bytes. Returns every complete plaintext frame now
    /// decryptable; partial frames remain buffered for the next call.
    /// </summary>
    public byte[] Feed(ReadOnlySpan<byte> bytes)
    {
        _inbound.AddRange(bytes.ToArray());
        var output = new List<byte>();

        while (true)
        {
            if (_inbound.Count < 2) break;
            int plainLen = _inbound[0] | (_inbound[1] << 8);
            int frameLen = 2 + plainLen + 16;
            if (_inbound.Count < frameLen) break;

            var header = new byte[] { _inbound[0], _inbound[1] };
            var cipher = new byte[plainLen];
            for (int i = 0; i < plainLen; i++) cipher[i] = _inbound[2 + i];
            var tag = new byte[16];
            for (int i = 0; i < 16; i++) tag[i] = _inbound[2 + plainLen + i];

            var plain = new byte[plainLen];
            try
            {
                _read.Decrypt(Nonce(_readCounter), cipher, tag, plain, header);
            }
            catch (CryptographicException)
            {
                // Drop the bad frame so we don't spin, then surface it.
                _inbound.RemoveRange(0, frameLen);
                throw new InvalidOperationException("HAP session frame: Poly1305 tag mismatch");
            }
            _readCounter++;
            output.AddRange(plain);
            _inbound.RemoveRange(0, frameLen);
        }
        return output.ToArray();
    }
}
