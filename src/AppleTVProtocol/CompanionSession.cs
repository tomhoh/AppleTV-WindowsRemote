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

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeoutCts.CancelAfter(timeout ?? _defaultTimeout);
            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token)))
            {
                return await tcs.Task.ConfigureAwait(false);
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
        catch { return; }
        if (dict is null) return;

        // Only Responses carry an XID that maps to a pending request.
        if (!dict.TryGetValue("_t", out var typeBoxed)) return;
        if (UnboxInt(typeBoxed) is not { } type) return;
        if ((CompanionMessageType)type != CompanionMessageType.Response) return;

        if (!dict.TryGetValue("_x", out var xidBoxed)) return;
        if (UnboxInt(xidBoxed) is not { } xid) return;

        if (_pending.TryRemove(xid, out var tcs))
            tcs.TrySetResult(dict);
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
