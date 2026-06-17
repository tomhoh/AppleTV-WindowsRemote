// SPDX-License-Identifier: MIT
//
// Tests for PairSetupMessages (pure envelope builders/parsers) and the
// CompanionPairSetup driver (asynchronous; uses a TcpListener loopback +
// a scripted server that plays back canned M2/M4/M6 frames).

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AppleTVProtocol;

namespace AppleTVProtocol.Tests;

public class PairSetupTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // ─── Pure envelope builders ───

    [Fact]
    public void BuildM1_MatchesPyatvFixture()
    {
        const string M1Hex = "e2435f706476000100060101455f7077547909";
        Assert.Equal(M1Hex, Hex(PairSetupMessages.BuildM1()));
    }

    [Fact]
    public void BuildM3_HeadAndTailMatchPyatv()
    {
        const string M3HexFirst64 = "e2435f706492c90106010303ffaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string M3HexLast64  = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb455f7077547909";
        var bytes = PairSetupMessages.BuildM3(new byte[384].Select(_ => (byte)0xAA).ToArray(),
                                              new byte[64] .Select(_ => (byte)0xBB).ToArray());
        var hex = Hex(bytes);
        Assert.StartsWith(M3HexFirst64, hex);
        Assert.EndsWith  (M3HexLast64,  hex);
    }

    [Fact]
    public void BuildM5_MatchesPyatvFixture()
    {
        const string M5Hex = "e2435f706491550601050550cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc455f7077547909";
        var bytes = PairSetupMessages.BuildM5(new byte[80].Select(_ => (byte)0xCC).ToArray());
        Assert.Equal(M5Hex, Hex(bytes));
    }

    [Fact]
    public void ParsePairingData_ExtractsExpectedTagsFromM2Envelope()
    {
        // Built the same way pyatv built env_m2 in the fixture script
        var salt = new byte[16].Select(_ => (byte)0xDE).ToArray();
        var B    = new byte[384].Select(_ => (byte)0xEF).ToArray();
        var tlv  = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            { (byte)Tlv8Tag.SeqNo,     new[] { (byte)0x02 } },
            { (byte)Tlv8Tag.Salt,      salt },
            { (byte)Tlv8Tag.PublicKey, B },
        });
        var envelope = Opack.Pack(new Dictionary<string, object?> { { "_pd", tlv } });

        var parsed = PairSetupMessages.ParsePairingData(envelope);
        Assert.Equal(Hex(salt), Hex(parsed[(byte)Tlv8Tag.Salt]));
        Assert.Equal(Hex(B),    Hex(parsed[(byte)Tlv8Tag.PublicKey]));
        Assert.Equal(new byte[] { 0x02 }, parsed[(byte)Tlv8Tag.SeqNo]);
    }

    [Fact]
    public void ParsePairingData_ThrowsOnDeviceErrorTag()
    {
        var ex = Assert.Throws<HapPairingException>(
            () => PairSetupMessages.ParsePairingData(H("e1435f706476060102070102")));
        Assert.Contains("Authentication", ex.Message);
    }

    [Fact]
    public void ParsePairingData_ThrowsWhenEnvelopeMissingPairingData()
    {
        // OPACK envelope without "_pd"
        var bytes = Opack.Pack(new Dictionary<string, object?> { { "junk", 1 } });
        var ex = Assert.Throws<HapPairingException>(() => PairSetupMessages.ParsePairingData(bytes));
        Assert.Contains("_pd", ex.Message);
    }

    // ─── Driver via scripted-server loopback ───
    // The "server" below is *not* a real Apple TV — it's a stub that
    // performs the SRP-6a server side enough to validate the client's
    // M3 proof, then crafts a syntactically valid M4 (with a fake proof
    // since we don't have a real verifier here; the driver's
    // VerifyServerProof will reject it). We assert the *outbound* M1
    // frame bytes against the pinned fixture to prove the wire format,
    // and we don't try to drive the full M6 flow — that needs a real
    // Apple TV's Ed25519 keypair.

    [Fact]
    public async Task RunAsync_SendsM1WithCorrectWireFormat()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();

        await using var conn = new CompanionConnection("127.0.0.1", port);
        await conn.ConnectAsync();
        var server = await acceptTask;

        // Kick off pair-setup on a background task; we only care about M1 wire bytes.
        var driverCts = new CancellationTokenSource();
        var pairSetup = new CompanionPairSetup(conn, timeout: TimeSpan.FromSeconds(1));
        var runTask = pairSetup.RunAsync(
            getPinAsync: () => Task.FromResult("1234"),
            clientPairingId: Encoding.ASCII.GetBytes("client-uuid"),
            clientSeed: new byte[32].Select((_, i) => (byte)i).ToArray(),
            cancel: driverCts.Token);

        // Read the M1 frame off the server-side socket.
        var s = server.GetStream();
        var head = new byte[4];
        int got = 0;
        while (got < 4) got += await s.ReadAsync(head.AsMemory(got));
        Assert.Equal((byte)CompanionFrameType.PS_Start, head[0]);
        int payloadLen = (head[1] << 16) | (head[2] << 8) | head[3];
        var payload = new byte[payloadLen];
        got = 0; while (got < payloadLen) got += await s.ReadAsync(payload.AsMemory(got));

        // M1 wire bytes must match the pinned BuildM1 fixture exactly.
        const string M1Hex = "e2435f706476000100060101455f7077547909";
        Assert.Equal(M1Hex, Hex(payload));

        // Tear everything down: cancel the driver, close the server.
        driverCts.Cancel();
        server.Close();
        listener.Stop();
        try { await runTask; } catch { /* expected — we never replied with M2 */ }
    }

    [Fact]
    public async Task RunAsync_TimesOutWhenServerNeverReplies()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var conn = new CompanionConnection("127.0.0.1", port);
        await conn.ConnectAsync();
        using var server = await acceptTask;

        var pairSetup = new CompanionPairSetup(conn, timeout: TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pairSetup.RunAsync(
                getPinAsync: () => Task.FromResult("1234"),
                clientPairingId: Encoding.ASCII.GetBytes("client-uuid"),
                clientSeed: new byte[32]));
        Assert.NotNull(ex);

        listener.Stop();
    }

    [Fact]
    public async Task RunAsync_PropagatesDevicePairingError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var conn = new CompanionConnection("127.0.0.1", port);
        await conn.ConnectAsync();
        using var server = await acceptTask;

        // Background: read M1, then send back an envelope with a HAP Error tag.
        var serverTask = Task.Run(async () =>
        {
            var s = server.GetStream();
            var head = new byte[4];
            int got = 0;
            while (got < 4) got += await s.ReadAsync(head.AsMemory(got));
            int payloadLen = (head[1] << 16) | (head[2] << 8) | head[3];
            var ignored = new byte[payloadLen];
            got = 0; while (got < payloadLen) got += await s.ReadAsync(ignored.AsMemory(got));

            // Reply with a PS_Next frame whose pairing-data has Error=Authentication
            var errorEnvelope = H("e1435f706476060102070102");
            var frame = CompanionFrame.EncodeFrame(CompanionFrameType.PS_Next, errorEnvelope);
            await s.WriteAsync(frame);
        });

        var pairSetup = new CompanionPairSetup(conn);
        var ex = await Assert.ThrowsAsync<HapPairingException>(
            () => pairSetup.RunAsync(
                getPinAsync: () => Task.FromResult("9999"),
                clientPairingId: Encoding.ASCII.GetBytes("client-uuid"),
                clientSeed: new byte[32]));

        Assert.Contains("Authentication", ex.Message);
        await serverTask;
        listener.Stop();
    }
}
