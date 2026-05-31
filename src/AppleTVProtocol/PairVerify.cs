// SPDX-License-Identifier: MIT
//
// HAP pair-verify for the Companion protocol.
//
// Pair-verify runs on every reconnect, using the long-term credentials
// stored from pair-setup. Three frames exchange a fresh X25519 ECDH
// shared secret and prove possession of the long-term Ed25519 key on
// each side.
//
//   M1 -> {SeqNo=M1, PublicKey=client_x25519_pub}
//   M2 <- {SeqNo=M2, PublicKey=server_x25519_pub, EncryptedData=enc(identifier + signature)}
//   M3 -> {SeqNo=M3, EncryptedData=enc(client_id + signature)}
//
// The encryption uses ChaCha20-Poly1305 with HAP's 8-byte-nonce
// convention; the AEAD key is HKDF-SHA512(K_shared,
// "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info"). After M3 is
// sent, the same shared secret produces the long-lived Companion session
// keys via HKDF(K_shared, "", "ClientEncrypt-main" / "ServerEncrypt-main").

using System.Text;

namespace AppleTVProtocol;

/// <summary>
/// Pure-data envelope helpers — same shape as <see cref="PairSetupMessages"/>
/// but with the pair-verify auth-type marker (<c>_auTy</c>) added on
/// outbound frames (matches pyatv's behaviour).
/// </summary>
public static class PairVerifyMessages
{
    public const string PairingDataKey = "_pd";
    public const string AuthTypeKey    = "_auTy";

    /// <summary>M1 outbound — fresh ECDH public. Matches Swift sibling's
    /// wrapPvStartData: dict({_pd, _auTy: 4}), no _x.</summary>
    public static byte[] BuildM1(byte[] clientX25519Public)
    {
        var tlv = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.SeqNo,     new[] { (byte)Tlv8State.M1 } },
            { (byte)Tlv8Tag.PublicKey, clientX25519Public },
        });
        return Opack.Pack(new Dictionary<string, object?>
        {
            { PairingDataKey, tlv },
            { AuthTypeKey,    4 },
        });
    }

    /// <summary>M3 outbound — encrypted identifier + signature.
    /// Matches Swift sibling's wrapPairingData: dict({_pd}), no _x, no _auTy.</summary>
    public static byte[] BuildM3(byte[] encryptedData)
    {
        var tlv = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.SeqNo,         new[] { (byte)Tlv8State.M3 } },
            { (byte)Tlv8Tag.EncryptedData, encryptedData },
        });
        return Opack.Pack(new Dictionary<string, object?> { { PairingDataKey, tlv } });
    }

    public static Dictionary<byte, byte[]> ParsePairingData(byte[] opackEnvelope)
        => PairSetupMessages.ParsePairingData(opackEnvelope);   // identical envelope layout
}

/// <summary>
/// Result of a successful pair-verify: the per-session out/in encryption
/// keys to install on the underlying <see cref="CompanionConnection"/>.
/// </summary>
public sealed record CompanionSessionKeys(byte[] OutKey, byte[] InKey);

/// <summary>
/// Async driver for HAP pair-verify on the Companion protocol.
/// </summary>
public sealed class CompanionPairVerify
{
    private const string EncryptSalt   = "Pair-Verify-Encrypt-Salt";
    private const string EncryptInfo   = "Pair-Verify-Encrypt-Info";
    public  const string SessionSalt   = "";
    public  const string OutInfoLabel  = "ClientEncrypt-main";
    public  const string InInfoLabel   = "ServerEncrypt-main";

    private readonly CompanionConnection _conn;
    private readonly TimeSpan _timeout;

    public CompanionPairVerify(CompanionConnection conn, TimeSpan? timeout = null)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Run pair-verify and, on success, install the derived session keys
    /// on the connection (so subsequent frames are encrypted).
    /// </summary>
    /// <param name="credentials">
    /// The HAP long-term credentials returned by <see cref="CompanionPairSetup"/>.
    /// </param>
    /// <param name="ephemeralX25519Seed">
    /// 32-byte X25519 private scalar; generated fresh if null. Tests can
    /// pin this so the wire bytes are deterministic.
    /// </param>
    public async Task<CompanionSessionKeys> RunAsync(
        HapCredentials credentials,
        byte[]? ephemeralX25519Seed = null,
        CancellationToken cancel = default)
    {
        if (credentials is null) throw new ArgumentNullException(nameof(credentials));

        ephemeralX25519Seed ??= System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var clientVerify = X25519PrivateKey.FromBytes(ephemeralX25519Seed);
        var clientPub    = clientVerify.PublicKey.Bytes;

        // ── frame-receive plumbing — same pattern as PairSetup ──
        var pending = new Queue<byte[]>();
        var pendingWait = new SemaphoreSlim(0);
        EventHandler<CompanionFrame> handler = (_, frame) =>
        {
            if (frame.Type == CompanionFrameType.PV_Next)
            {
                lock (pending) pending.Enqueue(frame.Payload);
                pendingWait.Release();
            }
        };
        _conn.FrameReceived += handler;

        async Task<Dictionary<byte, byte[]>> AwaitReplyAsync()
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeoutCts.CancelAfter(_timeout);
            await pendingWait.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            byte[] payload;
            lock (pending) payload = pending.Dequeue();
            return PairVerifyMessages.ParsePairingData(payload);
        }

        try
        {
            // ── M1 → ── ── M2 ← ──
            await _conn.SendAsync(CompanionFrameType.PV_Start, PairVerifyMessages.BuildM1(clientPub), cancel)
                .ConfigureAwait(false);

            var m2 = await AwaitReplyAsync().ConfigureAwait(false);
            if (!m2.TryGetValue((byte)Tlv8Tag.PublicKey,     out var serverPub) ||
                !m2.TryGetValue((byte)Tlv8Tag.EncryptedData, out var encryptedFromTV))
                throw new HapPairingException("M2 missing public-key or encrypted-data");

            // ── derive shared secret + AEAD key ──
            byte[] shared    = clientVerify.Agree(X25519PublicKey.FromBytes(serverPub));
            byte[] sessionKey= Crypto.HkdfSha512(shared, EncryptSalt, EncryptInfo);

            // Decrypt and verify the TV's proof.
            var chacha = new ChaCha20Poly1305_8ByteNonce(sessionKey);
            byte[] decryptedM2;
            try
            {
                decryptedM2 = chacha.Decrypt(encryptedFromTV, Encoding.ASCII.GetBytes("PV-Msg02"));
            }
            catch (Exception ex)
            {
                throw new HapPairingException("pair-verify M2 decrypt failed (stale credentials?)", ex);
            }

            var innerM2 = Tlv8.Read(decryptedM2);
            if (!innerM2.TryGetValue((byte)Tlv8Tag.Identifier, out var tvIdentifier) ||
                !innerM2.TryGetValue((byte)Tlv8Tag.Signature,  out var tvSignature))
                throw new HapPairingException("M2 inner TLV missing identifier or signature");

            if (!ByteEq(tvIdentifier, credentials.AtvIdentifier))
                throw new HapPairingException("M2 identifier does not match stored credentials");

            // The TV signs serverPub || tvIdentifier || clientPub with its long-term Ed25519 key.
            var tvSignedInput = Concat(serverPub, tvIdentifier, clientPub);
            var tvLtpk        = Ed25519PublicKey.FromBytes(credentials.AtvPublicKey);
            if (!tvLtpk.Verify(tvSignedInput, tvSignature))
                throw new HapPairingException("M2 TV signature did not verify");

            // ── M3 → ──
            // We sign clientPub || clientIdentifier || serverPub with our long-term
            // Ed25519 key (the seed we stored from pair-setup).
            var clientLtsk        = Ed25519PrivateKey.FromSeed(credentials.ClientPrivateKey);
            var clientSignedInput = Concat(clientPub, credentials.ClientIdentifier, serverPub);
            var clientSignature   = clientLtsk.Sign(clientSignedInput);

            var innerM3 = Tlv8.Write(new Dictionary<byte, byte[]>
            {
                { (byte)Tlv8Tag.Identifier, credentials.ClientIdentifier },
                { (byte)Tlv8Tag.Signature,  clientSignature },
            });
            var encryptedM3 = chacha.Encrypt(innerM3, Encoding.ASCII.GetBytes("PV-Msg03"));
            await _conn.SendAsync(CompanionFrameType.PV_Next, PairVerifyMessages.BuildM3(encryptedM3), cancel)
                .ConfigureAwait(false);

            // ── derive long-lived session keys + install ──
            var outKey = Crypto.HkdfSha512(shared, SessionSalt, OutInfoLabel);
            var inKey  = Crypto.HkdfSha512(shared, SessionSalt, InInfoLabel);
            _conn.EnableEncryption(outKey, inKey);
            return new CompanionSessionKeys(outKey, inKey);
        }
        finally
        {
            _conn.FrameReceived -= handler;
            pendingWait.Dispose();
        }
    }

    private static bool ByteEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0; foreach (var p in parts) total += p.Length;
        var output = new byte[total];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, output, o, p.Length); o += p.Length; }
        return output;
    }
}
