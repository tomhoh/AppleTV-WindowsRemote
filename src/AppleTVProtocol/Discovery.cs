// SPDX-License-Identifier: MIT
//
// mDNS / Bonjour discovery of Apple TVs on the LAN.
//
// Apple TVs advertise several Bonjour service types; the two we care
// about for remote control are:
//
//   _companion-link._tcp.local.   — Companion protocol (Apple TV HD and
//                                   later running tvOS 12+)
//   _mediaremotetv._tcp.local.    — MRP, the older protocol
//
// This class wraps `Makaretu.Dns.ServiceDiscovery` and surfaces a small
// `AppleTVDevice` record (name, IP, port, TXT key/value pairs) once
// enough records (SRV + A/AAAA + TXT) have been seen for a given
// service instance to actually connect.

using System.Collections.Concurrent;
using System.Net;
using Makaretu.Dns;

namespace AppleTVProtocol;

/// <summary>
/// One Apple TV (or HomePod / other Apple device) found on the LAN
/// advertising the requested service type.
/// </summary>
/// <param name="Name">The instance name — typically the user-set device name (e.g. "Living Room").</param>
/// <param name="FullName">The fully-qualified Bonjour name including the service type.</param>
/// <param name="Address">The first resolved IP address (prefers IPv4 when available).</param>
/// <param name="Port">The TCP port the service is listening on.</param>
/// <param name="TxtRecords">Key/value pairs from the TXT record. Apple TVs put model, device id, and feature flags here.</param>
public sealed record AppleTVDevice(
    string Name,
    string FullName,
    IPAddress Address,
    int Port,
    IReadOnlyDictionary<string, string> TxtRecords)
{
    /// <summary>The "deviceid" TXT value (usually a MAC-like 6-byte hex) — stable across name changes.</summary>
    public string? DeviceId => TxtRecords.GetValueOrDefault("deviceid");

    /// <summary>The "model" TXT value (e.g. "AppleTV6,2", "AppleTV11,1", "AudioAccessory1,1").</summary>
    public string? Model => TxtRecords.GetValueOrDefault("model");

    /// <summary>
    /// String form of <see cref="Address"/> — surfaced as its own
    /// property so WPF XAML can bind to it (Run.Text doesn't render
    /// IPAddress out of the box).
    /// </summary>
    public string AddressString => Address.ToString();

    /// <summary>
    /// True if the model TXT record looks like an Apple TV or HomePod —
    /// the two device families that actually accept Companion-protocol
    /// pairings. Used to filter out random other devices that also
    /// advertise <c>_companion-link._tcp.local.</c> (iPhones in screen-
    /// mirroring mode, Macs with sharing on, etc.).
    /// </summary>
    public bool IsAppleDevice
    {
        get
        {
            var m = Model;
            if (string.IsNullOrEmpty(m)) return false;
            return m.StartsWith("AppleTV",        StringComparison.OrdinalIgnoreCase)
                || m.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A friendlier label for the model TXT value. "AppleTV11,1" → "Apple TV 4K",
    /// "AudioAccessory1,1" → "HomePod", and falls back to the raw model
    /// string when we don't recognise the code.
    /// </summary>
    public string ModelFriendly => Model switch
    {
        null or ""        => "Unknown device",
        "AppleTV5,3"      => "Apple TV HD",
        "AppleTV6,2"      => "Apple TV 4K",
        "AppleTV11,1"     => "Apple TV 4K (2nd gen)",
        "AppleTV14,1"     => "Apple TV 4K (3rd gen)",
        "AudioAccessory1,1" => "HomePod",
        "AudioAccessory5,1" => "HomePod mini",
        "AudioAccessory6,1" => "HomePod (2nd gen)",
        var x when x.StartsWith("AppleTV",        StringComparison.OrdinalIgnoreCase) => "Apple TV (" + x + ")",
        var x when x.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase) => "HomePod (" + x + ")",
        var x => x,
    };

    /// <summary>"Apple TV 4K · 192.168.1.42:49152" — convenience for list rows.</summary>
    public string Subtitle => $"{ModelFriendly} · {AddressString}:{Port}";
}

public sealed class DeviceDiscovery : IDisposable
{
    /// <summary>Bonjour service name for the post-tvOS-12 Companion protocol.</summary>
    public const string CompanionService = "_companion-link._tcp";
    /// <summary>Bonjour service name for the legacy MediaRemote (MRP) protocol.</summary>
    public const string MediaRemoteService = "_mediaremotetv._tcp";
    /// <summary>Bonjour service name for AirPlay (used for now-playing tunnels).</summary>
    public const string AirPlayService = "_airplay._tcp";

    private readonly ServiceDiscovery _sd = new();
    private readonly ConcurrentDictionary<string, AppleTVDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _service;
    private System.Threading.Timer? _requeryTimer;

    /// <summary>How often to re-issue the mDNS query while running.</summary>
    public TimeSpan RequeryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised when a new device is fully resolved (SRV + A/AAAA + TXT all present).</summary>
    public event EventHandler<AppleTVDevice>? Discovered;

    /// <param name="service">A bonjour service prefix (default: <see cref="CompanionService"/>).</param>
    public DeviceDiscovery(string service = CompanionService)
    {
        _service = service.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? service
            : service + ".local";
        _sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
    }

    /// <summary>
    /// Send an active query for the configured service and start a periodic
    /// re-query timer (<see cref="RequeryInterval"/>). The first query
    /// fires immediately; subsequent ones fire on the interval until
    /// <see cref="Dispose"/>. Slow / sleeping devices often miss the first
    /// shout — repeating gives them a chance to answer.
    /// </summary>
    public void Start()
    {
        Requery();
        // Drop any prior timer (calling Start() twice shouldn't double-fire).
        _requeryTimer?.Dispose();
        _requeryTimer = new System.Threading.Timer(
            _ => Requery(),
            state: null,
            dueTime:  RequeryInterval,
            period:   RequeryInterval);
    }

    private void Requery()
    {
        try { _sd.QueryServiceInstances(_service); }
        catch { /* ignore transient socket errors; the next tick retries */ }
    }

    /// <summary>
    /// Convenience: browse for <paramref name="duration"/> and return everything seen.
    /// The re-query timer keeps running until the caller disposes this object —
    /// devices that arrive after <paramref name="duration"/> still fire
    /// <see cref="Discovered"/>, they're just not in the snapshot we return.
    /// </summary>
    public async Task<IReadOnlyList<AppleTVDevice>> BrowseAsync(TimeSpan duration, CancellationToken ct = default)
    {
        Start();
        try { await Task.Delay(duration, ct).ConfigureAwait(false); }
        catch (TaskCanceledException) { }
        return _devices.Values.ToList();
    }

    public void Dispose()
    {
        _requeryTimer?.Dispose();
        _requeryTimer = null;
        _sd.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
        _sd.Dispose();
    }

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        var device = TryResolveFromMessage(e.ServiceInstanceName, e.Message);
        if (device is null) return;

        // First-time arrivals fire the event; later additions silently update the dict.
        if (_devices.TryAdd(device.FullName, device))
        {
            Discovered?.Invoke(this, device);
        }
        else
        {
            _devices[device.FullName] = device;
        }
    }

    /// <summary>String-named convenience for callers / tests that don't want to construct a DomainName.</summary>
    public static AppleTVDevice? TryResolveFromMessage(string instanceFullName, Message message)
        => TryResolveFromMessage((DomainName)instanceFullName, message);

    /// <summary>
    /// Public for testability — given the bundled answers from a Bonjour
    /// PTR/SRV/TXT/A response, extract a fully-formed AppleTVDevice (or null
    /// if records are incomplete).
    /// </summary>
    public static AppleTVDevice? TryResolveFromMessage(DomainName instanceFullName, Message message)
    {
        var records = message.Answers.Concat(message.AdditionalRecords).ToArray();

        // Find the SRV for this instance — that gives us port and the target hostname.
        // Compare DomainName objects directly: Makaretu escapes special characters
        // (spaces → \032 etc.) when serialising to strings, so string comparison
        // would falsely miss e.g. "Living Room._companion-link._tcp.local".
        var srv = records.OfType<SRVRecord>().FirstOrDefault(r => r.Name.Equals(instanceFullName));
        if (srv is null) return null;

        var targetHost = srv.Target;

        // Find the matching A / AAAA record. Prefer IPv4 if both are present.
        var a    = records.OfType<ARecord>()   .FirstOrDefault(r => r.Name.Equals(targetHost));
        var aaaa = records.OfType<AAAARecord>().FirstOrDefault(r => r.Name.Equals(targetHost));
        var addr = (IPAddress?)a?.Address ?? aaaa?.Address;
        if (addr is null) return null;

        // TXT records carry the device id, model, feature flags, etc.
        var txt = records.OfType<TXTRecord>().FirstOrDefault(r => r.Name.Equals(instanceFullName));
        var txtMap = ParseTxtRecord(txt);

        // Build human-readable Name + FullName from the raw labels so spaces
        // and other special characters in the user-set device name stay
        // readable (Makaretu's DomainName.ToString() escapes spaces as \032,
        // which is right for the wire but ugly in a UI / log).
        var labels = instanceFullName.Labels;
        var name = (labels is { Count: > 0 } && labels[0] is { Length: > 0 })
            ? labels[0]
            : instanceFullName.ToString();
        var fullName = labels is { Count: > 0 } ? string.Join('.', labels) : instanceFullName.ToString();

        return new AppleTVDevice(name, fullName, addr, srv.Port, txtMap);
    }

    internal static IReadOnlyDictionary<string, string> ParseTxtRecord(TXTRecord? txt)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (txt is null) return map;
        foreach (var entry in txt.Strings)
        {
            if (string.IsNullOrEmpty(entry)) continue;
            int eq = entry.IndexOf('=');
            if (eq < 0)
            {
                // Boolean-style entry — key with no value
                map[entry] = string.Empty;
            }
            else
            {
                map[entry[..eq]] = entry[(eq + 1)..];
            }
        }
        return map;
    }
}
