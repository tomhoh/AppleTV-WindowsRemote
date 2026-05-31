// SPDX-License-Identifier: MIT
//
// Crypto primitives for HAP pairing & Companion session encryption.
//
// Ports the equivalents of pyatv/auth/hap_srp.py and pyatv/support/chacha20.py
// to C#. Built-in algorithms (SHA-512, HKDF, ChaCha20-Poly1305) wrap
// System.Security.Cryptography. SRP-6a is hand-rolled because .NET has no
// built-in SRP and the Python `srptools` package isn't a thing in .NET land.
//
// Curve25519 / Ed25519 are still missing from the .NET BCL as of .NET 10 —
// those will land via NSec (libsodium-based) when Phase 2's pair-verify
// flow needs them. SRP-6a is enough to drive pair-setup M1-M4.

using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AppleTVProtocol;

public static class Crypto
{
    /// <summary>
    /// HKDF-SHA512: derive a key from <paramref name="ikm"/> using
    /// <paramref name="salt"/> + <paramref name="info"/>. Output length
    /// is 32 bytes by default to match pyatv's <c>hkdf_expand</c>.
    /// </summary>
    public static byte[] HkdfSha512(byte[] ikm, byte[] salt, byte[] info, int outputLength = 32)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA512, ikm, outputLength, salt, info);
    }

    /// <summary>String-arg overload — common for the HAP fixed labels.</summary>
    public static byte[] HkdfSha512(byte[] ikm, string salt, string info, int outputLength = 32)
        => HkdfSha512(ikm, Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info), outputLength);
}

// ──────────────────────────────────────────────────────────────────────────
// ChaCha20-Poly1305 with HAP's 8-byte-nonce convention
// ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// ChaCha20-Poly1305 AEAD with HAP's "8-byte nonce" convention — the
/// nonce is given as 8 bytes and zero-padded to the 12 bytes the
/// underlying cipher requires.
///
/// Mirrors pyatv's <c>Chacha20Cipher8byteNonce</c>. Used for
/// pair-setup M5/M6 and pair-verify M2/M3 envelopes; the subsequent
/// EncryptedFrameTransport (every post-pair-verify message) uses
/// a different counter-based nonce scheme.
/// </summary>
public sealed class ChaCha20Poly1305_8ByteNonce
{
    private readonly ChaCha20Poly1305 _cipher;

    public ChaCha20Poly1305_8ByteNonce(byte[] key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (key.Length != 32)
            throw new ArgumentException($"key must be 32 bytes, got {key.Length}", nameof(key));
        _cipher = new ChaCha20Poly1305(key);
    }

    public byte[] Encrypt(byte[] plaintext, byte[] nonce8, byte[]? aad = null)
    {
        if (nonce8.Length != 8)
            throw new ArgumentException($"nonce must be 8 bytes, got {nonce8.Length}", nameof(nonce8));
        var nonce12 = new byte[12];
        Array.Copy(nonce8, 0, nonce12, 4, 8);  // zero-prefix to 12 bytes
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        _cipher.Encrypt(nonce12, plaintext, ciphertext, tag, aad);
        // HAP convention: ciphertext || tag concatenated
        var output = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(output, 0);
        tag.CopyTo(output, ciphertext.Length);
        return output;
    }

    public byte[] Decrypt(byte[] ciphertextAndTag, byte[] nonce8, byte[]? aad = null)
    {
        if (nonce8.Length != 8)
            throw new ArgumentException($"nonce must be 8 bytes, got {nonce8.Length}", nameof(nonce8));
        if (ciphertextAndTag.Length < 16)
            throw new ArgumentException("input too short to contain a tag");
        var nonce12 = new byte[12];
        Array.Copy(nonce8, 0, nonce12, 4, 8);
        int ciphertextLen = ciphertextAndTag.Length - 16;
        var ciphertext = new byte[ciphertextLen];
        var tag = new byte[16];
        Array.Copy(ciphertextAndTag, 0, ciphertext, 0, ciphertextLen);
        Array.Copy(ciphertextAndTag, ciphertextLen, tag, 0, 16);
        var plaintext = new byte[ciphertextLen];
        _cipher.Decrypt(nonce12, ciphertext, tag, plaintext, aad);
        return plaintext;
    }
}

// ──────────────────────────────────────────────────────────────────────────
// SRP-6a (HAP variant: 3072-bit prime, generator 5, SHA-512)
// ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// SRP-6a client for HAP pair-setup. Implements the four-step flow:
///   1. <see cref="Initialize"/> — generate or accept the ephemeral
///      private value `a`.
///   2. <see cref="SetCredentials"/> — store the PIN.
///   3. <see cref="ProcessChallenge"/> — given the server's salt and B,
///      compute the client's public A and proof M1.
///   4. <see cref="VerifyServerProof"/> — confirm the server's M2.
///
/// On success, <see cref="SharedKey"/> contains the SHA-512 hash of the
/// agreed SRP shared secret, ready to be passed through HKDF for session
/// key derivation.
///
/// Parameters per HAP: 3072-bit safe prime from RFC 5054, generator 5,
/// SHA-512, identity = "Pair-Setup".
/// </summary>
public sealed class Srp6aClient
{
    private const string PairSetupIdentity = "Pair-Setup";

    // RFC 5054 group 3072 (Modulus).
    private static readonly BigInteger N = BigIntFromHex(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3D" +
        "C2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D" +
        "670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9" +
        "DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64" +
        "ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6B" +
        "F12FFA06D98A0864D87602733EC86A64521F2B18177B200C" +
        "BBE117577A615D6C770988C0BAD946E208E24FA074E5AB31" +
        "43DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF");
    private static readonly BigInteger G = new(5);
    private static readonly int NLengthBytes = 384;  // 3072 bits

    private BigInteger _a;        // client private ephemeral
    private BigInteger _A;        // client public ephemeral = g^a mod N
    private string? _pin;
    private byte[]? _sharedKey;   // H(S) — the value we expose to HKDF

    /// <summary>
    /// Initialise with caller-provided private ephemeral bytes. If
    /// <paramref name="aBytes"/> is null, generates 32 bytes from
    /// <see cref="RandomNumberGenerator"/>.
    /// </summary>
    /// <param name="aBytes">
    /// Big-endian bytes for the ephemeral `a`. pyatv passes the Ed25519
    /// signing key here so the same value isn't generated twice (and so
    /// tests can pin it).
    /// </param>
    public void Initialize(byte[]? aBytes = null)
    {
        aBytes ??= RandomNumberGenerator.GetBytes(32);
        // Construct as positive big-endian BigInteger.
        var withSign = new byte[aBytes.Length + 1];
        Array.Copy(aBytes, 0, withSign, 1, aBytes.Length);
        Array.Reverse(withSign);  // BigInteger ctor is little-endian
        _a = new BigInteger(withSign);
        _A = BigInteger.ModPow(G, _a, N);
    }

    public void SetPin(string pin) => _pin = pin;

    /// <summary>
    /// Given the server's salt and public ephemeral B, compute and return
    /// the client's public A and the proof M1. After this call,
    /// <see cref="SharedKey"/> is populated and ready for HKDF.
    /// </summary>
    public (byte[] A, byte[] M1) ProcessChallenge(byte[] salt, byte[] B_bytes)
    {
        if (_pin is null) throw new InvalidOperationException("SetPin first");
        if (_a.IsZero) throw new InvalidOperationException("Initialize first");

        var B = BigIntFromBigEndian(B_bytes);
        // Server-public-key sanity check: B mod N == 0 means abort.
        if ((B % N).IsZero) throw new ArgumentException("Invalid server public key B");

        // k = H(PAD(N) | PAD(g))
        byte[] nPadded = PadLeft(BigIntToBigEndian(N), NLengthBytes);
        byte[] gPadded = PadLeft(BigIntToBigEndian(G), NLengthBytes);
        var k = BigIntFromBigEndian(Sha512(nPadded, gPadded));

        // x = H(salt | H(I + ":" + P))
        byte[] innerHash = Sha512(Encoding.ASCII.GetBytes($"{PairSetupIdentity}:{_pin}"));
        var x = BigIntFromBigEndian(Sha512(salt, innerHash));

        // u = H(PAD(A) | PAD(B))
        byte[] aPadded = PadLeft(BigIntToBigEndian(_A), NLengthBytes);
        byte[] bPadded = PadLeft(B_bytes, NLengthBytes);
        var u = BigIntFromBigEndian(Sha512(aPadded, bPadded));

        // S = (B - k * g^x)^(a + u*x) mod N
        var gx = BigInteger.ModPow(G, x, N);
        var diff = ((B - k * gx) % N + N) % N;  // ensure positive
        var exponent = _a + u * x;
        var S = BigInteger.ModPow(diff, exponent, N);

        // K = H(S) — S is hashed as its MINIMAL big-endian bytes (no padding),
        // matching srptools' int_to_bytes semantics. pyatv's hap_srp.py inherits
        // this from srptools so HAP wire compatibility depends on it.
        byte[] sBytes = BigIntToBigEndian(S);
        _sharedKey = Sha512(sBytes);

        // M1 = H(H(N) XOR H(g) | H(I) | salt | A | B | K)
        // srptools hashes N and g as their MINIMAL big-endian bytes here (not
        // padded to NLengthBytes). For HAP's 3072-bit prime, N's minimal form
        // happens to be exactly 384 bytes (high byte = 0xFF), but g is just
        // a single 0x05. A and B are also passed unpadded (in this fixture
        // they happen to be 384 bytes, but we should not rely on that).
        byte[] nBytes  = BigIntToBigEndian(N);
        byte[] gBytes  = BigIntToBigEndian(G);
        byte[] aBytes  = BigIntToBigEndian(_A);
        byte[] bMinBytes = BigIntToBigEndian(B);

        byte[] hN = Sha512(nBytes);
        byte[] hG = Sha512(gBytes);
        byte[] hNxorG = new byte[hN.Length];
        for (int i = 0; i < hN.Length; i++) hNxorG[i] = (byte)(hN[i] ^ hG[i]);
        byte[] hI = Sha512(Encoding.ASCII.GetBytes(PairSetupIdentity));

        byte[] M1 = Sha512(hNxorG, hI, salt, aBytes, bMinBytes, _sharedKey);

        return (aPadded, M1);
    }

    /// <summary>
    /// Validate the server's proof M2 against locally-computed
    /// H(A | M1 | K). Throws if mismatched.
    /// </summary>
    public void VerifyServerProof(byte[] M1, byte[] M2)
    {
        if (_sharedKey is null)
            throw new InvalidOperationException("ProcessChallenge must run first");
        // Server proof M2 = H(A | M1 | K) — A as minimal big-endian bytes
        // (matching srptools' `get_common_session_key_proof_hash`).
        byte[] aBytes = BigIntToBigEndian(_A);
        byte[] expected = Sha512(aBytes, M1, _sharedKey);
        if (!CryptographicOperations.FixedTimeEquals(expected, M2))
            throw new CryptographicException("SRP-6a: server proof M2 mismatch");
    }

    /// <summary>
    /// SHA-512 of the agreed SRP shared secret S. Available after
    /// <see cref="ProcessChallenge"/>. This is the value HAP feeds into
    /// HKDF to derive the pair-setup-encrypt / controller-sign keys.
    /// </summary>
    public byte[] SharedKey =>
        _sharedKey ?? throw new InvalidOperationException("ProcessChallenge must run first");

    // ── Helpers ──

    private static byte[] Sha512(params byte[][] chunks)
    {
        using var sha = SHA512.Create();
        foreach (var chunk in chunks)
        {
            sha.TransformBlock(chunk, 0, chunk.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash!;
    }

    /// <summary>Big-endian → BigInteger (always interpreted as unsigned/positive).</summary>
    internal static BigInteger BigIntFromBigEndian(byte[] beBytes)
    {
        // BigInteger(byte[]) is little-endian + signed; prepend a 0 to force positive
        // and reverse to little-endian.
        var le = new byte[beBytes.Length + 1];
        Array.Copy(beBytes, 0, le, 1, beBytes.Length);
        Array.Reverse(le);
        return new BigInteger(le);
    }

    /// <summary>BigInteger → big-endian, minimal length (no leading zeros).</summary>
    internal static byte[] BigIntToBigEndian(BigInteger value)
    {
        // Always emit positive (unsigned).
        var le = value.ToByteArray(isUnsigned: true);
        Array.Reverse(le);
        return le;
    }

    /// <summary>Left-pad with zeros to <paramref name="length"/> bytes.</summary>
    internal static byte[] PadLeft(byte[] input, int length)
    {
        if (input.Length == length) return input;
        if (input.Length > length)
            throw new ArgumentException($"input ({input.Length}) > target ({length})");
        var output = new byte[length];
        Array.Copy(input, 0, output, length - input.Length, input.Length);
        return output;
    }

    private static BigInteger BigIntFromHex(string hex)
    {
        var bytes = Convert.FromHexString(hex);  // big-endian
        return BigIntFromBigEndian(bytes);
    }
}
