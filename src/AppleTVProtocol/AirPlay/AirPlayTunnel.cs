// SPDX-License-Identifier: MIT
//
// Opens an encrypted AirPlay-2 RTSP tunnel and brings up the MRP data-stream
// channel that carries now-playing pushes. Port of the Swift sibling's
// AirPlayTunnel.swift + EncryptedAirPlayRTSP.swift + AirPlayEventChannel.swift
// + MRPDataChannel.swift, condensed into one cohesive transport file.
//
// Full sequence (mirrors pyatv/protocols/airplay/ap2_session.py):
//   1. TCP connect host:7000
//   2. AirPlay pair-verify → control keys + ECDH shared secret
//   3. wrap socket in HapSession → encrypted RTSP control channel
//   4. RTSP SETUP #1 (event-channel plist) → eventPort
//   5. open event socket (Events-Salt keys), reply 200 to its RTSP requests
//   6. RTSP RECORD
//   7. RTSP SETUP #2 (data-stream plist, type 130, random seed) → dataPort
//   8. open data socket, derive DataStream keys (salt "DataStream-Salt<seed>")
//   9. MRP init: DEVICE_INFO(Companion clientID) + SET_CONNECTION_STATE +
//      CLIENT_UPDATES_CONFIG + GET_KEYBOARD_SESSION
//  10. receive loop decodes SET_STATE / UPDATE_CONTENT_ITEM → now-playing

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace AppleTVProtocol.AirPlay;

public sealed class AirPlayTunnel : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _channels = new();
    private TcpClient? _controlTcp, _eventTcp, _dataTcp;

    /// <summary>Raised on the receive thread when a now-playing update is decoded.</summary>
    public event EventHandler<MrpNowPlayingUpdate>? NowPlayingUpdated;

    public static async Task<AirPlayTunnel> OpenAsync(
        string host, AirPlayCredentials credentials, string mrpClientId,
        CancellationToken ct = default)
    {
        var tunnel = new AirPlayTunnel();
        try
        {
            await tunnel.OpenInternalAsync(host, credentials, mrpClientId, ct).ConfigureAwait(false);
            return tunnel;
        }
        catch
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task OpenInternalAsync(string host, AirPlayCredentials credentials, string mrpClientId, CancellationToken ct)
    {
        // 1-2. Connect + pair-verify (plain HTTP on the same socket).
        var http = new AirPlayHttp(host, 7000);
        await http.ConnectAsync(ct: ct).ConfigureAwait(false);
        var keys = await new AirPlayPairVerify(http, credentials).VerifyAsync(ct).ConfigureAwait(false);
        _controlTcp = http.Tcp;
        var controlStream = http.Stream;

        // 3. Encrypted RTSP control channel over the same socket.
        var controlSession = new HapSession(writeKey: keys.WriteKey, readKey: keys.ReadKey);
        var control = new RtspControlChannel(controlStream, controlSession);
        control.Start();
        _channels.Add(control);

        // tvOS 18 requires the RTSP URI to be rtsp://<anything>/<random-u32>.
        uint uriId = (uint)Random.Shared.Next(int.MinValue, int.MaxValue);
        string rtspUri = $"rtsp://{GetLocalIp()}/{uriId}";

        // 4. SETUP #1 — event channel.
        var sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var setup1 = await control.RequestAsync("SETUP", rtspUri,
            BuildEventSetupPlist(sessionUuid),
            new() { ["Content-Type"] = "application/x-apple-binary-plist" }, ct).ConfigureAwait(false);
        if (setup1.Status != 200) throw new InvalidOperationException($"SETUP #1 → {setup1.Status}");
        int eventPort = ExtractInt(setup1.Body, "eventPort")
            ?? throw new InvalidOperationException("SETUP #1 missing eventPort");

        // 5. Event socket (must exist + reply 200 to its RTSP requests, or the
        //    TV withholds now-playing pushes). Keys: Events-Salt; pyatv swaps
        //    Read/Write info relative to our HapSession write/read.
        var evWrite = Crypto.HkdfSha512(keys.SharedSecret, "Events-Salt", "Events-Read-Encryption-Key");
        var evRead  = Crypto.HkdfSha512(keys.SharedSecret, "Events-Salt", "Events-Write-Encryption-Key");
        var eventTcp = new TcpClient { NoDelay = true };
        await eventTcp.ConnectAsync(host, eventPort, ct).ConfigureAwait(false);
        _eventTcp = eventTcp;
        var eventChannel = new EventChannel(eventTcp.GetStream(), new HapSession(evWrite, evRead));
        eventChannel.Start();
        _channels.Add(eventChannel);

        // 6. RECORD.
        var record = await control.RequestAsync("RECORD", rtspUri, null, null, ct).ConfigureAwait(false);
        if (record.Status != 200) throw new InvalidOperationException($"RECORD → {record.Status}");

        // 7. SETUP #2 — data stream.
        ulong seed = (ulong)Random.Shared.NextInt64(0, long.MaxValue);
        var setup2 = await control.RequestAsync("SETUP", rtspUri,
            BuildDataSetupPlist(seed),
            new() { ["Content-Type"] = "application/x-apple-binary-plist" }, ct).ConfigureAwait(false);
        if (setup2.Status != 200) throw new InvalidOperationException($"SETUP #2 → {setup2.Status}");
        int dataPort = ExtractStreamDataPort(setup2.Body)
            ?? throw new InvalidOperationException("SETUP #2 missing dataPort");

        // 8. Data socket + DataStream keys (from the control ECDH secret).
        string saltStr = $"DataStream-Salt{seed}";
        var outKey = Crypto.HkdfSha512(keys.SharedSecret, saltStr, "DataStream-Output-Encryption-Key");
        var inKey  = Crypto.HkdfSha512(keys.SharedSecret, saltStr, "DataStream-Input-Encryption-Key");
        var dataTcp = new TcpClient { NoDelay = true };
        await dataTcp.ConnectAsync(host, dataPort, ct).ConfigureAwait(false);
        _dataTcp = dataTcp;
        var mrp = new MrpDataChannel(dataTcp.GetStream(), new HapSession(outKey, inKey));
        mrp.NowPlaying += (_, u) => NowPlayingUpdated?.Invoke(this, u);
        mrp.Start();
        _channels.Add(mrp);

        // 9. MRP init — DEVICE_INFO carries the Companion clientID so the TV
        //    recognizes us and pushes state.
        mrp.Send(MrpMessages.DeviceInfo(mrpClientId));
        mrp.Send(MrpMessages.SetConnectionState());
        mrp.Send(MrpMessages.ClientUpdatesConfig());
        mrp.Send(MrpMessages.GetKeyboardSession());
    }

    private static byte[] BuildEventSetupPlist(string sessionUuid)
    {
        var w = new BinaryPlistWriter();
        var pairs = new List<(BinaryPlistWriter.ObjRef, BinaryPlistWriter.ObjRef)>
        {
            (w.AddString("isRemoteControlOnly"), w.AddBool(true)),
            (w.AddString("osName"),              w.AddString("iPhone OS")),
            (w.AddString("sourceVersion"),       w.AddString("550.10")),
            (w.AddString("timingProtocol"),      w.AddString("None")),
            (w.AddString("model"),               w.AddString("iPhone10,6")),
            (w.AddString("deviceID"),            w.AddString("AA:BB:CC:DD:EE:FF")),
            (w.AddString("osVersion"),           w.AddString("15.0")),
            (w.AddString("osBuildVersion"),      w.AddString("19A5297e")),
            (w.AddString("macAddress"),          w.AddString("AA:BB:CC:DD:EE:FF")),
            (w.AddString("sessionUUID"),         w.AddString(sessionUuid)),
            (w.AddString("name"),                w.AddString("AppleTVRemote")),
        };
        return w.Build(w.AddDict(pairs));
    }

    private static byte[] BuildDataSetupPlist(ulong seed)
    {
        var w = new BinaryPlistWriter();
        var streamPairs = new List<(BinaryPlistWriter.ObjRef, BinaryPlistWriter.ObjRef)>
        {
            (w.AddString("controlType"),          w.AddInt(2)),
            (w.AddString("channelID"),            w.AddString(Guid.NewGuid().ToString().ToUpperInvariant())),
            (w.AddString("seed"),                 w.AddInt((long)seed)),
            (w.AddString("clientUUID"),           w.AddString(Guid.NewGuid().ToString().ToUpperInvariant())),
            (w.AddString("type"),                 w.AddInt(130)),
            (w.AddString("wantsDedicatedSocket"), w.AddBool(true)),
            (w.AddString("clientTypeUUID"),       w.AddString("1910A70F-DBC0-4242-AF95-115DB30604E1")),
        };
        var stream = w.AddDict(streamPairs);
        var streams = w.AddArray(new[] { stream });
        var root = w.AddDict(new[] { (w.AddString("streams"), streams) });
        return w.Build(root);
    }

    private static int? ExtractInt(byte[] plistBytes, string key)
    {
        if (plistBytes.Length == 0) return null;
        if (BinaryPlistReader.Parse(plistBytes) is Dictionary<string, object?> d &&
            d.TryGetValue(key, out var v) && v is long l)
            return (int)l;
        return null;
    }

    private static int? ExtractStreamDataPort(byte[] plistBytes)
    {
        if (plistBytes.Length == 0) return null;
        if (BinaryPlistReader.Parse(plistBytes) is Dictionary<string, object?> d &&
            d.TryGetValue("streams", out var sv) && sv is object?[] arr && arr.Length > 0 &&
            arr[0] is Dictionary<string, object?> s &&
            s.TryGetValue("dataPort", out var dp) && dp is long l)
            return (int)l;
        return null;
    }

    private static string GetLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            return (s.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _channels) { try { await c.DisposeAsync().ConfigureAwait(false); } catch { } }
        _channels.Clear();
        foreach (var t in new[] { _controlTcp, _eventTcp, _dataTcp })
            { try { t?.Dispose(); } catch { } }
    }
}

// ── Encrypted RTSP control channel: request/response over HAP frames ──
internal sealed class RtspControlChannel : IAsyncDisposable
{
    public sealed record Response(int Status, byte[] Body);

    private readonly NetworkStream _stream;
    private readonly HapSession _session;
    private readonly object _gate = new();
    private readonly List<byte> _plain = new();
    private TaskCompletionSource<Response>? _pending;
    private CancellationTokenSource? _loopCts;

    public RtspControlChannel(NetworkStream stream, HapSession session)
    {
        _stream = stream;
        _session = session;
    }

    public void Start()
    {
        _loopCts = new CancellationTokenSource();
        _ = ReadLoopAsync(_loopCts.Token);
    }

    public async Task<Response> RequestAsync(string method, string uri, byte[]? body,
        Dictionary<string, string>? headers, CancellationToken ct)
    {
        body ??= Array.Empty<byte>();
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pending = tcs;

        var sb = new StringBuilder();
        sb.Append($"{method} {uri} RTSP/1.0\r\n");
        if (headers is not null) foreach (var (k, v) in headers) sb.Append($"{k}: {v}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var plain = new byte[head.Length + body.Length];
        head.CopyTo(plain, 0);
        body.CopyTo(plain, head.Length);

        await _stream.WriteAsync(_session.Encrypt(plain), ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        await using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
            return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var scratch = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(scratch, ct).ConfigureAwait(false);
                if (n <= 0) break;
                var dec = _session.Feed(scratch.AsSpan(0, n));
                if (dec.Length == 0) continue;
                _plain.AddRange(dec);
                DrainResponses();
            }
        }
        catch { /* loop ends on socket close / cancel */ }
    }

    private void DrainResponses()
    {
        while (true)
        {
            int sep = IndexOf(_plain, "\r\n\r\n"u8);
            if (sep < 0) break;
            var headerText = Encoding.ASCII.GetString(_plain.GetRange(0, sep).ToArray());
            var lines = headerText.Split("\r\n");
            var statusParts = lines[0].Split(' ', 3);
            int status = (statusParts.Length >= 2 && int.TryParse(statusParts[1], out var s)) ? s : 0;
            int contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                int c = line.IndexOf(':');
                if (c > 0 && line[..c].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line[(c + 1)..].Trim(), out contentLength);
            }
            int bodyStart = sep + 4;
            if (_plain.Count - bodyStart < contentLength) break;
            var bodyBytes = _plain.GetRange(bodyStart, contentLength).ToArray();
            _plain.RemoveRange(0, bodyStart + contentLength);

            TaskCompletionSource<Response>? tcs;
            lock (_gate) { tcs = _pending; _pending = null; }
            tcs?.TrySetResult(new Response(status, bodyBytes));
        }
    }

    internal static int IndexOf(List<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Count; i++)
        {
            bool m = true;
            for (int j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { m = false; break; }
            if (m) return i;
        }
        return -1;
    }

    public ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        return ValueTask.CompletedTask;
    }
}

// ── Event channel: parse inbound RTSP requests, reply 200 OK (echo CSeq) ──
internal sealed class EventChannel : IAsyncDisposable
{
    private readonly NetworkStream _stream;
    private readonly HapSession _session;
    private readonly List<byte> _plain = new();
    private CancellationTokenSource? _loopCts;

    public EventChannel(NetworkStream stream, HapSession session)
    {
        _stream = stream;
        _session = session;
    }

    public void Start()
    {
        _loopCts = new CancellationTokenSource();
        _ = ReadLoopAsync(_loopCts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var scratch = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(scratch, ct).ConfigureAwait(false);
                if (n <= 0) break;
                var dec = _session.Feed(scratch.AsSpan(0, n));
                if (dec.Length == 0) continue;
                _plain.AddRange(dec);
                await DrainRequestsAsync(ct).ConfigureAwait(false);
            }
        }
        catch { }
    }

    private async Task DrainRequestsAsync(CancellationToken ct)
    {
        while (true)
        {
            int sep = RtspControlChannel.IndexOf(_plain, "\r\n\r\n"u8);
            if (sep < 0) break;
            var headerText = Encoding.ASCII.GetString(_plain.GetRange(0, sep).ToArray());
            var lines = headerText.Split("\r\n");
            int contentLength = 0;
            string cseq = "0";
            foreach (var line in lines.Skip(1))
            {
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                var k = line[..c].Trim();
                var v = line[(c + 1)..].Trim();
                if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(v, out contentLength);
                else if (k.Equals("CSeq", StringComparison.OrdinalIgnoreCase)) cseq = v;
            }
            int bodyStart = sep + 4;
            if (_plain.Count - bodyStart < contentLength) break;
            _plain.RemoveRange(0, bodyStart + contentLength);

            var reply = $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nAudio-Latency: 0\r\nContent-Length: 0\r\n\r\n";
            try { await _stream.WriteAsync(_session.Encrypt(Encoding.ASCII.GetBytes(reply)), ct).ConfigureAwait(false); }
            catch { }
        }
    }

    public ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        return ValueTask.CompletedTask;
    }
}

// ── MRP data channel: DataStream framing + sync/rply + plist + MRP decode ──
internal sealed class MrpDataChannel : IAsyncDisposable
{
    private const int HeaderLen = 32;
    private static readonly byte[] SyncType = Concat("sync"u8.ToArray(), new byte[8]);
    private static readonly byte[] RplyType = Concat("rply"u8.ToArray(), new byte[8]);
    private static readonly byte[] CommCmd  = "comm"u8.ToArray();

    private readonly NetworkStream _stream;
    private readonly HapSession _session;
    private readonly List<byte> _plain = new();
    private ulong _sendSeqno = (ulong)Random.Shared.NextInt64(0x1_0000_0000, 0x2_0000_0000);
    private CancellationTokenSource? _loopCts;
    private readonly object _writeGate = new();

    public event EventHandler<MrpNowPlayingUpdate>? NowPlaying;

    public MrpDataChannel(NetworkStream stream, HapSession session)
    {
        _stream = stream;
        _session = session;
    }

    public void Start()
    {
        _loopCts = new CancellationTokenSource();
        _ = ReadLoopAsync(_loopCts.Token);
    }

    public void Send(byte[] mrpFrames)
    {
        var payload = EncodeEnvelope(mrpFrames);
        ulong seq = System.Threading.Interlocked.Increment(ref _sendSeqno);
        var hdr = new byte[HeaderLen + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0), (uint)(HeaderLen + payload.Length));
        SyncType.CopyTo(hdr, 4);
        CommCmd.CopyTo(hdr, 16);
        BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(20), seq);
        // bytes 28..32 stay zero
        payload.CopyTo(hdr, HeaderLen);

        var enc = _session.Encrypt(hdr);
        lock (_writeGate) { _stream.Write(enc, 0, enc.Length); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var scratch = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(scratch, ct).ConfigureAwait(false);
                if (n <= 0) break;
                byte[] dec;
                try { dec = _session.Feed(scratch.AsSpan(0, n)); }
                catch { break; }   // decrypt desync — bail
                if (dec.Length == 0) continue;
                _plain.AddRange(dec);
                ProcessPlaintext();
            }
        }
        catch { }
    }

    private void ProcessPlaintext()
    {
        while (_plain.Count >= HeaderLen)
        {
            uint totalSize = BinaryPrimitives.ReadUInt32BigEndian(_plain.GetRange(0, 4).ToArray());
            if (totalSize < HeaderLen || _plain.Count < totalSize) break;
            var msgType = _plain.GetRange(4, 4).ToArray();
            ulong seqno = BinaryPrimitives.ReadUInt64BigEndian(_plain.GetRange(20, 8).ToArray());
            var payload = _plain.GetRange(HeaderLen, (int)totalSize - HeaderLen).ToArray();
            _plain.RemoveRange(0, (int)totalSize);

            if (msgType.AsSpan().SequenceEqual("sync"u8))
                SendReply(seqno);

            HandlePayload(payload);
        }
    }

    private void HandlePayload(byte[] payload)
    {
        var mrpFrames = DecodeEnvelope(payload);
        if (mrpFrames is null) return;
        // Each varint-length-prefixed MRP ProtocolMessage.
        int offset = 0;
        while (offset < mrpFrames.Length)
        {
            int o = offset;
            ulong len = ReadVarint(mrpFrames, ref o);
            int end = o + (int)len;
            if (end > mrpFrames.Length) break;
            var msg = mrpFrames.AsSpan(o, (int)len);
            var update = MrpNowPlayingDecoder.Decode(msg);
            if (update is not null && !update.IsEmpty)
                NowPlaying?.Invoke(this, update);
            offset = end;
        }
    }

    private void SendReply(ulong seqno)
    {
        var hdr = new byte[HeaderLen];
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0), HeaderLen);
        RplyType.CopyTo(hdr, 4);
        // command + padding stay zero
        BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(20), seqno);
        try { var enc = _session.Encrypt(hdr); lock (_writeGate) { _stream.Write(enc, 0, enc.Length); } }
        catch { }
    }

    // {params:{data:<mrpFrames>}}
    private static byte[] EncodeEnvelope(byte[] mrpFrames)
    {
        var w = new BinaryPlistWriter();
        var dataRef = w.AddData(mrpFrames);
        var inner   = w.AddDict(new[] { (w.AddString("data"), dataRef) });
        var root    = w.AddDict(new[] { (w.AddString("params"), inner) });
        return w.Build(root);
    }

    private static byte[]? DecodeEnvelope(byte[] payload)
    {
        if (payload.Length == 0) return null;
        try
        {
            if (BinaryPlistReader.Parse(payload) is Dictionary<string, object?> d &&
                d.TryGetValue("params", out var pv) && pv is Dictionary<string, object?> p &&
                p.TryGetValue("data", out var dv) && dv is byte[] mrp)
                return mrp;
        }
        catch { }
        return null;
    }

    private static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0; int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0); b.CopyTo(r, a.Length);
        return r;
    }

    public ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        return ValueTask.CompletedTask;
    }
}
