// SPDX-License-Identifier: MIT
//
// Minimal HTTP/1.1 + RTSP client over a single long-lived TCP connection.
// AirPlay's /pair-pin-start, /pair-setup, /pair-verify (and later the RTSP
// SETUP/RECORD methods) speak plain HTTP/1.1-style request/response on port
// 7000. After pair-verify the same socket switches to encrypted framing, so
// this client also exposes the raw NetworkStream for the encrypted phase to
// take over.
//
// Port of the Swift sibling's AirPlayHTTP.swift, simplified for .NET's
// synchronous-friendly NetworkStream.

using System.Net.Sockets;
using System.Text;

namespace AppleTVProtocol.AirPlay;

public sealed class AirPlayHttp : IDisposable
{
    public sealed record Response(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly List<byte> _buffer = new();

    public AirPlayHttp(string host, int port = 7000)
    {
        _host = host;
        _port = port;
    }

    /// <summary>The underlying stream — for the encrypted transport to take over after pair-verify.</summary>
    public NetworkStream Stream => _stream ?? throw new InvalidOperationException("not connected");
    public TcpClient Tcp => _tcp ?? throw new InvalidOperationException("not connected");

    public async Task ConnectAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>
    /// Send a request (HTTP method or RTSP method) and read one response.
    /// For pairing, method="POST" and path is the endpoint; for the tunnel,
    /// method="SETUP"/"RECORD" and path is the rtsp:// URI, protocol="RTSP/1.0".
    /// </summary>
    public async Task<Response> RequestAsync(
        string method, string path, byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string protocol = "HTTP/1.1",
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("not connected");
        body ??= Array.Empty<byte>();

        var sb = new StringBuilder();
        sb.Append($"{method} {path} {protocol}\r\n");
        sb.Append($"Host: {_host}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        if (headers is not null)
            foreach (var (k, v) in headers) sb.Append($"{k}: {v}\r\n");
        sb.Append("\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var frame = new byte[head.Length + body.Length];
        head.CopyTo(frame, 0);
        body.CopyTo(frame, head.Length);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        await _stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);

        return await ReadResponseAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>POST convenience for the pairing endpoints.</summary>
    public Task<Response> PostAsync(string path, byte[] body,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
        => RequestAsync("POST", path, body, headers, "HTTP/1.1", timeout, ct);

    private async Task<Response> ReadResponseAsync(CancellationToken ct)
    {
        var scratch = new byte[16 * 1024];
        while (true)
        {
            // Look for the header/body separator in whatever we have buffered.
            int sep = IndexOf(_buffer, "\r\n\r\n"u8);
            if (sep >= 0)
            {
                var headerText = Encoding.ASCII.GetString(_buffer.GetRange(0, sep).ToArray());
                var lines = headerText.Split("\r\n");
                var statusParts = lines[0].Split(' ', 3);
                if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out int status))
                    throw new InvalidOperationException($"bad status line: {lines[0]}");

                var hdrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0)
                        hdrs[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
                }

                int bodyStart = sep + 4;
                int contentLength = hdrs.TryGetValue("Content-Length", out var cls) && int.TryParse(cls, out var cl) ? cl : 0;
                int available = _buffer.Count - bodyStart;

                if (available >= contentLength)
                {
                    var bodyBytes = _buffer.GetRange(bodyStart, contentLength).ToArray();
                    _buffer.RemoveRange(0, bodyStart + contentLength);
                    return new Response(status, hdrs, bodyBytes);
                }
            }

            int n = await _stream!.ReadAsync(scratch, ct).ConfigureAwait(false);
            if (n <= 0) throw new InvalidOperationException("connection closed before full response");
            _buffer.AddRange(scratch.AsSpan(0, n).ToArray());
        }
    }

    private static int IndexOf(List<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }
}
