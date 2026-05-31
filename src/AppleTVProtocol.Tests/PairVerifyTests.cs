// SPDX-License-Identifier: MIT
//
// End-to-end pair-verify test: simulate the Apple TV server side over a
// TcpListener loopback and run our CompanionPairVerify driver against
// it. On success, both sides install matching session keys and we
// verify an encrypted round-trip via the underlying CompanionConnection.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AppleTVProtocol;

namespace AppleTVProtocol.Tests;

public class PairVerifyTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    // ─── Pure envelope builders ───

    [Fact]
    public void BuildM1_OpackEnvelopeRoundTripsThroughParser()
    {
        var pub = new byte[32].Select((_, i) => (byte)(0xA0 + i)).ToArray();
        var envelope = PairVerifyMessages.BuildM1(pub);
        var parsed = PairVerifyMessages.ParsePairingData(envelope);
        Assert.Equal(pub, parsed[(byte)Tlv8Tag.PublicKey]);
        Assert.Equal(new byte[] { (byte)Tlv8State.M1 }, parsed[(byte)Tlv8Tag.SeqNo]);
    }

    [Fact]
    public void BuildM3_OpackEnvelopeRoundTripsThroughParser()
    {
        var enc = new byte[64].Select((_, i) => (byte)(0xC0 + i)).ToArray();
        var envelope = PairVerifyMessages.BuildM3(enc);
        var parsed = PairVerifyMessages.ParsePairingData(envelope);
        Assert.Equal(enc, parsed[(byte)Tlv8Tag.EncryptedData]);
        Assert.Equal(new byte[] { (byte)Tlv8State.M3 }, parsed[(byte)Tlv8Tag.SeqNo]);
    }

    // ─── End-to-end driver test ───

    /// <summary>
    /// Stand-in for an Apple TV's pair-verify server side. Reads M1,
    /// constructs and sends M2, reads M3 and validates the client
    /// signature. Mirrors what an actual TV does (and what pyatv's
    /// `pyatv.protocols.companion.server_auth` does).
    /// </summary>
    private sealed class FakeAppleTVPairVerifyServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Ed25519PrivateKey _tvLtsk;
        private readonly Ed25519PublicKey _clientLtpk;
        private readonly byte[] _tvIdentifier;
        private readonly byte[] _clientIdentifier;
        private readonly byte[] _serverVerifySeed;

        public int Port { get; }
        public byte[]? DerivedShared { get; private set; }
        public Task ServerLoop { get; private set; } = Task.CompletedTask;

        public FakeAppleTVPairVerifyServer(
            Ed25519PrivateKey tvLtsk,
            Ed25519PublicKey clientLtpk,
            byte[] tvIdentifier,
            byte[] clientIdentifier,
            byte[] serverVerifySeed)
        {
            _tvLtsk = tvLtsk;
            _clientLtpk = clientLtpk;
            _tvIdentifier = tvIdentifier;
            _clientIdentifier = clientIdentifier;
            _serverVerifySeed = serverVerifySeed;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start()
        {
            ServerLoop = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();

                // ── Read M1 frame ──
                var m1Frame = await ReadFrameAsync(stream);
                if (m1Frame.Type != CompanionFrameType.PV_Start)
                    throw new InvalidOperationException($"expected PV_Start, got {m1Frame.Type}");
                var m1Tlv = PairVerifyMessages.ParsePairingData(m1Frame.Payload);
                var clientVerifyPub = m1Tlv[(byte)Tlv8Tag.PublicKey];

                // ── Server side of ECDH + session key ──
                var serverVerify = X25519PrivateKey.FromBytes(_serverVerifySeed);
                var serverVerifyPub = serverVerify.PublicKey.Bytes;
                var shared = serverVerify.Agree(X25519PublicKey.FromBytes(clientVerifyPub));
                DerivedShared = shared;
                var sessionKey = Crypto.HkdfSha512(shared,
                    "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
                var chacha = new ChaCha20Poly1305_8ByteNonce(sessionKey);

                // TV signs serverPub || tvIdentifier || clientPub
                var tvSigInput = Concat(serverVerifyPub, _tvIdentifier, clientVerifyPub);
                var tvSig = _tvLtsk.Sign(tvSigInput);
                var innerM2 = Tlv8.Write(new Dictionary<byte, byte[]>
                {
                    { (byte)Tlv8Tag.Identifier, _tvIdentifier },
                    { (byte)Tlv8Tag.Signature,  tvSig },
                });
                var encryptedM2 = chacha.Encrypt(innerM2, Encoding.ASCII.GetBytes("PV-Msg02"));
                var outerM2 = Tlv8.Write(new Dictionary<byte, byte[]>
                {
                    { (byte)Tlv8Tag.SeqNo,         new[] { (byte)Tlv8State.M2 } },
                    { (byte)Tlv8Tag.PublicKey,     serverVerifyPub },
                    { (byte)Tlv8Tag.EncryptedData, encryptedM2 },
                });
                var envelopeM2 = Opack.Pack(new Dictionary<string, object?>
                {
                    { PairVerifyMessages.PairingDataKey, outerM2 },
                });
                await stream.WriteAsync(CompanionFrame.EncodeFrame(CompanionFrameType.PV_Next, envelopeM2));

                // ── Read M3 frame, decrypt and verify client signature ──
                var m3Frame = await ReadFrameAsync(stream);
                var m3Tlv   = PairVerifyMessages.ParsePairingData(m3Frame.Payload);
                var m3Enc   = m3Tlv[(byte)Tlv8Tag.EncryptedData];
                var m3Dec   = chacha.Decrypt(m3Enc, Encoding.ASCII.GetBytes("PV-Msg03"));
                var innerM3 = Tlv8.Read(m3Dec);
                var clientId  = innerM3[(byte)Tlv8Tag.Identifier];
                var clientSig = innerM3[(byte)Tlv8Tag.Signature];
                if (!clientId.SequenceEqual(_clientIdentifier))
                    throw new InvalidOperationException("client identifier mismatch");
                var clientSigInput = Concat(clientVerifyPub, _clientIdentifier, serverVerifyPub);
                if (!_clientLtpk.Verify(clientSigInput, clientSig))
                    throw new InvalidOperationException("client signature did not verify");
            });
        }

        public void Dispose() => _listener.Stop();

        private static async Task<CompanionFrame> ReadFrameAsync(NetworkStream s)
        {
            var head = new byte[4];
            int got = 0;
            while (got < 4) got += await s.ReadAsync(head.AsMemory(got));
            int len = (head[1] << 16) | (head[2] << 8) | head[3];
            var payload = new byte[len];
            got = 0;
            while (got < len) got += await s.ReadAsync(payload.AsMemory(got));
            return new CompanionFrame((CompanionFrameType)head[0], payload);
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

    [Fact]
    public async Task RunAsync_CompletesFullVerifyAndInstallsSessionKeys()
    {
        // Pinned long-term keys (so the test is deterministic).
        var tvLtsk = Ed25519PrivateKey.FromSeed(new byte[32].Select((_, i) => (byte)(0x10 + i)).ToArray());
        var tvLtpk = tvLtsk.PublicKey;
        var clientLtsk = Ed25519PrivateKey.FromSeed(new byte[32].Select((_, i) => (byte)(0x40 + i)).ToArray());

        var tvIdentifier     = Encoding.ASCII.GetBytes("AA:BB:CC:DD:EE:FF");
        var clientIdentifier = Encoding.ASCII.GetBytes("11111111-2222-3333-4444-555555555555");

        var serverEphemeral = new byte[32].Select((_, i) => (byte)(0x70 + i)).ToArray();
        var clientEphemeral = new byte[32].Select((_, i) => (byte)(0xA0 + i)).ToArray();

        using var server = new FakeAppleTVPairVerifyServer(
            tvLtsk:           tvLtsk,
            clientLtpk:       clientLtsk.PublicKey,
            tvIdentifier:     tvIdentifier,
            clientIdentifier: clientIdentifier,
            serverVerifySeed: serverEphemeral);
        server.Start();

        await using var conn = new CompanionConnection("127.0.0.1", server.Port);
        await conn.ConnectAsync();

        var credentials = new HapCredentials(
            AtvPublicKey:    tvLtpk.Bytes,
            ClientPrivateKey:clientLtsk.Seed,
            AtvIdentifier:   tvIdentifier,
            ClientIdentifier:clientIdentifier);

        var driver = new CompanionPairVerify(conn, timeout: TimeSpan.FromSeconds(5));
        var sessionKeys = await driver.RunAsync(credentials, ephemeralX25519Seed: clientEphemeral);

        // Wait for the server side to finish processing M3
        await server.ServerLoop.WaitAsync(TimeSpan.FromSeconds(2));

        // Both sides arrived at the same shared secret and therefore
        // identical session keys.
        var expectedShared = X25519PrivateKey.FromBytes(clientEphemeral)
            .Agree(X25519PrivateKey.FromBytes(serverEphemeral).PublicKey);
        Assert.Equal(Convert.ToHexString(expectedShared), Convert.ToHexString(server.DerivedShared!));

        var expectedOut = Crypto.HkdfSha512(expectedShared,
            CompanionPairVerify.SessionSalt, CompanionPairVerify.OutInfoLabel);
        var expectedIn  = Crypto.HkdfSha512(expectedShared,
            CompanionPairVerify.SessionSalt, CompanionPairVerify.InInfoLabel);
        Assert.Equal(Convert.ToHexString(expectedOut), Convert.ToHexString(sessionKeys.OutKey));
        Assert.Equal(Convert.ToHexString(expectedIn),  Convert.ToHexString(sessionKeys.InKey));

        // Connection should now be in encrypted mode.
        Assert.True(conn.EncryptionEnabled);
    }

    [Fact]
    public async Task RunAsync_FailsWhenStoredAtvPublicKeyDoesntMatchSigner()
    {
        // Construct credentials that point at a DIFFERENT TV public key
        // than the one the fake server signs with — pair-verify should
        // reject the M2 signature.
        var tvLtsk = Ed25519PrivateKey.FromSeed(new byte[32].Select((_, i) => (byte)(0x10 + i)).ToArray());
        var wrongTvLtsk = Ed25519PrivateKey.FromSeed(new byte[32].Select((_, i) => (byte)(0xEE)).ToArray());
        var clientLtsk = Ed25519PrivateKey.FromSeed(new byte[32].Select((_, i) => (byte)(0x40 + i)).ToArray());

        var tvIdentifier     = Encoding.ASCII.GetBytes("AA:BB:CC:DD:EE:FF");
        var clientIdentifier = Encoding.ASCII.GetBytes("client-uuid");

        using var server = new FakeAppleTVPairVerifyServer(
            tvLtsk:           tvLtsk,
            clientLtpk:       clientLtsk.PublicKey,
            tvIdentifier:     tvIdentifier,
            clientIdentifier: clientIdentifier,
            serverVerifySeed: new byte[32].Select((_, i) => (byte)(0x70 + i)).ToArray());
        server.Start();

        await using var conn = new CompanionConnection("127.0.0.1", server.Port);
        await conn.ConnectAsync();

        var credentials = new HapCredentials(
            AtvPublicKey:    wrongTvLtsk.PublicKey.Bytes,   // wrong!
            ClientPrivateKey:clientLtsk.Seed,
            AtvIdentifier:   tvIdentifier,
            ClientIdentifier:clientIdentifier);

        var driver = new CompanionPairVerify(conn, timeout: TimeSpan.FromSeconds(2));
        var ex = await Assert.ThrowsAsync<HapPairingException>(
            () => driver.RunAsync(credentials, ephemeralX25519Seed: new byte[32]));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(conn.EncryptionEnabled);
    }
}
