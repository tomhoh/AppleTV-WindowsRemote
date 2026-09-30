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

        // 3b. TVRCSessionStart — "TV Remote Client" session registration.
        // Per pyatv PR #2855 (and matching open issues home-assistant/core
        // #171666, pyatv #2845/#2868): newer tvOS (notably Apple TV 4K gen 3
        // on tvOS 26.x) refuses to bind handlers for
        // FetchLaunchableApplicationsEvent / _fetchAttentionState until a
        // TVRC session is registered with the tvremoted process. Older
        // tvOS responds with an error, which we swallow — safe to always
        // send (pyatv: "Older devices that do not implement this message
        // return an error, so it is safe to always send it.")
        try
        {
            var tvrc = new Dictionary<string, object?>
            {
                { "ProtocolVersionKey", "1.2" },
            };
            await SendRequestAsync("TVRCSessionStart", tvrc, timeout: TimeSpan.FromSeconds(10), cancel: cancel).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No request handler", StringComparison.OrdinalIgnoreCase))
        {
            // Older firmware doesn't know this command — that's fine, skip.
            if (CompanionTrace.IsEnabled)
                CompanionTrace.Write("SEND", "TVRCSessionStart not supported on this firmware (older tvOS), continuing");
        }

        // 4. _tiStart — text-input session start, empty content.
        await SendRequestAsync("_tiStart", new Dictionary<string, object?>(), timeout: TimeSpan.FromSeconds(10), cancel: cancel).ConfigureAwait(false);

        // 5. Subscribe to events (Swift sibling's exact set). Fire-and-forget.
        var interest = new Dictionary<string, object?>
        {
            { "_regEvents", new object[] { "_iMC", "SystemStatus", "TVSystemStatus", "_tiStarted", "_tiStopped" } },
        };
        await SendEventNoWaitAsync("_interest", interest, cancel).ConfigureAwait(false);

        // NOTE: previously we awaited _fetchAttentionState here to mirror
        // Swift's sessionDidConfirmStart() path, but the user's tvOS returns
        // an explicit "MPErrorDomain / No request handler" for that command
        // (seen in the v0.1.31 trace). It doesn't appear to be required for
        // FetchLaunchableApplicationsEvent to work on supported firmwares,
        // and sending it just adds noise. Skip until we have evidence
        // otherwise.
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
                IDictionary<string, object?> resp;
                try
                {
                    resp = await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancel.IsCancellationRequested)
                {
                    if (CompanionTrace.IsEnabled)
                        CompanionTrace.Write("SEND", $"timeout waiting for response to {identifier} xid=0x{xid:x8} after {effective.TotalSeconds:0}s");
                    throw;
                }
                // pyatv-style error surfacing: responses with an _em (error
                // message) field are protocol-level failures, not data.
                if (resp.TryGetValue("_em", out var emBoxed) && emBoxed is string em && em.Length > 0)
                {
                    string domain = resp.TryGetValue("_ed", out var edBoxed) && edBoxed is string ed ? ed : "Companion";
                    int    code   = resp.TryGetValue("_ec", out var ecBoxed) ? UnboxInt(ecBoxed) ?? 0    : 0;
                    if (CompanionTrace.IsEnabled)
                        CompanionTrace.Write("SEND", $"{identifier} xid=0x{xid:x8} returned error: {domain}#{code} \"{em}\"");
                    throw new InvalidOperationException($"{identifier}: {domain}#{code} {em}");
                }
                return resp;
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

    /// <summary>
    /// Hold a button down for a fixed duration, then release — the gesture
    /// the TV interprets as a "long press". Mirrors the Swift sibling's
    /// CompanionSession.sendLongPress: HID down, sleep <paramref name="holdMs"/>,
    /// HID up. Holding Home ≈1 s opens the tvOS app-switcher; holding Menu
    /// the screensaver; holding Select the home-screen jiggle/edit mode.
    /// Uses a programmatic, fixed hold rather than the user's physical
    /// button-hold time so the TV reliably sees a full-duration press.
    /// </summary>
    public Task LongPressAsync(HidCommand command, int holdMs = 1000, CancellationToken cancel = default)
        => PressAsync(command, TimeSpan.FromMilliseconds(holdMs), cancel);

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

        if (!resp.TryGetValue("_c", out var contentBoxed)) return Empty(resp, "no _c key");
        var content = ConvertToStringKeyed(contentBoxed);
        if (content is null) return Empty(resp, "_c had non-string keys");

        // Two known response shapes:
        //   pyatv (older docs): _c["FetchLaunchableApplicationsEventResponseDictionary"][bundleId] = name
        //   Swift sibling + current tvOS: _c[bundleId] = name (flat — _c IS the apps dict)
        // Try the wrapper first, fall back to the flat shape.
        IDictionary<string, object?>? apps = null;
        if (content.TryGetValue("FetchLaunchableApplicationsEventResponseDictionary", out var appsBoxed))
            apps = ConvertToStringKeyed(appsBoxed);
        if (apps is null && content.Count > 0)
            apps = content;   // flat shape: _c itself is the bundleId → name map

        if (apps is null || apps.Count == 0)
            return Empty(resp, $"no apps in response (_c keys: [{string.Join(", ", content.Keys)}])");

        var result = new Dictionary<string, string>(apps.Count);
        foreach (var kv in apps)
        {
            if (kv.Key is null) continue;
            // Filter out non-app entries (anything where the value isn't a string —
            // e.g. status fields the flat-shape response might inline alongside apps).
            if (kv.Value is not string s) continue;
            result[kv.Key] = s;
        }
        if (CompanionTrace.IsEnabled)
            CompanionTrace.Write("RECV", $"FetchLaunchableApplicationsEvent parsed {result.Count} apps");
        return result;

        static IReadOnlyDictionary<string, string> Empty(IDictionary<string, object?> resp, string why)
        {
            if (CompanionTrace.IsEnabled)
                CompanionTrace.Write("RECV", $"FetchLaunchableApplicationsEvent empty: {why}");
            return new Dictionary<string, string>();
        }
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
                // Surface _iMC's inner _c dict to NowPlayingUpdated so the
                // UI can render a Now-Playing banner. The actual contents
                // depend on the TV state: minimal `_mcF` flags when nothing
                // is playing, richer fields (title, artist, app id) when
                // active media exists.
                if (identifier == "_iMC")
                {
                    IDictionary<string, object?>? content = null;
                    if (dict.TryGetValue("_c", out var contentBoxed))
                        content = ConvertToStringKeyed(contentBoxed);
                    NowPlayingUpdated?.Invoke(this,
                        NowPlayingUpdate.FromInnerDict(content ?? new Dictionary<string, object?>()));
                }
                else if (identifier == "_tiStarted")
                {
                    // The TV surfaced a text field. Content dict carries
                    // `_tiD` — the RTI binary plist we'll need to send
                    // characters back at it. Cache + notify.
                    byte[]? tiD = null;
                    if (dict.TryGetValue("_c", out var contentBoxed) &&
                        ConvertToStringKeyed(contentBoxed) is { } content &&
                        content.TryGetValue("_tiD", out var tiBoxed) &&
                        tiBoxed is byte[] tiBytes)
                    {
                        tiD = tiBytes;
                    }
                    _currentTiD = tiD;
                    KeyboardActiveChanged?.Invoke(this, new KeyboardActiveEventArgs(true, tiD));
                }
                else if (identifier == "_tiStopped")
                {
                    _currentTiD = null;
                    KeyboardActiveChanged?.Invoke(this, new KeyboardActiveEventArgs(false, null));
                }
                return;
        }
    }

    /// <summary>
    /// Raised whenever the TV pushes an <c>_iMC</c> (Media Control) event.
    /// The payload is parsed best-effort from the inner <c>_c</c> dict —
    /// most fields are optional and depend on whether media is actually
    /// playing on the TV.
    /// </summary>
    public event EventHandler<NowPlayingUpdate>? NowPlayingUpdated;

    /// <summary>
    /// Raised when the TV pushes <c>_tiStarted</c> (a text field opened) or
    /// <c>_tiStopped</c> (closed). The UI listens for this to show / hide
    /// its floating input strip.
    /// </summary>
    public event EventHandler<KeyboardActiveEventArgs>? KeyboardActiveChanged;

    /// <summary>The most recent `_tiD` (RTI plist) the TV volunteered
    /// via `_tiStarted`, or null if no field is currently active. Kept
    /// as the source of truth for <see cref="SendTextAsync"/>.</summary>
    private byte[]? _currentTiD;

    /// <summary>True iff the TV has an active text field waiting for input.</summary>
    public bool KeyboardActive => _currentTiD is not null;

    /// <summary>
    /// Append <paramref name="text"/> to the ATV's currently-focused text
    /// field via an RTI insertion. No-op if no field is active
    /// (<see cref="KeyboardActive"/> is false). Fire-and-forget event; the
    /// TV echoes the text into its own field but doesn't respond.
    /// </summary>
    public async Task SendTextAsync(string text, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_currentTiD is not { } tiD) return;   // no field open
        var uuid = RTITextOperations.ExtractSessionUuid(tiD);
        if (uuid is null) return;
        var payload = RTITextOperations.InputPayload(uuid, text);
        var content = new Dictionary<string, object?>
        {
            { "_tiV", 1 },
            { "_tiD", payload },
        };
        await SendEventNoWaitAsync("_tiC", content, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete the last character of the ATV's currently-focused text
    /// field. Under the hood this stops+restarts the RTI session to
    /// refresh the session UUID + current text (see Swift sibling), then
    /// asserts the truncated text.
    /// </summary>
    public async Task SendBackspaceAsync(CancellationToken cancel = default)
    {
        // Refresh the RTI session so we read the current text off the
        // TV, not our stale cache — the assert-payload path needs the
        // exact current contents (mirrors Swift's sendBackspace).
        await SendEventNoWaitAsync("_tiStop", new Dictionary<string, object?>(), cancel).ConfigureAwait(false);
        var startResp = await SendRequestAsync(
            "_tiStart",
            new Dictionary<string, object?>(),
            timeout: TimeSpan.FromSeconds(5),
            cancel: cancel).ConfigureAwait(false);
        byte[]? freshTiD = null;
        if (startResp.TryGetValue("_c", out var contentBoxed) &&
            ConvertToStringKeyed(contentBoxed) is { } inner &&
            inner.TryGetValue("_tiD", out var tiBoxed) &&
            tiBoxed is byte[] tiBytes)
        {
            freshTiD = tiBytes;
        }
        if (freshTiD is null) return;
        _currentTiD = freshTiD;

        var uuid = RTITextOperations.ExtractSessionUuid(freshTiD);
        if (uuid is null) return;
        var current = RTITextOperations.ExtractCurrentText(freshTiD) ?? "";
        if (current.Length == 0) return;
        var updated = current.Substring(0, current.Length - 1);

        var payload = RTITextOperations.AssertPayload(uuid, updated);
        var body = new Dictionary<string, object?>
        {
            { "_tiV", 1 },
            { "_tiD", payload },
        };
        await SendEventNoWaitAsync("_tiC", body, cancel).ConfigureAwait(false);
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

/// <summary>
/// Payload for <see cref="CompanionSession.KeyboardActiveChanged"/>.
/// <see cref="Active"/> is true on <c>_tiStarted</c>, false on
/// <c>_tiStopped</c>. <see cref="TiD"/> carries the RTI plist when the
/// field just opened, and is null on close.
/// </summary>
public sealed class KeyboardActiveEventArgs : EventArgs
{
    public bool    Active { get; }
    public byte[]? TiD    { get; }

    public KeyboardActiveEventArgs(bool active, byte[]? tiD)
    {
        Active = active;
        TiD    = tiD;
    }
}
