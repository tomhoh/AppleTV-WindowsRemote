// SPDX-License-Identifier: MIT
//
// Tests for CompanionSession — the post-pair-verify request/response /
// HID layer. We bring up an encrypted CompanionConnection on a
// TcpListener loopback, fronted by a tiny fake Apple TV that decrypts
// requests and replies with matching XID responses.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AppleTVProtocol;

namespace AppleTVProtocol.Tests;

public class CompanionSessionTests
{
    /// <summary>
    /// Spin up a TcpListener, accept one connection, install matching
    /// session keys on both sides, and run a per-request callback that
    /// returns the response dict to send back.
    /// </summary>
    private sealed class FakeEncryptedAppleTV : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        public int Port { get; }
        public Task? ServerLoop { get; private set; }

        public byte[] ServerOutKey { get; }
        public byte[] ServerInKey { get; }

        public FakeEncryptedAppleTV()
        {
            ServerOutKey = RandomNumberGenerator.GetBytes(32);
            ServerInKey  = RandomNumberGenerator.GetBytes(32);
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start(Func<IDictionary<string, object?>, IDictionary<string, object?>?> handler)
        {
            ServerLoop = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var cipher = new CompanionCipher(ServerOutKey, ServerInKey);

                while (true)
                {
                    var head = new byte[4];
                    int got = 0;
                    while (got < 4)
                    {
                        int n = await stream.ReadAsync(head.AsMemory(got));
                        if (n <= 0) return;
                        got += n;
                    }
                    int len = (head[1] << 16) | (head[2] << 8) | head[3];
                    var ct = new byte[len];
                    got = 0;
                    while (got < len) got += await stream.ReadAsync(ct.AsMemory(got));

                    var pt = cipher.Decrypt(ct, head);
                    var (req, _) = Opack.Unpack(pt);
                    var reqDict = CompanionSession.ConvertToStringKeyed(req);
                    if (reqDict is null) continue;

                    var resp = handler(reqDict);
                    if (resp is null) continue;

                    var respBytes = Opack.Pack(resp);
                    var respHeader = CompanionFrame.EncodeHeader(CompanionFrameType.E_OPACK,
                        respBytes.Length + CompanionFrame.AuthTagLength);
                    var respCt = cipher.Encrypt(respBytes, respHeader);
                    await stream.WriteAsync(respHeader);
                    await stream.WriteAsync(respCt);
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            if (ServerLoop is { } t) { try { await t; } catch { } }
        }
    }

    private static IDictionary<string, object?> AckResponse(IDictionary<string, object?> req)
    {
        return new Dictionary<string, object?>
        {
            { "_i", req.TryGetValue("_i", out var i) ? i : null },
            { "_t", (int)CompanionMessageType.Response },
            { "_x", req["_x"] },
            { "_c", new Dictionary<string, object?>() },
        };
    }

    [Fact]
    public async Task SendRequestAsync_RoundTripsThroughEncryptedTransport()
    {
        await using var fake = new FakeEncryptedAppleTV();
        fake.Start(AckResponse);

        await using var conn = new CompanionConnection("127.0.0.1", fake.Port);
        await conn.ConnectAsync();
        // Install the same keys, swapped: client-out == server-in, client-in == server-out
        conn.EnableEncryption(fake.ServerInKey, fake.ServerOutKey);

        await using var session = new CompanionSession(conn);
        var resp = await session.SendRequestAsync("_hidC",
            new Dictionary<string, object?> { { "_hBtS", 1 }, { "_hidC", (int)HidCommand.Up } },
            timeout: TimeSpan.FromSeconds(2));

        Assert.Equal("_hidC", resp["_i"]);
        Assert.Equal((int)CompanionMessageType.Response, CompanionSession.UnboxInt(resp["_t"]));
    }

    [Fact]
    public async Task SendHidAsync_SendsCorrectlyShapedRequest()
    {
        var captured = new TaskCompletionSource<IDictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fake = new FakeEncryptedAppleTV();
        fake.Start(req => { captured.TrySetResult(req); return AckResponse(req); });

        await using var conn = new CompanionConnection("127.0.0.1", fake.Port);
        await conn.ConnectAsync();
        conn.EnableEncryption(fake.ServerInKey, fake.ServerOutKey);
        await using var session = new CompanionSession(conn);

        await session.SendHidAsync(HidCommand.PlayPause, down: true);

        var req = await captured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("_hidC", req["_i"]);
        Assert.Equal((int)CompanionMessageType.Request, CompanionSession.UnboxInt(req["_t"]));
        var content = CompanionSession.ConvertToStringKeyed(req["_c"]);
        Assert.NotNull(content);
        Assert.Equal(1, CompanionSession.UnboxInt(content!["_hBtS"]));            // 1 = down
        Assert.Equal((int)HidCommand.PlayPause, CompanionSession.UnboxInt(content["_hidC"]));
    }

    [Fact]
    public async Task PressAsync_SendsDownThenUp()
    {
        var requests = new List<int>();   // capture _hBtS in order
        var allReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fake = new FakeEncryptedAppleTV();
        fake.Start(req =>
        {
            var content = CompanionSession.ConvertToStringKeyed(req["_c"]);
            int btnState;
            lock (requests)
            {
                btnState = CompanionSession.UnboxInt(content!["_hBtS"]) ?? -1;
                requests.Add(btnState);
                if (requests.Count == 2) allReceived.TrySetResult(true);
            }
            return AckResponse(req);
        });

        await using var conn = new CompanionConnection("127.0.0.1", fake.Port);
        await conn.ConnectAsync();
        conn.EnableEncryption(fake.ServerInKey, fake.ServerOutKey);
        await using var session = new CompanionSession(conn);

        await session.PressAsync(HidCommand.Select);
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { 1, 2 }, requests);   // down then up
    }

    [Fact]
    public async Task SendRequestAsync_TimesOutWhenServerSilent()
    {
        await using var fake = new FakeEncryptedAppleTV();
        fake.Start(_ => null);   // server reads but never responds

        await using var conn = new CompanionConnection("127.0.0.1", fake.Port);
        await conn.ConnectAsync();
        conn.EnableEncryption(fake.ServerInKey, fake.ServerOutKey);
        await using var session = new CompanionSession(conn) { DefaultTimeout = TimeSpan.FromMilliseconds(100) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.SendRequestAsync("_ping", new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task SendEventAsync_DoesNotWaitForResponse()
    {
        await using var fake = new FakeEncryptedAppleTV();
        // Server reads silently — events aren't expected to be acked.
        fake.Start(_ => null);

        await using var conn = new CompanionConnection("127.0.0.1", fake.Port);
        await conn.ConnectAsync();
        conn.EnableEncryption(fake.ServerInKey, fake.ServerOutKey);
        await using var session = new CompanionSession(conn);

        // Should return quickly even though the server never responds.
        await session.SendEventAsync("_interest", new Dictionary<string, object?> { { "_regEvents", new[] { "evt" } } })
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Constructor_RejectsUnencryptedConnection()
    {
        var unencrypted = new CompanionConnection("127.0.0.1", 49152);
        Assert.Throws<InvalidOperationException>(() => new CompanionSession(unencrypted));
    }
}
