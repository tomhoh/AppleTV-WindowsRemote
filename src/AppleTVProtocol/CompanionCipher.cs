// SPDX-License-Identifier: MIT
//
// Post-pair-verify encryption layer for the Companion protocol.
//
// Mirrors pyatv's `Chacha20Cipher` (nonce_length=12). One cipher object
// per connection holds two ChaCha20-Poly1305 instances — one for outgoing
// frames, one for incoming — each with its own monotonically incrementing
// 64-bit counter rendered as a 12-byte little-endian nonce.
//
// The 4-byte Companion frame header is used as AAD on every frame.

using System.Security.Cryptography;

namespace AppleTVProtocol;

/// <summary>
/// ChaCha20-Poly1305 with separate out/in keys + counter-based 12-byte
/// nonces. Used by <c>CompanionConnection</c> once pair-verify has
/// finished and the session keys are known.
/// </summary>
public sealed class CompanionCipher
{
    private readonly ChaCha20Poly1305 _encOut;
    private readonly ChaCha20Poly1305 _encIn;
    private ulong _outCounter;
    private ulong _inCounter;

    public CompanionCipher(byte[] outKey, byte[] inKey)
    {
        if (outKey is null || outKey.Length != 32)
            throw new ArgumentException("outKey must be 32 bytes", nameof(outKey));
        if (inKey is null || inKey.Length != 32)
            throw new ArgumentException("inKey must be 32 bytes", nameof(inKey));
        _encOut = new ChaCha20Poly1305(outKey);
        _encIn  = new ChaCha20Poly1305(inKey);
    }

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> with the next outbound nonce.
    /// Returns ciphertext concatenated with the 16-byte Poly1305 tag.
    /// The <paramref name="aad"/> is typically the 4-byte frame header.
    /// </summary>
    public byte[] Encrypt(byte[] plaintext, byte[]? aad = null)
    {
        var nonce = NonceFor(_outCounter++);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        _encOut.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        var output = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, output, 0,                ciphertext.Length);
        Buffer.BlockCopy(tag,        0, output, ciphertext.Length, tag.Length);
        return output;
    }

    /// <summary>
    /// Decrypt <paramref name="ciphertextAndTag"/> with the next inbound
    /// nonce. <paramref name="aad"/> must match what the peer signed with
    /// (typically the 4-byte frame header).
    /// </summary>
    public byte[] Decrypt(byte[] ciphertextAndTag, byte[]? aad = null)
    {
        if (ciphertextAndTag.Length < 16)
            throw new ArgumentException("input too short to contain a Poly1305 tag");
        int ctLen = ciphertextAndTag.Length - 16;
        var nonce = NonceFor(_inCounter++);
        var ciphertext = new byte[ctLen];
        var tag = new byte[16];
        Buffer.BlockCopy(ciphertextAndTag, 0,     ciphertext, 0, ctLen);
        Buffer.BlockCopy(ciphertextAndTag, ctLen, tag,        0, 16);
        var plaintext = new byte[ctLen];
        _encIn.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    /// <summary>Counter value that will be used on the next outbound encrypt.</summary>
    public ulong OutCounter => _outCounter;
    /// <summary>Counter value that will be used on the next inbound decrypt.</summary>
    public ulong InCounter  => _inCounter;

    /// <summary>12-byte little-endian nonce derived from a 64-bit counter.</summary>
    internal static byte[] NonceFor(ulong counter)
    {
        var nonce = new byte[12];
        // counter occupies the LOW 8 bytes (little-endian), upper 4 bytes stay 0
        BinaryWrite64LittleEndian(nonce, counter);
        return nonce;
    }

    private static void BinaryWrite64LittleEndian(Span<byte> destination, ulong value)
    {
        destination[0] = (byte) value;
        destination[1] = (byte)(value >>  8);
        destination[2] = (byte)(value >> 16);
        destination[3] = (byte)(value >> 24);
        destination[4] = (byte)(value >> 32);
        destination[5] = (byte)(value >> 40);
        destination[6] = (byte)(value >> 48);
        destination[7] = (byte)(value >> 56);
    }
}
