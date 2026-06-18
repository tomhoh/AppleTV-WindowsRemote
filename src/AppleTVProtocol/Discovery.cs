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
    /// True when the device looks like an Apple TV. Matches the
    /// sibling Swift port's <c>companionTXTIsAppleTV</c> exactly:
    /// <list type="bullet">
    /// <item>If <c>rpMd</c> (companion-link TXT key — case-insensitive
    /// 'rpMd') starts with "AppleTV" → yes.</item>
    /// <item>If <c>rpMd</c> is present but doesn't start with AppleTV
    /// (HomePod = "AudioAccessory*", Mac = "Mac*") → no.</item>
    /// <item>If <c>rpMd</c> is missing, fall back to <c>rpFl</c>: bit
    /// <c>0x4000</c> is "PIN pairing supported" — set on Apple TVs,
    /// cleared on Macs (0x20000) and HomePods (0x627B2/0x62792).</item>
    /// <item>If neither field is present → assume yes (Apple TVs
    /// sometimes omit TXT records at browse time; we'd rather show
    /// them and let the user pick than silently hide them).</item>
    /// </list>
    /// Also accepts the AirPlay-side <c>model</c> field (values like
    /// "AppleTV6,2") when we discovered the device via _airplay._tcp
    /// instead of _companion-link._tcp.
    /// </summary>
    public bool IsAppleTV
    {
        get
        {
            // Companion-link rpMd is the strongest positive signal — when
            // present and AppleTV* prefixed, it's definitive.
            var rpMd = Rpmd ?? string.Empty;
            if (rpMd.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase))
                return true;
            if (rpMd.Length > 0)
                return false;   // present but not AppleTV → HomePod / Mac / iPhone

            // AirPlay 'model' field — values like "AppleTV11,1" /
            // "AudioAccessory5,1" — same prefix-match rule.
            var airplay = AirPlayModel ?? string.Empty;
            if (airplay.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase))
                return true;
            if (airplay.Length > 0)
                return false;

            // No model field at all → reject by default. Pre-v0.2.4 we
            // defaulted TRUE here ("be lenient") but that's how every
            // half-resolved companion-link advert from HomePods, Macs,
            // iPhones, AirPods companions and screen-shared Macs ended up
            // in the list. Apple TVs reliably advertise model on _airplay
            // even when companion-link is slow — losing a few seconds of
            // visibility is the right trade for kicking the noise out.
            //
            // pyatv also warns the rpFl bit-0x4000 ("PIN pairing supported")
            // check is unreliable across tvOS versions, so we drop it as a
            // primary signal entirely.
            return false;
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

    /// <summary>
    /// User-friendly display name for list rows. AirPlay TXT key 'fn'
    /// (friendlyName) is the user's chosen device name — "Living Room",
    /// "Bedroom" — which is what you actually want to show. Falls back to
    /// the Bonjour instance name (which for companion-link entries is
    /// usually a UUID-looking string, so we use it only when nothing
    /// better is available).
    /// </summary>
    public string DisplayName
    {
        get
        {
            var fn = TxtRecords.GetValueOrDefault("fn");
            if (!string.IsNullOrWhiteSpace(fn)) return fn;
            // Bonjour instance name is informative iff it doesn't look
            // like a UUID/MAC — accept anything containing a space or
            // any lowercase letter (UUIDs are uppercase hex + dashes,
            // MACs are uppercase hex + colons).
            if (!string.IsNullOrWhiteSpace(Name) &&
                (Name.Contains(' ') || Name.Any(char.IsLower)))
                return Name;
            return ModelFriendly;
        }
    }

    /// <summary>"Apple TV 4K · 192.168.1.42:49152" — convenience for list rows.</summary>
    public string Subtitle => $"{ModelFriendly} · {AddressString}:{Port}";

    /// <summary>
    /// Return a new device record that unions TXT keys from <paramref name="other"/>
    /// into ours. Used when the same physical device shows up under both
    /// _companion-link._tcp and _airplay._tcp — each carries different TXT
    /// keys (fn lives on airplay; rpMd on companion-link). We keep
    /// <em>this</em> entry's Port/FullName (the caller picks which entry
    /// "wins" — usually the companion-link one because its Port is the
    /// pairing port) and only merge in TXT keys we don't already have.
    /// </summary>
    public AppleTVDevice MergedWith(AppleTVDevice other)
    {
        var merged = new Dictionary<string, string>(TxtRecords, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in other.TxtRecords)
            merged.TryAdd(kv.Key, kv.Value);
        // Prefer the more user-friendly Bonjour instance name when ours
        // looks like a UUID (companion-link) and the other's doesn't (airplay).
        var name = (!string.IsNullOrWhiteSpace(other.Name) &&
                    (other.Name.Contains(' ') || other.Name.Any(char.IsLower)) &&
                    !Name.Contains(' ') && !Name.Any(char.IsLower))
            ? other.Name
            : Name;
        return new AppleTVDevice(name, FullName, Address, Port, merged);
    }

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
    private CancellationTokenSource? _subnetScanCts;

    /// <summary>How often to re-issue the mDNS query while running.</summary>
    public TimeSpan RequeryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When true (default), <see cref="Start"/> also kicks off a
    /// parallel subnet sweep that unicast-probes every IPv4 address in
    /// the user's local /24 networks. Multicast is unreliable on Windows
    /// (Makaretu's known multi-NIC IGMP-join bug; Tailscale / Hyper-V /
    /// VPN virtual adapters break it further) — the subnet sweep is the
    /// belt-and-suspenders fallback that finds every responsive Apple
    /// TV regardless of whether multicast made it there.
    /// </summary>
    public bool SubnetSweepEnabled { get; set; } = true;

    /// <summary>Per-probe timeout for the subnet sweep. Default 1.5 s.</summary>
    public TimeSpan SubnetProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Max concurrent unicast probes during the subnet sweep.</summary>
    public int SubnetProbeConcurrency { get; set; } = 32;

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
    /// Direct unicast mDNS probe to a known Apple TV IP. Sends a DNS
    /// PTR query for <c>_companion-link._tcp.local.</c> straight to
    /// <paramref name="ip"/>:5353 — bypasses multicast entirely, so it
    /// works even when Windows / VPN / firewall configurations are
    /// dropping multicast packets (which is why our normal discovery
    /// can take minutes to find a TV the macOS sibling sees in seconds).
    ///
    /// Returns the resolved companion-link port + the bundled TXT
    /// records, or <c>null</c> if the TV didn't reply within
    /// <paramref name="timeout"/>. Apple TVs reply to unicast mDNS
    /// queries immediately (it's part of the standard), so this is
    /// usually a sub-100-ms round trip.
    /// </summary>
    public static async Task<AppleTVDevice?> UnicastProbeAsync(
        IPAddress ip, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(3);
        using var udp = new System.Net.Sockets.UdpClient();
        udp.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
            System.Net.Sockets.SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        // Build a DNS query: 1 question for PTR of
        // _companion-link._tcp.local. — Makaretu's Message class can
        // serialise this for us.
        var question = new Question
        {
            Name  = CompanionService + ".local",
            Type  = DnsType.PTR,
            Class = DnsClass.IN,
        };
        var query = new Message { Id = 0 };
        query.Questions.Add(question);
        var queryBytes = query.ToByteArray();

        await udp.SendAsync(queryBytes, queryBytes.Length, new IPEndPoint(ip, 5353)).ConfigureAwait(false);

        // Wait for the response or timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout.Value);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                // Some routers / responders echo our own query back; ignore those.
                if (res.RemoteEndPoint.Address.Equals(IPAddress.Loopback)) continue;

                var msg = new Message();
                msg.Read(res.Buffer, 0, res.Buffer.Length);

                // Find the COMPANION-LINK SRV specifically. Some
                // responders fold unrelated SRVs (airplay, raop) into the
                // additional-records section; picking the first one would
                // give us the wrong port and Companion frames sent there
                // are silently dropped.
                var records = msg.Answers.Concat(msg.AdditionalRecords).ToArray();
                var srv = records.OfType<SRVRecord>().FirstOrDefault(r =>
                    r.Name.ToString().Contains("_companion-link", StringComparison.OrdinalIgnoreCase));
                if (srv is null) continue;

                var dev = TryResolveFromMessage(srv.Name, msg);
                if (dev is not null) return dev;
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }

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

        if (SubnetSweepEnabled)
        {
            _subnetScanCts?.Cancel();
            _subnetScanCts?.Dispose();
            _subnetScanCts = new CancellationTokenSource();
            _ = Task.Run(() => SubnetSweepAsync(_subnetScanCts.Token));
        }
    }

    /// <summary>
    /// Probe every IPv4 address in the local /24 subnet(s) via the
    /// per-IP <see cref="UnicastProbeAsync"/>. Each successful probe
    /// yields a fully-resolved <see cref="AppleTVDevice"/> with TXT/SRV
    /// — the device deduplicates against multicast hits via the same
    /// <c>_devices[FullName]</c> dict so the UI never sees doubles.
    /// </summary>
    private async Task SubnetSweepAsync(CancellationToken ct)
    {
        IReadOnlyList<IPAddress> addresses;
        try { addresses = EnumerateLocalSubnetAddresses(); }
        catch { return; }
        if (addresses.Count == 0) return;

        using var gate = new System.Threading.SemaphoreSlim(SubnetProbeConcurrency);
        var tasks = new List<Task>(addresses.Count);
        foreach (var ip in addresses)
        {
            if (ct.IsCancellationRequested) break;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var dev = await UnicastProbeAsync(ip, SubnetProbeTimeout, ct).ConfigureAwait(false);
                    if (dev is null) return;
                    if (_devices.TryAdd(dev.FullName, dev))
                        Discovered?.Invoke(this, dev);
                }
                catch { /* one bad IP shouldn't kill the sweep */ }
                finally { gate.Release(); }
            }, ct));
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* swallow per-task exceptions; we logged what mattered */ }
    }

    /// <summary>
    /// Enumerate IPv4 addresses in the user's local /24 subnet(s), one
    /// per address. Filters out loopback, link-local, and the obvious
    /// virtual adapters (Tailscale, Hyper-V, WSL, VPN, VirtualBox,
    /// VMware) since those usually point at empty or one-host overlay
    /// networks and just waste probes.
    /// </summary>
    private static IReadOnlyList<IPAddress> EnumerateLocalSubnetAddresses()
    {
        var seen = new HashSet<uint>();
        var result = new List<IPAddress>();

        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)   continue;

            var desc = (nic.Description ?? string.Empty);
            if (desc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Hyper-V",    StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("WSL",        StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VPN",        StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VMware",     StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var ipBytes = ua.Address.GetAddressBytes();
                // Skip link-local 169.254/16
                if (ipBytes[0] == 169 && ipBytes[1] == 254) continue;
                // Force /24: assume the user's TV is on the same /24
                // segment as them. (Most consumer routers; bigger nets
                // would explode probe count.)
                uint network = ((uint)ipBytes[0] << 24) | ((uint)ipBytes[1] << 16) | ((uint)ipBytes[2] << 8);
                if (!seen.Add(network)) continue;
                uint myHost = ipBytes[3];
                for (uint h = 1; h < 255; h++)
                {
                    if (h == myHost) continue;
                    var probe = new IPAddress(new byte[] {
                        (byte)((network >> 24) & 0xff),
                        (byte)((network >> 16) & 0xff),
                        (byte)((network >> 8) & 0xff),
                        (byte)h,
                    });
                    result.Add(probe);
                }
            }
        }
        return result;
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
        _subnetScanCts?.Cancel();
        _subnetScanCts?.Dispose();
        _subnetScanCts = null;
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
