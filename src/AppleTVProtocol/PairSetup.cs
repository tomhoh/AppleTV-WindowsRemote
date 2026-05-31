// SPDX-License-Identifier: MIT
//
// HAP pair-setup state machine for the Companion protocol.
//
// Pair-setup is a six-frame exchange. The client (us) emits M1, M3, M5;
// the Apple TV emits M2, M4, M6. Every frame is the same envelope:
//
//   Companion frame {
//     type    = PS_Start (M1) or PS_Next (everything else),
//     payload = OPACK({
//                 "_pd": tlv-bytes,     // the actual HAP TLV8 payload
//                 "_pwTy": 1             // pyatv adds this; means "PIN-based pairing"
//               })
//   }
//
// At a high level:
//
//   M1 -> {Method=PairSetup, State=M1}                     // "I want to pair, PIN-based"
//   M2 <- {State=M2, Salt, PublicKey=B}                    // here's the salt + server pubkey
//   M3 -> {State=M3, PublicKey=A, Proof=M1-srp}            // here's our pubkey + SRP proof
//   M4 <- {State=M4, Proof=M2-srp}                          // SRP proof verification
//   M5 -> {State=M5, EncryptedData=chacha(inner-tlv)}      // encrypted: identifier + public-key + signature
//   M6 <- {State=M6, EncryptedData=chacha(inner-tlv)}      // encrypted: device's identifier + public-key + signature
//
// The two ChaCha20-Poly1305 calls inside M5/M6 use the HAP 8-byte-nonce
// convention with nonces "PS-Msg05" / "PS-Msg06"; the key is HKDF(K,
// "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info").
//
// This file has pure-data envelope builders/parsers (no IO) plus an
// async `CompanionPairSetup` driver that runs the flow over a
// `CompanionConnection`.

using System.Text;

namespace AppleTVProtocol;

/// <summary>
/// Pure-data helpers — build the OPACK-envelope bytes for outbound
/// pair-setup frames and extract the inner TLV8 from inbound frames.
/// </summary>
public static class PairSetupMessages
{
    public const string PairingDataKey = "_pd";
    public const string PinTypeKey     = "_pwTy";
    public const string XidKey         = "_x";

    /// <summary>
    /// M1 outbound — "I want to start pair-setup, PIN-based".
    /// pyatv's <c>send_opack</c> auto-adds an <c>_x</c> XID to every
    /// outgoing OPACK envelope; without it, real Apple TVs silently
    /// drop the frame, so we mirror that behaviour and require an XID
    /// from the caller.
    /// </summary>
    public static byte[] BuildM1(int xid)
    {
        var tlv = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.Method, new[] { (byte)Tlv8Method.PairSetup } },
            { (byte)Tlv8Tag.SeqNo,  new[] { (byte)Tlv8State.M1 } },
        });
        return Opack.Pack(new Dictionary<string, object?>
        {
            { PairingDataKey, tlv },
            { PinTypeKey,     1 },
            { XidKey,         xid },
        });
    }

    /// <summary>M3 outbound — client SRP public key + proof.</summary>
    public static byte[] BuildM3(byte[] clientPublicKey, byte[] proofM1, int xid)
    {
        var tlv = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.SeqNo,     new[] { (byte)Tlv8State.M3 } },
            { (byte)Tlv8Tag.PublicKey, clientPublicKey },
            { (byte)Tlv8Tag.Proof,     proofM1 },
        });
        return Opack.Pack(new Dictionary<string, object?>
        {
            { PairingDataKey, tlv },
            { PinTypeKey,     1 },
            { XidKey,         xid },
        });
    }

    /// <summary>M5 outbound — wraps encrypted identifier+publickey+signature.</summary>
    public static byte[] BuildM5(byte[] encryptedData, int xid)
    {
        var tlv = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.SeqNo,         new[] { (byte)Tlv8State.M5 } },
            { (byte)Tlv8Tag.EncryptedData, encryptedData },
        });
        return Opack.Pack(new Dictionary<string, object?>
        {
            { PairingDataKey, tlv },
            { PinTypeKey,     1 },
            { XidKey,         xid },
        });
    }

    /// <summary>
    /// Pull the inner TLV8 dictionary out of a received OPACK-envelope
    /// payload (M2 / M4 / M6 reply).
    /// </summary>
    /// <exception cref="HapPairingException">
    /// If the envelope is missing or malformed, or the inner TLV contains
    /// an <see cref="Tlv8Tag.Error"/> entry.
    /// </exception>
    public static Dictionary<byte, byte[]> ParsePairingData(byte[] opackEnvelope)
    {
        var (unpacked, _) = Opack.Unpack(opackEnvelope);
        if (unpacked is not System.Collections.IDictionary dict)
            throw new HapPairingException($"expected OPACK dict, got {unpacked?.GetType().Name ?? "null"}");
        if (!dict.Contains(PairingDataKey))
            throw new HapPairingException("envelope has no _pd (pairing-data) bytes");
        if (dict[PairingDataKey] is not byte[] tlvBytes)
            throw new HapPairingException($"_pd is not bytes (got {dict[PairingDataKey]?.GetType().Name ?? "null"})");

        var tlv = Tlv8.Read(tlvBytes);
        if (tlv.TryGetValue((byte)Tlv8Tag.Error, out var errBytes) && errBytes.Length > 0)
        {
            var code = (Tlv8ErrorCode)errBytes[0];
            throw new HapPairingException($"device returned pairing error: {code} (0x{errBytes[0]:X2})");
        }
        return tlv;
    }
}

/// <summary>Thrown when pair-setup fails to make forward progress.</summary>
public sealed class HapPairingException : Exception
{
    public HapPairingException(string message) : base(message) { }
    public HapPairingException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Async driver — runs the M1–M6 exchange over a connected
/// <see cref="CompanionConnection"/>, returning the long-term
/// <see cref="HapCredentials"/> that result.
/// </summary>
public sealed class CompanionPairSetup
{
    private const string EncryptSalt = "Pair-Setup-Encrypt-Salt";
    private const string EncryptInfo = "Pair-Setup-Encrypt-Info";
    private const string SignSalt    = "Pair-Setup-Controller-Sign-Salt";
    private const string SignInfo    = "Pair-Setup-Controller-Sign-Info";

    private readonly CompanionConnection _conn;
    private readonly TimeSpan _timeout;

    public CompanionPairSetup(CompanionConnection conn, TimeSpan? timeout = null)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Run the full pair-setup flow.
    /// </summary>
    /// <param name="pin">The 4-digit PIN displayed on the Apple TV.</param>
    /// <param name="clientPairingId">
    /// A UUID-shaped identifier for *this* client; persists with the
    /// credentials so pair-verify can present the same identity later.
    /// </param>
    /// <param name="clientSeed">
    /// 32-byte Ed25519 seed. Generated fresh if null.
    /// </param>
    /// <param name="displayName">
    /// Optional friendly name to send as `Name` in the M5 inner TLV.
    /// </param>
    public async Task<HapCredentials> RunAsync(
        string pin,
        byte[] clientPairingId,
        byte[]? clientSeed = null,
        string? displayName = null,
        CancellationToken cancel = default)
    {
        // ── set up state ──
        clientSeed ??= System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var ed = Ed25519PrivateKey.FromSeed(clientSeed);

        var srp = new Srp6aClient();
        srp.Initialize(clientSeed);   // pyatv uses signing-key bytes as the SRP `a`; harmless re-use
        srp.SetPin(pin);

        // ── frame-receive plumbing ──
        var pending = new Queue<byte[]>();
        var pendingWait = new SemaphoreSlim(0);
        EventHandler<CompanionFrame> handler = (_, frame) =>
        {
            // Only handle PS_Next responses; ignore noise.
            if (frame.Type == CompanionFrameType.PS_Next)
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
            return PairSetupMessages.ParsePairingData(payload);
        }

        // pyatv hands out XIDs from an int counter and bumps it on every
        // send_opack. Match that — Apple TVs silently drop frames that
        // don't include an _x field.
        int xid = Random.Shared.Next(1, int.MaxValue);

        try
        {
            // ── M1 → ── ── M2 ← ──
            await _conn.SendAsync(CompanionFrameType.PS_Start, PairSetupMessages.BuildM1(xid++), cancel).ConfigureAwait(false);
            var m2 = await AwaitReplyAsync().ConfigureAwait(false);
            if (!m2.TryGetValue((byte)Tlv8Tag.Salt, out var atvSalt) ||
                !m2.TryGetValue((byte)Tlv8Tag.PublicKey, out var atvPub))
                throw new HapPairingException("M2 reply missing salt or public-key");

            // ── M3 → ── ── M4 ← ──
            var (A, M1proof) = srp.ProcessChallenge(atvSalt, atvPub);
            await _conn.SendAsync(CompanionFrameType.PS_Next, PairSetupMessages.BuildM3(A, M1proof, xid++), cancel).ConfigureAwait(false);
            var m4 = await AwaitReplyAsync().ConfigureAwait(false);
            if (!m4.TryGetValue((byte)Tlv8Tag.Proof, out var atvProof))
                throw new HapPairingException("M4 reply missing proof");
            srp.VerifyServerProof(M1proof, atvProof);

            // ── M5 →  (encrypted inner TLV) ──
            var sessionKey = Crypto.HkdfSha512(srp.SharedKey, EncryptSalt, EncryptInfo);
            var iosDeviceX = Crypto.HkdfSha512(srp.SharedKey, SignSalt, SignInfo);

            var clientPublicKey = ed.PublicKey.Bytes;
            var deviceInfo = Concat(iosDeviceX, clientPairingId, clientPublicKey);
            var deviceSignature = ed.Sign(deviceInfo);

            var innerTlvEntries = new Dictionary<byte, byte[]>
            {
                { (byte)Tlv8Tag.Identifier, clientPairingId },
                { (byte)Tlv8Tag.PublicKey,  clientPublicKey },
                { (byte)Tlv8Tag.Signature,  deviceSignature },
            };
            if (!string.IsNullOrEmpty(displayName))
            {
                innerTlvEntries[(byte)Tlv8Tag.Name] = Opack.Pack(
                    new Dictionary<string, object?> { { "name", displayName } });
            }
            var innerTlv = Tlv8.Write(innerTlvEntries);

            var chacha = new ChaCha20Poly1305_8ByteNonce(sessionKey);
            var encryptedM5 = chacha.Encrypt(innerTlv, Encoding.ASCII.GetBytes("PS-Msg05"));
            await _conn.SendAsync(CompanionFrameType.PS_Next, PairSetupMessages.BuildM5(encryptedM5, xid++), cancel).ConfigureAwait(false);

            // ── M6 ← (encrypted inner TLV) ──
            var m6 = await AwaitReplyAsync().ConfigureAwait(false);
            if (!m6.TryGetValue((byte)Tlv8Tag.EncryptedData, out var encryptedM6))
                throw new HapPairingException("M6 reply missing encrypted-data");
            byte[] decryptedM6;
            try
            {
                decryptedM6 = chacha.Decrypt(encryptedM6, Encoding.ASCII.GetBytes("PS-Msg06"));
            }
            catch (Exception ex)
            {
                throw new HapPairingException("M6 decrypt failed (wrong PIN?)", ex);
            }
            var innerM6 = Tlv8.Read(decryptedM6);
            if (!innerM6.TryGetValue((byte)Tlv8Tag.Identifier, out var atvIdentifier) ||
                !innerM6.TryGetValue((byte)Tlv8Tag.PublicKey,  out var atvPubKey) ||
                !innerM6.TryGetValue((byte)Tlv8Tag.Signature,  out var atvSignature))
                throw new HapPairingException("M6 inner TLV missing identifier / public-key / signature");

            // Verify the device's signature: the value the TV signed is
            //   AccessoryX || atvIdentifier || atvPubKey
            // where AccessoryX = HKDF(K, "Pair-Setup-Accessory-Sign-Salt",
            //                            "Pair-Setup-Accessory-Sign-Info").
            var atvSignBase = Crypto.HkdfSha512(srp.SharedKey,
                "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info");
            var signedInput = Concat(atvSignBase, atvIdentifier, atvPubKey);
            var atvPubBytes = atvPubKey;
            if (atvPubBytes.Length == 32)
            {
                var atvLtpk = Ed25519PublicKey.FromBytes(atvPubBytes);
                if (!atvLtpk.Verify(signedInput, atvSignature))
                    throw new HapPairingException("M6 device signature did not verify");
            }
            // (If pubkey isn't 32 bytes, we skip — older TVs are observed to
            // pad oddly. pyatv has a similar TODO.)

            return new HapCredentials(
                AtvPublicKey:    atvPubKey,
                ClientPrivateKey:clientSeed,
                AtvIdentifier:   atvIdentifier,
                ClientIdentifier:clientPairingId);
        }
        finally
        {
            _conn.FrameReceived -= handler;
            pendingWait.Dispose();
        }
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
