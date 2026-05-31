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

    /// <summary>
    /// Apple's internal model code from the <c>rpmd</c> TXT key —
    /// e.g. <c>K66AP</c>, <c>J42dAP</c>, <c>J305AP</c>. This is what
    /// pyatv keys off (the older <c>model</c> key isn't set on
    /// <c>_companion-link._tcp</c> records).
    /// </summary>
    public string? Rpmd => TxtRecords.GetValueOrDefault("rpmd");

    /// <summary>String form of <see cref="Address"/>. WPF Run.Text doesn't render IPAddress directly.</summary>
    public string AddressString => Address.ToString();

    /// <summary>
    /// Convenience: pyatv's airplay handler keys "is this an Apple
    /// device?" off the <c>model</c> TXT field on
    /// <c>_airplay._tcp.local.</c>. Values look like "AppleTV6,2",
    /// "AppleTV11,1", "AudioAccessory1,1".
    /// </summary>
    public string? AirPlayModel => TxtRecords.GetValueOrDefault("model");

    /// <summary>
    /// True when the device looks like an Apple TV / HomePod-class
    /// peer. We accept TWO signals (mirrors pyatv, which queries both
    /// service types):
    ///   - <c>rpmd</c> TXT key set (from <c>_companion-link._tcp</c>) —
    ///     Apple's internal model code (K66AP, J42dAP, J305AP …);
    ///   - <c>model</c> TXT key starts with "AppleTV" or "AudioAccessory"
    ///     (from <c>_airplay._tcp</c>).
    /// Some Apple TVs only advertise the latter promptly — the
    /// companion-link advert may be slow or sleeping. iPhones in
    /// screen-mirror mode and Macs with Sharing on don't set either
    /// of these.
    /// </summary>
    public bool IsAppleTV
    {
        get
        {
            if (!string.IsNullOrEmpty(Rpmd)) return true;
            var m = AirPlayModel;
            return !string.IsNullOrEmpty(m)
                && (m.StartsWith("AppleTV",        StringComparison.OrdinalIgnoreCase)
                 || m.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Pretty name for the model — looks up the internal code in our
    /// table, falls back to the raw code, falls back to "Apple TV".
    /// </summary>
    public string ModelFriendly => Rpmd switch
    {
        null or "" => "Apple TV",
        var code when AppleTVModelByInternalName.TryGetValue(code, out var pretty) => pretty,
        var code => $"Apple TV ({code})",
    };

    /// <summary>"Apple TV 4K · 192.168.1.42:49152" — convenience for list rows.</summary>
    public string Subtitle => $"{ModelFriendly} · {AddressString}:{Port}";

    /// <summary>
    /// True if this entry came from <c>_companion-link._tcp.local.</c>
    /// (vs <c>_airplay._tcp.local.</c>). Pairing needs a companion-link
    /// entry because that's where the Companion-protocol port lives —
    /// AirPlay's port (typically 7000) is the wrong one.
    /// </summary>
    public bool IsCompanionLinkEntry =>
        FullName.Contains("_companion-link._tcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A stable identifier per physical device, regardless of which
    /// Bonjour service the entry came from. Prefers the <c>deviceid</c>
    /// TXT key (a MAC-format string shared by all Apple services on
    /// the same device); falls back to the IP address.
    /// </summary>
    public string DeviceKey => DeviceId ?? AddressString;

    // ── Apple TV internal-name table (synced with pyatv's _INTERNAL_NAME_LIST) ──
    private static readonly Dictionary<string, string> AppleTVModelByInternalName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["K66AP"]   = "Apple TV (2nd gen)",
        ["J33AP"]   = "Apple TV (3rd gen)",
        ["J33IAP"]  = "Apple TV (3rd gen)",
        ["J42dAP"]  = "Apple TV HD",
        ["J105aAP"] = "Apple TV 4K",
        ["J305AP"]  = "Apple TV 4K (2nd gen)",
        ["J255AP"]  = "Apple TV 4K (3rd gen)",
    };

    private static readonly HashSet<string> AppleTVInternalModelNames =
        new(AppleTVModelByInternalName.Keys, StringComparer.OrdinalIgnoreCase);
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
    private readonly string[] _services;
    private System.Threading.Timer? _requeryTimer;

    /// <summary>How often to re-issue the mDNS query while running.</summary>
    public TimeSpan RequeryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised when a new device is fully resolved (SRV + A/AAAA + TXT all present).</summary>
    public event EventHandler<AppleTVDevice>? Discovered;

    /// <param name="service">
    /// A bonjour service prefix (default: <see cref="CompanionService"/>).
    /// Older callers that pass just one service still work; pass
    /// <see cref="CompanionAndAirplay"/> to query both — this is what
    /// the pair UIs do, because some Apple TVs respond to AirPlay
    /// faster than to companion-link, and the AirPlay TXT carries a
    /// <c>model</c> field we can recognise even when <c>rpmd</c>
    /// hasn't arrived yet.
    /// </param>
    public DeviceDiscovery(string service = CompanionService)
        : this(new[] { service }) { }

    public DeviceDiscovery(IEnumerable<string> services)
    {
        _services = services.Select(s =>
            s.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? s : s + ".local").ToArray();
        if (_services.Length == 0) throw new ArgumentException("at least one service required", nameof(services));
        _sd.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
    }

    /// <summary>Companion + AirPlay — the two services pyatv queries for Apple TV scans.</summary>
    public static readonly string[] CompanionAndAirplay =
        { CompanionService, AirPlayService };

    /// <summary>
    /// Send an active query for every configured service and start a
    /// periodic re-query timer (<see cref="RequeryInterval"/>). Slow
    /// / sleeping devices often miss the first shout — repeating
    /// gives them a chance to answer, and querying multiple services
    /// in parallel sometimes wakes a TV that was ignoring just one.
    /// </summary>
    public void Start()
    {
        Requery();
        _requeryTimer?.Dispose();
        _requeryTimer = new System.Threading.Timer(
            _ => Requery(),
            state: null,
            dueTime:  RequeryInterval,
            period:   RequeryInterval);
    }

    private void Requery()
    {
        foreach (var s in _services)
        {
            try { _sd.QueryServiceInstances(s); }
            catch { /* ignore transient socket errors; the next tick retries */ }
        }
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
