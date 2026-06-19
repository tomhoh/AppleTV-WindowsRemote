// SPDX-License-Identifier: MIT
//
// AirPlay pair-setup + pair-verify. Wraps our existing Srp6aClient + TLV8 +
// Ed25519/X25519/ChaCha20-Poly1305/HKDF primitives. Kept separate from the
// Companion HAPPairing so the working Companion flow can't regress.
//
// Wire protocol is plain HTTP/1.1 on port 7000 (see AirPlayHttp). Salts and
// info strings below MUST match pyatv exactly. Direct port of the Swift
// sibling's AirPlayPairing.swift.

using System.Security.Cryptography;
using System.Text;

namespace AppleTVProtocol.AirPlay;

/// <summary>
/// AirPlay HAP credentials, persisted per-device alongside the Companion creds.
///   Ltpk     — the ATV's long-term public key (verify its signatures)
///   Ltsk     — OUR Ed25519 private key (32 B seed)
///   AtvId    — ATV's pairing identifier bytes
///   ClientId — our pairing identifier bytes
/// </summary>
public sealed record AirPlayCredentials(byte[] Ltpk, byte[] Ltsk, byte[] AtvId, byte[] ClientId);

/// <summary>Keys derived after pair-verify, used to encrypt the control channel.</summary>
public sealed record AirPlaySessionKeys(byte[] ReadKey, byte[] WriteKey, byte[] SharedSecret);

public sealed class AirPlayPairingException : Exception
{
    public AirPlayPairingException(string message) : base(message) { }
}

/// <summary>
/// Shared AirPlay HTTP headers. <c>X-Apple-HKP: 3</c> selects the HAP-with-PIN
/// flow; the exact User-Agent gates the TLV8 path on modern tvOS — do not
/// change it without testing against a real Apple TV.
/// </summary>
internal static class AirPlayHeaders
{
    public static readonly Dictionary<string, string> Default = new()
    {
        ["User-Agent"]   = "AirPlay/320.20",
        ["Connection"]   = "keep-alive",
        ["X-Apple-HKP"]  = "3",
        ["Content-Type"] = "application/octet-stream",
    };
}

/// <summary>
/// Drives AirPlay pair-setup. Caller: <see cref="BeginPairingAsync"/> (TV shows
/// a PIN), prompt the user, then <see cref="CompletePairingAsync"/>.
/// </summary>
public sealed class AirPlayPairSetup
{
    private readonly AirPlayHttp _http;
    private readonly Srp6aClient _srp = new();
    private readonly Ed25519PrivateKey _ltKey;
    private readonly byte[] _clientIdBytes;

    private byte[]? _atvSalt;
    private byte[]? _atvPub;

    public AirPlayPairSetup(AirPlayHttp http)
    {
        _http = http;
        var seed = RandomNumberGenerator.GetBytes(32);
        _ltKey = Ed25519PrivateKey.FromSeed(seed);
        _clientIdBytes = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString().ToUpperInvariant());
        _srp.Initialize();
    }

    /// <summary>POST /pair-pin-start + /pair-setup(M1). TV displays the PIN.</summary>
    public async Task BeginPairingAsync(CancellationToken ct = default)
    {
        // Best-effort — some models 404 here yet still honor /pair-setup.
        try { await _http.PostAsync("/pair-pin-start", Array.Empty<byte>(), AirPlayHeaders.Default, ct: ct).ConfigureAwait(false); }
        catch { /* ignore */ }

        var m1 = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.Method, new byte[] { 0x00 } },
            { (byte)Tlv8Tag.SeqNo,  new byte[] { 0x01 } },   // tag 0x06 = State
        });
        var r = await _http.PostAsync("/pair-setup", m1, AirPlayHeaders.Default, ct: ct).ConfigureAwait(false);
        if (r.Status != 200) throw new AirPlayPairingException($"pair-setup M1 HTTP {r.Status}");
        var resp = Tlv8.Read(r.Body);
        ThrowIfError(resp);
        _atvSalt = Require(resp, Tlv8Tag.Salt, "salt");
        _atvPub  = Require(resp, Tlv8Tag.PublicKey, "publicKey");
    }

    /// <summary>POST /pair-setup(M3)+(M5). Returns credentials on success.</summary>
    public async Task<AirPlayCredentials> CompletePairingAsync(string pin, CancellationToken ct = default)
    {
        if (_atvSalt is null || _atvPub is null)
            throw new AirPlayPairingException("BeginPairingAsync not called");

        _srp.SetPin(pin);
        var (A, M1proof) = _srp.ProcessChallenge(_atvSalt, _atvPub);

        var m3 = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.SeqNo,     new byte[] { 0x03 } },
            { (byte)Tlv8Tag.PublicKey, PadLeft(A, 384) },
            { (byte)Tlv8Tag.Proof,     M1proof },
        });
        var r4 = await _http.PostAsync("/pair-setup", m3, AirPlayHeaders.Default, ct: ct).ConfigureAwait(false);
        if (r4.Status != 200) throw new AirPlayPairingException($"pair-setup M3 HTTP {r4.Status}");
        var resp4 = Tlv8.Read(r4.Body);
        ThrowIfError(resp4);
        var atvProof = Require(resp4, Tlv8Tag.Proof, "proof (M4)");
        _srp.VerifyServerProof(M1proof, atvProof);   // throws on PIN mismatch

        // M5: encrypted identity + signature.
        var encryptKey = Crypto.HkdfSha512(_srp.SharedKey, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");
        var signSalt   = Crypto.HkdfSha512(_srp.SharedKey, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info");

        var ltpk       = _ltKey.PublicKey.Bytes;
        var deviceInfo = Concat(signSalt, _clientIdBytes, ltpk);
        var signature  = _ltKey.Sign(deviceInfo);

        var inner = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.Identifier, _clientIdBytes },
            { (byte)Tlv8Tag.PublicKey,  ltpk },
            { (byte)Tlv8Tag.Signature,  signature },
        });
        var chacha = new ChaCha20Poly1305_8ByteNonce(encryptKey);
        var encPayload = chacha.Encrypt(inner, Encoding.ASCII.GetBytes("PS-Msg05"));

        var m5 = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.SeqNo,         new byte[] { 0x05 } },
            { (byte)Tlv8Tag.EncryptedData, encPayload },
        });
        var r6 = await _http.PostAsync("/pair-setup", m5, AirPlayHeaders.Default, ct: ct).ConfigureAwait(false);
        if (r6.Status != 200) throw new AirPlayPairingException($"pair-setup M5 HTTP {r6.Status}");
        var resp6 = Tlv8.Read(r6.Body);
        ThrowIfError(resp6);
        var encData6 = Require(resp6, Tlv8Tag.EncryptedData, "encryptedData (M6)");

        var plain   = chacha.Decrypt(encData6, Encoding.ASCII.GetBytes("PS-Msg06"));
        var innerM6 = Tlv8.Read(plain);
        var atvId   = Require(innerM6, Tlv8Tag.Identifier, "atv identifier");
        var atvLtpk = Require(innerM6, Tlv8Tag.PublicKey,  "atv ltpk");

        return new AirPlayCredentials(Ltpk: atvLtpk, Ltsk: _ltKey.Seed, AtvId: atvId, ClientId: _clientIdBytes);
    }

    private static byte[] PadLeft(byte[] data, int length)
    {
        if (data.Length >= length) return data;
        var padded = new byte[length];
        data.CopyTo(padded, length - data.Length);
        return padded;
    }

    internal static void ThrowIfError(Dictionary<byte, byte[]> tlv)
    {
        if (tlv.TryGetValue((byte)Tlv8Tag.Error, out var err) && err.Length > 0)
            throw new AirPlayPairingException($"server reported error={err[0]}");
    }

    internal static byte[] Require(Dictionary<byte, byte[]> tlv, Tlv8Tag tag, string name)
        => tlv.TryGetValue((byte)tag, out var v) ? v : throw new AirPlayPairingException($"missing TLV: {name}");

    internal static byte[] Concat(params byte[][] parts)
    {
        int total = 0; foreach (var p in parts) total += p.Length;
        var outp = new byte[total];
        int o = 0; foreach (var p in parts) { p.CopyTo(outp, o); o += p.Length; }
        return outp;
    }
}

/// <summary>
/// AirPlay pair-verify. Returns control-channel session keys on success; the
/// caller then switches the TCP stream to encrypted framing.
/// </summary>
public sealed class AirPlayPairVerify
{
    private readonly AirPlayHttp _http;
    private readonly AirPlayCredentials _creds;
    private readonly X25519PrivateKey _ephemeral;
    private readonly byte[] _ourPub;

    public AirPlayPairVerify(AirPlayHttp http, AirPlayCredentials credentials)
    {
        _http = http;
        _creds = credentials;
        var (priv, pub) = X25519KeyPair.Generate();
        _ephemeral = priv;
        _ourPub = pub.Bytes;
    }

    public async Task<AirPlaySessionKeys> VerifyAsync(CancellationToken ct = default)
    {
        // M1: { state=1, publicKey=ourEphemeralPub }
        var m1 = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.SeqNo,     new byte[] { 0x01 } },
            { (byte)Tlv8Tag.PublicKey, _ourPub },
        });
        var r2 = await _http.PostAsync("/pair-verify", m1, AirPlayHeaders.Default, ct: ct).ConfigureAwait(false);
        if (r2.Status != 200) throw new AirPlayPairingException($"pair-verify M1 HTTP {r2.Status}");
        var resp2 = Tlv8.Read(r2.Body);
        AirPlayPairSetup.ThrowIfError(resp2);
        var atvPub  = AirPlayPairSetup.Require(resp2, Tlv8Tag.PublicKey,    "publicKey");
        var encData = AirPlayPairSetup.Require(resp2, Tlv8Tag.EncryptedData, "encryptedData");

        var shared     = _ephemeral.Agree(X25519PublicKey.FromBytes(atvPub));
        var sessionKey = Crypto.HkdfSha512(shared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");

        var chacha = new ChaCha20Poly1305_8ByteNonce(sessionKey);
        var plain  = chacha.Decrypt(encData, Encoding.ASCII.GetBytes("PV-Msg02"));
        var inner  = Tlv8.Read(plain);
        var atvId  = AirPlayPairSetup.Require(inner, Tlv8Tag.Identifier, "atv identifier");
        var atvSig = AirPlayPairSetup.Require(inner, Tlv8Tag.Signature,  "atv signature");

        if (!atvId.AsSpan().SequenceEqual(_creds.AtvId))
            throw new AirPlayPairingException("atv id mismatch — wrong device or stale creds");

        var atvLt = Ed25519PublicKey.FromBytes(_creds.Ltpk);
        if (!atvLt.Verify(AirPlayPairSetup.Concat(atvPub, atvId, _ourPub), atvSig))
            throw new AirPlayPairingException("ATV signature invalid — wrong ltpk or MITM");

        // M3: sign (ourPub || ourID || atvPub) with our LT key, encrypt, send.
        var signKey   = Ed25519PrivateKey.FromSeed(_creds.Ltsk);
        var signature = signKey.Sign(AirPlayPairSetup.Concat(_ourPub, _creds.ClientId, atvPub));
        var m3Inner = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.Identifier, _creds.ClientId },
            { (byte)Tlv8Tag.Signature,  signature },
        });
        var sealed3 = chacha.Encrypt(m3Inner, Encoding.ASCII.GetBytes("PV-Msg03"));
        var m3 = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.SeqNo,         new byte[] { 0x03 } },
            { (byte)Tlv8Tag.EncryptedData, sealed3 },
        });
        var r4 = await _http.PostAsync("/pair-verify", m3, AirPlayHeaders.Default, ct: ct).ConfigureAwait(false);
        if (r4.Status != 200) throw new AirPlayPairingException($"pair-verify M3 HTTP {r4.Status}");
        AirPlayPairSetup.ThrowIfError(Tlv8.Read(r4.Body));

        var writeKey = Crypto.HkdfSha512(shared, "Control-Salt", "Control-Write-Encryption-Key");
        var readKey  = Crypto.HkdfSha512(shared, "Control-Salt", "Control-Read-Encryption-Key");
        return new AirPlaySessionKeys(ReadKey: readKey, WriteKey: writeKey, SharedSecret: shared);
    }
}
