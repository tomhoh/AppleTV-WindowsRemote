// SPDX-License-Identifier: MIT
//
// Application-layer session on top of an encrypted CompanionConnection.
//
// Once pair-verify has installed session keys, every Companion request
// is an E_OPACK frame containing an OPACK dict:
//
//   {
//     "_i": "<identifier>",   // event/command name, e.g. "_hidC"
//     "_t": 2,                 // 1=Event, 2=Request, 3=Response
//     "_c": { ... },           // command-specific content dict
//     "_x": <xid>              // request id, copied back on response
//   }
//
// We track an outbound XID counter and a map of pending responses keyed
// by XID; the underlying CompanionConnection's FrameReceived event
// dispatches each incoming response to whichever request it matches.

using System.Collections.Concurrent;

namespace AppleTVProtocol;

/// <summary>HID usage codes accepted by the Companion <c>_hidC</c> command.</summary>
public enum HidCommand
{
    Up               = 1,
    Down             = 2,
    Left             = 3,
    Right            = 4,
    Menu             = 5,
    Select           = 6,
    Home             = 7,
    VolumeUp         = 8,
    VolumeDown       = 9,
    Siri             = 10,
    Screensaver      = 11,
    Sleep            = 12,
    Wake             = 13,
    PlayPause        = 14,
    ChannelIncrement = 15,
    ChannelDecrement = 16,
    Guide            = 17,
    PageUp           = 18,
    PageDown         = 19,
}

/// <summary>OPACK message types — the value of the <c>_t</c> field.</summary>
public enum CompanionMessageType
{
    Event    = 1,
    Request  = 2,
    Response = 3,
}

/// <summary>
/// Touchpad action phases — what the touchpad is being told happened
/// to the finger. <c>Press</c> begins a stroke, <c>Hold</c> continues
/// it, <c>Release</c> ends it; <c>Click</c> is the
/// click-the-touchpad-as-a-button variant.
/// </summary>
public enum TouchAction
{
    Press   = 1,
    Hold    = 3,
    Release = 4,
    Click   = 5,
}

/// <summary>
/// Wraps a pair-verified <see cref="CompanionConnection"/> and exposes
/// request/response and fire-and-forget HID commands.
/// </summary>
public sealed class CompanionSession : IAsyncDisposable
{
    private readonly CompanionConnection _conn;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IDictionary<string, object?>>> _pending = new();
    private int _xidCounter;
    private TimeSpan _defaultTimeout = TimeSpan.FromSeconds(5);

    public CompanionSession(CompanionConnection conn)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        if (!_conn.EncryptionEnabled)
            throw new InvalidOperationException("CompanionSession requires the connection to be in encrypted mode (run pair-verify first)");
        _xidCounter = Random.Shared.Next();  // initial XID, mirrors pyatv
        _conn.FrameReceived += OnFrameReceived;
    }

    /// <summary>
    /// Run the post-pair-verify session-init handshake. Mirrors pyatv's
    /// CompanionAPI.connect(): system_info → touch_start → session_start →
    /// text_input_start → subscribe_event("_iMC"), all AWAITED in sequence.
    /// The TV ignores app-launcher RPCs (FetchLaunchableApplicationsEvent,
    /// _launchApp) until the client has acknowledged all four init responses
    /// — fire-and-forget left every reply orphaned in _pending and the next
    /// FetchApps just timed out. Mirrors pyatv api.py:153-158.
    /// </summary>
    public async Task SendSessionInitAsync(string clientId, string deviceName, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))   throw new ArgumentException("clientId required",   nameof(clientId));
        if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentException("deviceName required", nameof(deviceName));

        // 1. _systemInfo
        var sysInfo = new Dictionary<string, object?>
        {
            { "_bf",    0 },
            { "_cf",    512 },
            { "_clFl",  128 },
            { "_i",     clientId },
            { "_idsID", System.Text.Encoding.UTF8.GetBytes(clientId) },
            { "_pubID", clientId },
            { "_sf",    256 },
            { "_sv",    "170.18" },
            { "model",  "MacBookPro" },
            { "name",   deviceName },
        };
        await SendRequestAsync("_systemInfo", sysInfo, timeout: TimeSpan.FromSeconds(10), cancel: cancel).ConfigureAwait(false);

        // 2. _touchStart — load-bearing: without it the ATV closes the
        //    socket ~35 s after pair-verify (mirrors pyatv api.py:450).
        var touchStart = new Dictionary<string, object?>
        {
            { "_height", 1000 },
            { "_tFl",    0 },
            { "_width",  1000 },
        };
        await SendRequestAsync("_touchStart", touchStart, timeout: TimeSpan.FromSeconds(10), cancel: cancel).ConfigureAwait(false);

        // 3. _sessionStart — async-await pattern mirrors pyatv _session_start().
        uint sid = (uint)Random.Shared.Next(int.MinValue, int.MaxValue);
        var sessionStart = new Dictionary<string, object?>
        {
            { "_srvT", "com.apple.tvremoteservices" },
            { "_sid",  sid },
        };
        await SendRequestAsync("_sessionStart", sessionStart, timeout: TimeSpan.FromSeconds(10), cancel: cancel).ConfigureAwait(false);

        // 4. _tiStart — text-input session start, empty content.
        await SendRequestAsync("_tiStart", new Dictionary<string, object?>(), timeout: TimeSpan.FromSeconds(10), cancel: cancel).ConfigureAwait(false);

        // 5. Subscribe to events (Swift sibling's exact set). Fire-and-forget.
        var interest = new Dictionary<string, object?>
        {
            { "_regEvents", new object[] { "_iMC", "SystemStatus", "TVSystemStatus", "_tiStarted", "_tiStopped" } },
        };
        await SendEventNoWaitAsync("_interest", interest, cancel).ConfigureAwait(false);

        // 6. _fetchAttentionState — Request, AWAIT response.
        // This is what Swift CompanionSession does on receipt of the
        // _sessionStart response (CompanionSession.swift:576-592): it sends
        // _interest, then _fetchAttentionState, and only fires
        // sessionDidConfirmStart() after the latter's reply arrives. Without
        // this final handshake the TV processes session-init replies and HID
        // commands but silently drops FetchLaunchableApplicationsEvent and
        // _launchApp — observed end-to-end in the v0.1.30 wire trace.
        await SendRequestAsync("_fetchAttentionState", new Dictionary<string, object?>(),
            timeout: TimeSpan.FromSeconds(10), cancel: cancel).ConfigureAwait(false);
    }

    private async Task<int> SendRequestNoWaitAsync(string identifier, IDictionary<string, object?> content, CancellationToken cancel)
    {
        int xid = System.Threading.Interlocked.Increment(ref _xidCounter);
        var envelope = new Dictionary<string, object?>
        {
            { "_i", identifier },
            { "_t", (int)CompanionMessageType.Request },
            { "_c", content },
            { "_x", xid },
        };
        await _conn.SendAsync(CompanionFrameType.E_OPACK, Opack.Pack(envelope), cancel).ConfigureAwait(false);
        return xid;
    }

    private async Task SendEventNoWaitAsync(string identifier, IDictionary<string, object?> content, CancellationToken cancel)
    {
        int xid = System.Threading.Interlocked.Increment(ref _xidCounter);
        var envelope = new Dictionary<string, object?>
        {
            { "_i", identifier },
            { "_t", (int)CompanionMessageType.Event },
            { "_c", content },
            { "_x", xid },
        };
        await _conn.SendAsync(CompanionFrameType.E_OPACK, Opack.Pack(envelope), cancel).ConfigureAwait(false);
    }

    private async Task SendResponseNoWaitAsync(string identifier, int xid, IDictionary<string, object?>? content, CancellationToken cancel)
    {
        var envelope = new Dictionary<string, object?>
        {
            { "_i", identifier },
            { "_t", (int)CompanionMessageType.Response },
            { "_c", content ?? new Dictionary<string, object?>() },
            { "_x", xid },
        };
        await _conn.SendAsync(CompanionFrameType.E_OPACK, Opack.Pack(envelope), cancel).ConfigureAwait(false);
    }

    public TimeSpan DefaultTimeout
    {
        get => _defaultTimeout;
        set => _defaultTimeout = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    /// Issue a request and await the matching response.
    /// </summary>
    public async Task<IDictionary<string, object?>> SendRequestAsync(
        string identifier,
        IDictionary<string, object?> content,
        TimeSpan? timeout = null,
        CancellationToken cancel = default)
    {
        int xid = System.Threading.Interlocked.Increment(ref _xidCounter);
        var envelope = new Dictionary<string, object?>
        {
            { "_i", identifier },
            { "_t", (int)CompanionMessageType.Request },
            { "_c", content },
            { "_x", xid },
        };

        var tcs = new TaskCompletionSource<IDictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(xid, tcs))
            throw new InvalidOperationException($"XID collision: {xid}");

        try
        {
            await _conn.SendAsync(CompanionFrameType.E_OPACK, Opack.Pack(envelope), cancel).ConfigureAwait(false);

            var effective = timeout ?? _defaultTimeout;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeoutCts.CancelAfter(effective);
            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token)))
            {
                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancel.IsCancellationRequested)
                {
                    if (CompanionTrace.IsEnabled)
                        CompanionTrace.Write("SEND", $"timeout waiting for response to {identifier} xid=0x{xid:x8} after {effective.TotalSeconds:0}s");
                    throw;
                }
            }
        }
        finally
        {
            _pending.TryRemove(xid, out _);
        }
    }

    /// <summary>
    /// Send a one-way event (no response expected).
    /// </summary>
    public Task SendEventAsync(string identifier, IDictionary<string, object?> content, CancellationToken cancel = default)
    {
        int xid = System.Threading.Interlocked.Increment(ref _xidCounter);
        var envelope = new Dictionary<string, object?>
        {
            { "_i", identifier },
            { "_t", (int)CompanionMessageType.Event },
            { "_c", content },
            { "_x", xid },
        };
        return _conn.SendAsync(CompanionFrameType.E_OPACK, Opack.Pack(envelope), cancel);
    }

    /// <summary>
    /// Send a single HID button state-change.
    /// </summary>
    public Task SendHidAsync(HidCommand command, bool down, CancellationToken cancel = default)
    {
        var content = new Dictionary<string, object?>
        {
            { "_hBtS", down ? 1 : 2 },
            { "_hidC", (int)command },
        };
        return SendRequestAsync("_hidC", content, timeout: _defaultTimeout, cancel: cancel);
    }

    /// <summary>
    /// Press a button: send down, optionally hold for <paramref name="hold"/>,
    /// then send up.
    /// </summary>
    public async Task PressAsync(HidCommand command, TimeSpan? hold = null, CancellationToken cancel = default)
    {
        await SendHidAsync(command, down: true,  cancel: cancel).ConfigureAwait(false);
        if (hold is { TotalMilliseconds: > 0 } h)
            await Task.Delay(h, cancel).ConfigureAwait(false);
        await SendHidAsync(command, down: false, cancel: cancel).ConfigureAwait(false);
    }

    // ─── Touchpad / swipe ───

    /// <summary>Logical touchpad dimensions used by Apple's Siri Remote model. Coordinates 0..1000.</summary>
    public const int TouchpadWidth  = 1000;
    public const int TouchpadHeight = 1000;
    private static readonly TimeSpan TouchInterpolationDelay = TimeSpan.FromMilliseconds(16);

    private readonly long _baseTimestampNanos = DateTime.UtcNow.Ticks * 100;

    /// <summary>
    /// Send a single touch event (fire-and-forget — pyatv uses
    /// MessageType.Event for touch).
    /// </summary>
    public Task SendTouchAsync(int x, int y, TouchAction action, CancellationToken cancel = default)
    {
        x = Math.Clamp(x, 0, TouchpadWidth);
        y = Math.Clamp(y, 0, TouchpadHeight);
        long nanosSinceBase = (DateTime.UtcNow.Ticks * 100) - _baseTimestampNanos;
        var content = new Dictionary<string, object?>
        {
            { "_ns",  nanosSinceBase },
            { "_tFg", 1 },               // touch finger count = 1
            { "_cx",  x },
            { "_tPh", (int)action },
            { "_cy",  y },
        };
        return SendEventAsync("_hidT", content, cancel);
    }

    /// <summary>
    /// Animate a swipe from (startX, startY) to (endX, endY) over
    /// <paramref name="duration"/>. Interpolates positions every ~16 ms
    /// (matching pyatv's TOUCHPAD_DELAY_MS). Sends Press at start,
    /// Hold for each interpolated point, Release at end.
    /// </summary>
    public async Task SwipeAsync(
        int startX, int startY, int endX, int endY,
        TimeSpan duration, CancellationToken cancel = default)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "must be positive");

        await SendTouchAsync(startX, startY, TouchAction.Press, cancel).ConfigureAwait(false);

        var endAt = DateTime.UtcNow + duration;
        double x = startX, y = startY;
        while (DateTime.UtcNow < endAt)
        {
            var remaining = endAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            // Linear interpolation toward (endX, endY).
            double fraction = TouchInterpolationDelay.TotalMilliseconds / Math.Max(1, remaining.TotalMilliseconds);
            x += (endX - x) * fraction;
            y += (endY - y) * fraction;
            await SendTouchAsync((int)x, (int)y, TouchAction.Hold, cancel).ConfigureAwait(false);
            try { await Task.Delay(TouchInterpolationDelay, cancel).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }

        await SendTouchAsync(endX, endY, TouchAction.Release, cancel).ConfigureAwait(false);
    }

    // ─── App launcher ───

    /// <summary>
    /// Fetch the list of launchable apps from the Apple TV. Returns a
    /// dict keyed by bundle identifier; the value is the app's display
    /// name. The exact shape pyatv expects: response[_c]["FetchLaunchableApplicationsEventResponseDictionary"]
    /// is a dict {bundleId → displayName}.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ListAppsAsync(CancellationToken cancel = default)
    {
        // The TV can take a while to enumerate every installed app — push the
        // 5 s default out to 30 s before we declare it dead.
        var resp = await SendRequestAsync("FetchLaunchableApplicationsEvent",
            new Dictionary<string, object?>(), timeout: TimeSpan.FromSeconds(30), cancel: cancel).ConfigureAwait(false);

        // Walk down resp["_c"]["FetchLaunchableApplicationsEventResponseDictionary"]
        if (!resp.TryGetValue("_c", out var contentBoxed)) return new Dictionary<string, string>();
        var content = ConvertToStringKeyed(contentBoxed);
        if (content is null) return new Dictionary<string, string>();
        if (!content.TryGetValue("FetchLaunchableApplicationsEventResponseDictionary", out var appsBoxed))
            return new Dictionary<string, string>();
        var apps = ConvertToStringKeyed(appsBoxed);
        if (apps is null) return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(apps.Count);
        foreach (var kv in apps)
            result[kv.Key] = kv.Value?.ToString() ?? string.Empty;
        return result;
    }

    /// <summary>
    /// Launch an app by bundle identifier (e.g. <c>com.netflix.Netflix</c>).
    /// </summary>
    public Task LaunchAppAsync(string bundleId, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(bundleId))
            throw new ArgumentException("bundleId required", nameof(bundleId));
        var content = new Dictionary<string, object?> { { "_bundleID", bundleId } };
        return SendRequestAsync("_launchApp", content, cancel: cancel);
    }

    public ValueTask DisposeAsync()
    {
        _conn.FrameReceived -= OnFrameReceived;
        foreach (var tcs in _pending.Values)
            tcs.TrySetCanceled();
        _pending.Clear();
        return ValueTask.CompletedTask;
    }

    private void OnFrameReceived(object? sender, CompanionFrame frame)
    {
        if (frame.Type != CompanionFrameType.E_OPACK || frame.Payload.Length == 0) return;
        IDictionary<string, object?>? dict;
        try
        {
            var (unpacked, _) = Opack.Unpack(frame.Payload);
            dict = ConvertToStringKeyed(unpacked);
        }
        catch (Exception ex)
        {
            // Surface decoder failures into the trace — silent drops were
            // exactly the failure mode for the launcher: a 6 KB app-list
            // response that our OPACK reader choked on, and the request
            // timed out as "Couldn't load apps" with no visible cause.
            if (CompanionTrace.IsEnabled)
            {
                CompanionTrace.Write("RECV", $"Opack.Unpack threw: {ex.GetType().Name}: {ex.Message}");
                CompanionTrace.WriteBytes("RECV", "undecoded payload", frame.Payload, maxBytes: 256);
            }
            return;
        }
        if (dict is null)
        {
            if (CompanionTrace.IsEnabled)
                CompanionTrace.Write("RECV", $"frame decoded but top-level dict had non-string keys, dropped ({frame.Payload.Length}B)");
            return;
        }

        if (!dict.TryGetValue("_t", out var typeBoxed))
        {
            if (CompanionTrace.IsEnabled)
                CompanionTrace.Write("RECV", $"frame decoded, no _t key (keys: {string.Join(",", dict.Keys)}), {frame.Payload.Length}B");
            return;
        }
        if (UnboxInt(typeBoxed) is not { } type)
        {
            if (CompanionTrace.IsEnabled)
                CompanionTrace.Write("RECV", $"frame has _t but couldn't unbox to int (type={typeBoxed?.GetType().Name}), {frame.Payload.Length}B");
            return;
        }
        dict.TryGetValue("_x", out var xidBoxed);
        int? xid = UnboxInt(xidBoxed);
        dict.TryGetValue("_i", out var idBoxed);
        string identifier = idBoxed as string ?? string.Empty;

        switch ((CompanionMessageType)type)
        {
            case CompanionMessageType.Request:
                // The ATV expects an immediate response to _heartbeat /
                // _ping requests. Without it the connection is considered
                // dead and the socket is closed mid-session — which is
                // exactly the "first command works, the rest time out"
                // symptom (mirrors Swift's onInbound _heartbeat / _ping).
                if (xid is int reqXid)
                {
                    if (identifier == "_heartbeat")
                        _ = SendResponseNoWaitAsync("_heartbeat", reqXid, null, CancellationToken.None);
                    else if (identifier == "_ping")
                        _ = SendResponseNoWaitAsync("_pong",      reqXid, null, CancellationToken.None);
                }
                return;

            case CompanionMessageType.Response:
                if (xid is not int respXid)
                {
                    if (CompanionTrace.IsEnabled)
                        CompanionTrace.Write("RECV", $"response had no _x, dropped ({frame.Payload.Length}B, identifier='{identifier}')");
                    return;
                }
                if (_pending.TryRemove(respXid, out var tcs))
                {
                    tcs.TrySetResult(dict);
                    if (CompanionTrace.IsEnabled)
                        CompanionTrace.Write("RECV", $"dispatched response xid=0x{respXid:x8} ({frame.Payload.Length}B)");
                }
                else if (CompanionTrace.IsEnabled)
                {
                    CompanionTrace.Write("RECV", $"response xid=0x{respXid:x8} has no pending request, dropped ({frame.Payload.Length}B)");
                }
                return;

            case CompanionMessageType.Event:
                // Inbound events (e.g. _iMC media-control updates) don't
                // require a response — they're just keepalive-by-side-effect.
                return;
        }
    }

    /// <summary>
    /// Coerce OPACK's various numeric encodings to <see cref="int"/>.
    /// OPACK returns small ints as <see cref="long"/>, sized ints as
    /// <see cref="SizedInt"/>, and very small literals (0–63) as
    /// <see cref="int"/> already.
    /// </summary>
    internal static int? UnboxInt(object? boxed) => boxed switch
    {
        null      => null,
        int i     => i,
        long l    => (int)l,
        SizedInt s=> (int)s.Value,
        byte b    => b,
        short sh  => sh,
        uint u    => (int)u,
        ulong ul  => (int)ul,
        _ => null,
    };

    /// <summary>
    /// Convert an OPACK-unpacked dict (keys come back as <c>object</c>)
    /// into a string-keyed one. Returns null if non-string keys are present.
    /// </summary>
    internal static IDictionary<string, object?>? ConvertToStringKeyed(object? boxed)
    {
        if (boxed is not System.Collections.IDictionary src) return null;
        var dst = new Dictionary<string, object?>(src.Count);
        foreach (System.Collections.DictionaryEntry kv in src)
        {
            if (kv.Key is not string sk) return null;
            dst[sk] = kv.Value;
        }
        return dst;
    }
}
