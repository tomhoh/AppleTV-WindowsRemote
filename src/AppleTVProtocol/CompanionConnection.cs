// SPDX-License-Identifier: MIT
//
// Async TCP connection that speaks the Companion frame protocol.
//
// Lifecycle:
//   1. ctor with host + port (no IO yet)
//   2. await ConnectAsync(ct) — opens the socket, starts the read loop
//   3. await SendAsync(...) / receive frames via the FrameReceived event
//   4. EnableEncryption(out, in) flips the cipher on after pair-verify
//   5. Dispose closes the socket
//
// The read loop is started in ConnectAsync and tears itself down when
// the connection is closed (server EOF, transport error, or Dispose).

using System.Net.Sockets;

namespace AppleTVProtocol;

/// <summary>
/// Long-lived async TCP connection that frames bytes into
/// <see cref="CompanionFrame"/> objects.
/// </summary>
public sealed class CompanionConnection : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoop;
    private CompanionCipher? _cipher;

    // Serializes the (encrypt → write) critical section so two concurrent
    // SendAsync callers (e.g. user-thread HID click + threadpool keepalive
    // timer) can't race the cipher nonce counter and produce frames the
    // peer can't decrypt. Without this the ATV closes the socket on the
    // first corrupted frame and every subsequent send appears to hang.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Frame received from the peer. Fires on the read-loop thread.</summary>
    public event EventHandler<CompanionFrame>? FrameReceived;

    /// <summary>Underlying TCP connection lost or closed.</summary>
    public event EventHandler<Exception?>? Closed;

    public CompanionConnection(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host required", nameof(host));
        if (port is <= 0 or > 65535)         throw new ArgumentOutOfRangeException(nameof(port));
        _host = host;
        _port = port;
    }

    public bool Connected => _tcp?.Connected == true;
    public bool EncryptionEnabled => _cipher is not null;

    public async Task ConnectAsync(CancellationToken cancel = default)
    {
        if (_tcp is not null) throw new InvalidOperationException("already connected");
        if (CompanionTrace.IsEnabled) CompanionTrace.Write("CONN", $"connecting to {_host}:{_port}");
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(_host, _port, cancel).ConfigureAwait(false);
        _tcp = tcp;
        _stream = tcp.GetStream();
        _readLoopCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
        if (CompanionTrace.IsEnabled) CompanionTrace.Write("CONN", "connected");
    }

    /// <summary>
    /// Turn on ChaCha20-Poly1305 encryption with the given session keys.
    /// Call exactly once, after pair-verify completes.
    /// </summary>
    public void EnableEncryption(byte[] outKey, byte[] inKey)
    {
        if (_cipher is not null) throw new InvalidOperationException("encryption already enabled");
        _cipher = new CompanionCipher(outKey, inKey);
    }

    /// <summary>
    /// Send a frame. If encryption is enabled, the payload is wrapped in
    /// ChaCha20-Poly1305 with the 4-byte header as AAD and a 16-byte tag
    /// appended (so the on-wire payload length grows by 16).
    /// </summary>
    public async Task SendAsync(CompanionFrameType type, byte[] payload, CancellationToken cancel = default)
    {
        if (_stream is null) throw new InvalidOperationException("not connected");

        await _sendLock.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            byte[] wireBytes;
            if (_cipher is not null && payload.Length > 0)
            {
                // Encrypted: header AAD covers ciphertext + tag length.
                // Nonce-counter increment lives inside _cipher.Encrypt; it must
                // be serialized with the matching stream-write below so the
                // peer sees an unbroken nonce sequence on the wire.
                int wirePayloadLength = payload.Length + CompanionFrame.AuthTagLength;
                var header = CompanionFrame.EncodeHeader(type, wirePayloadLength);
                var ct = _cipher.Encrypt(payload, header);
                wireBytes = new byte[header.Length + ct.Length];
                Buffer.BlockCopy(header, 0, wireBytes, 0, header.Length);
                Buffer.BlockCopy(ct, 0, wireBytes, header.Length, ct.Length);
                if (CompanionTrace.IsEnabled)
                    CompanionTrace.WriteBytes("SEND", $"{type} (enc, ctr={_cipher.OutCounter - 1})", payload);
            }
            else
            {
                wireBytes = CompanionFrame.EncodeFrame(type, payload);
                if (CompanionTrace.IsEnabled)
                    CompanionTrace.WriteBytes("SEND", $"{type} (clear)", payload);
            }

            await _stream.WriteAsync(wireBytes, cancel).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancel)
    {
        var buffer = new List<byte>(capacity: 4096);
        var scratch = new byte[4096];
        Exception? closeReason = null;

        try
        {
            while (!cancel.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await _stream!.ReadAsync(scratch, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (CompanionTrace.IsEnabled) CompanionTrace.Write("CONN", $"read error: {ex.GetType().Name}: {ex.Message}");
                    closeReason = ex;
                    break;
                }

                if (n <= 0)
                {
                    if (CompanionTrace.IsEnabled) CompanionTrace.Write("CONN", "EOF from peer (socket closed)");
                    break;
                }
                buffer.AddRange(scratch.AsSpan(0, n).ToArray());

                while (true)
                {
                    var maybe = CompanionFrame.TryDecode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer), out var consumed);
                    if (maybe is null) break;

                    // Snapshot the header bytes BEFORE removing them — they may be AAD
                    var header = buffer.GetRange(0, CompanionFrame.HeaderLength).ToArray();
                    buffer.RemoveRange(0, consumed);

                    var frame = maybe.Value;
                    if (_cipher is not null && frame.Payload.Length > 0)
                    {
                        try
                        {
                            var pt = _cipher.Decrypt(frame.Payload, header);
                            frame = new CompanionFrame(frame.Type, pt);
                        }
                        catch (Exception ex)
                        {
                            if (CompanionTrace.IsEnabled) CompanionTrace.Write("RECV", $"decrypt failed (ctr={_cipher.InCounter - 1}): {ex.Message}");
                            closeReason = ex;
                            cancel = new CancellationToken(canceled: true);   // bail
                            break;
                        }
                    }

                    if (CompanionTrace.IsEnabled)
                        CompanionTrace.WriteBytes("RECV", $"{frame.Type}", frame.Payload);
                    try { FrameReceived?.Invoke(this, frame); }
                    catch { /* listener errors must not kill the read loop */ }
                }
            }
        }
        finally
        {
            if (CompanionTrace.IsEnabled) CompanionTrace.Write("CONN", $"read loop exited (reason: {closeReason?.GetType().Name ?? "clean"})");
            Closed?.Invoke(this, closeReason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _readLoopCts?.Cancel();
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        if (_readLoop is { } t) { try { await t.ConfigureAwait(false); } catch { } }
        _readLoopCts?.Dispose();
        _stream?.Dispose();
        _tcp?.Dispose();
    }
}
