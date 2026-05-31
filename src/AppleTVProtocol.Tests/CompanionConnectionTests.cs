// SPDX-License-Identifier: MIT
//
// Exercise CompanionConnection against a local TcpListener loopback that
// replays / verifies bytes the way an Apple TV would.

using System.Net;
using System.Net.Sockets;
using System.Text;
using AppleTVProtocol;

namespace AppleTVProtocol.Tests;

public class CompanionConnectionTests
{
    // Spin up an ephemeral TcpListener; return the bound port and a handle
    // to await the first accepted socket.
    private static (int port, Task<TcpClient> accepted, TcpListener listener) StartLoopbackServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return (port, listener.AcceptTcpClientAsync(), listener);
    }

    [Fact]
    public async Task Connect_DialsListenerAndReportsConnected()
    {
        var (port, accept, listener) = StartLoopbackServer();
        await using var conn = new CompanionConnection("127.0.0.1", port);
        await conn.ConnectAsync();
        Assert.True(conn.Connected);
        using var server = await accept;
        Assert.True(server.Connected);
        listener.Stop();
    }

    [Fact]
    public async Task SendAsync_WritesPlaintextFrameOverTheWire()
    {
        var (port, accept, listener) = StartLoopbackServer();
        await using var conn = new CompanionConnection("127.0.0.1", port);
        await conn.ConnectAsync();
        using var server = await accept;

        var payload = Encoding.ASCII.GetBytes("PING");
        await conn.SendAsync(CompanionFrameType.U_OPACK, payload);

        var received = new byte[1024];
        int n = await server.GetStream().ReadAsync(received);
        var actual = Convert.ToHexString(received.AsSpan(0, n)).ToLowerInvariant();
        Assert.Equal("070000045049 4e47".Replace(" ", ""), actual);

        listener.Stop();
    }

    [Fact]
    public async Task FrameReceived_FiresWhenServerSendsAFrame()
    {
        var (port, accept, listener) = StartLoopbackServer();
        await using var conn = new CompanionConnection("127.0.0.1", port);
        var got = new TaskCompletionSource<CompanionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.FrameReceived += (_, f) => got.TrySetResult(f);
        await conn.ConnectAsync();
        using var server = await accept;

        var payload = Encoding.ASCII.GetBytes("PONG!");
        var wire    = CompanionFrame.EncodeFrame(CompanionFrameType.U_OPACK, payload);
        await server.GetStream().WriteAsync(wire);

        var frame = await got.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CompanionFrameType.U_OPACK, frame.Type);
        Assert.Equal("PONG!", Encoding.ASCII.GetString(frame.Payload));

        listener.Stop();
    }

    [Fact]
    public async Task FrameReceived_ReassemblesPartialReads()
    {
        var (port, accept, listener) = StartLoopbackServer();
        await using var conn = new CompanionConnection("127.0.0.1", port);
        var got = new TaskCompletionSource<CompanionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.FrameReceived += (_, f) => got.TrySetResult(f);
        await conn.ConnectAsync();
        using var server = await accept;

        var payload = new byte[1500];
        new Random(0).NextBytes(payload);
        var wire = CompanionFrame.EncodeFrame(CompanionFrameType.E_OPACK, payload);

        // Send in three chunks with little gaps to force the read loop to
        // re-enter without a complete frame in the buffer.
        var s = server.GetStream();
        await s.WriteAsync(wire.AsMemory(0, 7));      await Task.Delay(20);
        await s.WriteAsync(wire.AsMemory(7, 600));    await Task.Delay(20);
        await s.WriteAsync(wire.AsMemory(607));

        var frame = await got.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CompanionFrameType.E_OPACK, frame.Type);
        Assert.Equal(payload.Length, frame.Payload.Length);
        Assert.Equal(Convert.ToHexString(payload), Convert.ToHexString(frame.Payload));

        listener.Stop();
    }

    [Fact]
    public async Task FrameReceived_DeliversMultipleFramesInOneRead()
    {
        var (port, accept, listener) = StartLoopbackServer();
        await using var conn = new CompanionConnection("127.0.0.1", port);
        var received = new List<CompanionFrame>();
        var all      = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.FrameReceived += (_, f) =>
        {
            int count;
            lock (received) { received.Add(f); count = received.Count; }
            if (count == 3) all.TrySetResult(true);
        };
        await conn.ConnectAsync();
        using var server = await accept;

        var f1 = CompanionFrame.EncodeFrame(CompanionFrameType.U_OPACK, Encoding.ASCII.GetBytes("AAA"));
        var f2 = CompanionFrame.EncodeFrame(CompanionFrameType.NoOp,    Array.Empty<byte>());
        var f3 = CompanionFrame.EncodeFrame(CompanionFrameType.U_OPACK, Encoding.ASCII.GetBytes("BBBB"));
        await server.GetStream().WriteAsync(f1.Concat(f2).Concat(f3).ToArray());

        await all.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, received.Count);
        Assert.Equal("AAA",  Encoding.ASCII.GetString(received[0].Payload));
        Assert.Empty(received[1].Payload);
        Assert.Equal("BBBB", Encoding.ASCII.GetString(received[2].Payload));

        listener.Stop();
    }

    [Fact]
    public async Task EncryptedFrame_RoundTripsThroughTwoConnections()
    {
        // Set up two sibling connections with swapped keys, then send an
        // encrypted frame from client → server and decode on the server side
        // using a fresh CompanionCipher (the server-side cipher is created
        // by CompanionConnection.EnableEncryption with the opposite roles).
        var (port, accept, listener) = StartLoopbackServer();
        await using var client = new CompanionConnection("127.0.0.1", port);
        await client.ConnectAsync();
        using var raw = await accept;

        var outKey = new byte[32];   outKey[31] = 1;
        var inKey  = new byte[32];   inKey[31]  = 2;
        client.EnableEncryption(outKey, inKey);

        var payload = Encoding.ASCII.GetBytes("encrypted hello, world!");
        await client.SendAsync(CompanionFrameType.E_OPACK, payload);

        // Read the entire frame from the server side
        var rs = raw.GetStream();
        var head = new byte[4];
        int read = 0; while (read < 4) read += await rs.ReadAsync(head.AsMemory(read));
        int payloadLen = (head[1] << 16) | (head[2] << 8) | head[3];
        var body = new byte[payloadLen];
        read = 0; while (read < payloadLen) read += await rs.ReadAsync(body.AsMemory(read));

        var serverCipher = new CompanionCipher(inKey, outKey);   // swapped roles
        var decoded = serverCipher.Decrypt(body, head);
        Assert.Equal("encrypted hello, world!", Encoding.ASCII.GetString(decoded));

        listener.Stop();
    }

    [Fact]
    public async Task Closed_FiresWhenPeerHangsUp()
    {
        var (port, accept, listener) = StartLoopbackServer();
        await using var conn = new CompanionConnection("127.0.0.1", port);
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Closed += (_, ex) => closed.TrySetResult(ex);
        await conn.ConnectAsync();
        var server = await accept;
        server.Close();

        var ex = await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(ex);    // EOF is graceful
        listener.Stop();
    }

    [Fact]
    public void Constructor_ValidatesArgs()
    {
        Assert.Throws<ArgumentException>(() => new CompanionConnection("", 49152));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompanionConnection("h", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompanionConnection("h", 70000));
    }

    [Fact]
    public async Task SendAsync_BeforeConnect_Throws()
    {
        await using var conn = new CompanionConnection("127.0.0.1", 49152);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => conn.SendAsync(CompanionFrameType.U_OPACK, new byte[1]));
    }

    [Fact]
    public async Task EnableEncryption_Twice_Throws()
    {
        var (port, accept, listener) = StartLoopbackServer();
        await using var conn = new CompanionConnection("127.0.0.1", port);
        await conn.ConnectAsync();
        using var _ = await accept;
        var k = new byte[32];
        conn.EnableEncryption(k, k);
        Assert.Throws<InvalidOperationException>(() => conn.EnableEncryption(k, k));
        listener.Stop();
    }
}
