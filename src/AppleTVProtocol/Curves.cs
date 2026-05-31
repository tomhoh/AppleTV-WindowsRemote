// SPDX-License-Identifier: MIT
//
// Ed25519 (signing) and X25519 (ECDH) wrappers for HAP pair-setup /
// pair-verify. .NET 10's BCL still has no managed Ed25519 or X25519, so
// we use BouncyCastle's pure-managed implementation — it keeps single-file
// publish + MSIX simple (no native libsodium binary to extract at startup).
//
// The wrapper surface intentionally mirrors the parts of pyatv's
// hap_srp.py that touch these curves: byte-in / byte-out, no third-party
// types leaked. Calling code in Phase 2 (pair-setup M5/M6 and
// pair-verify M1–M3) should only need:
//
//   Ed25519KeyPair.Generate()  / Ed25519KeyPair.FromPrivateSeed(seed)
//   Ed25519PrivateKey.Sign(msg)
//   Ed25519PublicKey.Verify(msg, sig)
//   X25519KeyPair.Generate()
//   X25519PrivateKey.Agree(otherPublic) -> 32-byte shared secret

using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace AppleTVProtocol;

// ─────────────────────────────── Ed25519 ───────────────────────────────

public sealed class Ed25519PrivateKey
{
    internal readonly Ed25519PrivateKeyParameters Inner;
    /// <summary>32-byte raw private seed.</summary>
    public byte[] Seed => Inner.GetEncoded();

    internal Ed25519PrivateKey(Ed25519PrivateKeyParameters inner) { Inner = inner; }

    public static Ed25519PrivateKey FromSeed(byte[] seed)
    {
        if (seed is null) throw new ArgumentNullException(nameof(seed));
        if (seed.Length != 32)
            throw new ArgumentException($"Ed25519 seed must be 32 bytes, got {seed.Length}", nameof(seed));
        return new Ed25519PrivateKey(new Ed25519PrivateKeyParameters(seed, 0));
    }

    public Ed25519PublicKey PublicKey =>
        new Ed25519PublicKey((Ed25519PublicKeyParameters)Inner.GeneratePublicKey());

    /// <summary>Detached signature of <paramref name="message"/> — 64 bytes.</summary>
    public byte[] Sign(byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, Inner);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }
}

public sealed class Ed25519PublicKey
{
    internal readonly Ed25519PublicKeyParameters Inner;
    /// <summary>32-byte raw public key.</summary>
    public byte[] Bytes => Inner.GetEncoded();

    internal Ed25519PublicKey(Ed25519PublicKeyParameters inner) { Inner = inner; }

    public static Ed25519PublicKey FromBytes(byte[] publicKey)
    {
        if (publicKey is null) throw new ArgumentNullException(nameof(publicKey));
        if (publicKey.Length != 32)
            throw new ArgumentException($"Ed25519 public key must be 32 bytes, got {publicKey.Length}", nameof(publicKey));
        return new Ed25519PublicKey(new Ed25519PublicKeyParameters(publicKey, 0));
    }

    public bool Verify(byte[] message, byte[] signature)
    {
        if (signature is null) throw new ArgumentNullException(nameof(signature));
        if (signature.Length != 64) return false;
        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, Inner);
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }
}

public static class Ed25519KeyPair
{
    public static (Ed25519PrivateKey Priv, Ed25519PublicKey Pub) Generate()
    {
        var seed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var priv = Ed25519PrivateKey.FromSeed(seed);
        return (priv, priv.PublicKey);
    }

    public static (Ed25519PrivateKey Priv, Ed25519PublicKey Pub) FromPrivateSeed(byte[] seed)
    {
        var priv = Ed25519PrivateKey.FromSeed(seed);
        return (priv, priv.PublicKey);
    }
}

// ─────────────────────────────── X25519 ───────────────────────────────

public sealed class X25519PrivateKey
{
    internal readonly X25519PrivateKeyParameters Inner;
    /// <summary>32-byte raw scalar.</summary>
    public byte[] Bytes => Inner.GetEncoded();

    internal X25519PrivateKey(X25519PrivateKeyParameters inner) { Inner = inner; }

    public static X25519PrivateKey FromBytes(byte[] privateScalar)
    {
        if (privateScalar is null) throw new ArgumentNullException(nameof(privateScalar));
        if (privateScalar.Length != 32)
            throw new ArgumentException($"X25519 private scalar must be 32 bytes, got {privateScalar.Length}", nameof(privateScalar));
        return new X25519PrivateKey(new X25519PrivateKeyParameters(privateScalar, 0));
    }

    public X25519PublicKey PublicKey =>
        new X25519PublicKey((X25519PublicKeyParameters)Inner.GeneratePublicKey());

    /// <summary>X25519 ECDH — returns 32-byte raw shared secret.</summary>
    public byte[] Agree(X25519PublicKey other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        var agreement = new X25519Agreement();
        agreement.Init(Inner);
        var shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(other.Inner, shared, 0);
        return shared;
    }
}

public sealed class X25519PublicKey
{
    internal readonly X25519PublicKeyParameters Inner;
    /// <summary>32-byte raw public key.</summary>
    public byte[] Bytes => Inner.GetEncoded();

    internal X25519PublicKey(X25519PublicKeyParameters inner) { Inner = inner; }

    public static X25519PublicKey FromBytes(byte[] publicKey)
    {
        if (publicKey is null) throw new ArgumentNullException(nameof(publicKey));
        if (publicKey.Length != 32)
            throw new ArgumentException($"X25519 public key must be 32 bytes, got {publicKey.Length}", nameof(publicKey));
        return new X25519PublicKey(new X25519PublicKeyParameters(publicKey, 0));
    }
}

public static class X25519KeyPair
{
    public static (X25519PrivateKey Priv, X25519PublicKey Pub) Generate()
    {
        var scalar = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var priv = X25519PrivateKey.FromBytes(scalar);
        return (priv, priv.PublicKey);
    }
}
