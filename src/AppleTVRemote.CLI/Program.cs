// SPDX-License-Identifier: MIT
//
// Thin command-line front-end for AppleTVProtocol — exists mainly so we
// can drive the protocol stack end-to-end without the WPF UI in the loop.
//
// Today: `atv discover` is the only command. As pair-setup / Companion
// land we'll add `pair`, `key`, `swipe`, `apps`, etc.

using AppleTVProtocol;

return await CliMain(args);

static async Task<int> CliMain(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        PrintUsage();
        return args.Length == 0 ? 1 : 0;
    }

    switch (args[0])
    {
        case "discover":
            return await DiscoverCommand(args[1..]);
        case "pair":
            return await PairCommand(args[1..]);
        case "list":
            return ListCommand();
        case "unpair":
            return UnpairCommand(args[1..]);
        case "key":
            return await KeyCommand(args[1..]);
        case "swipe":
            return await SwipeCommand(args[1..]);
        case "apps":
            return await AppsCommand(args[1..]);
        case "launch":
            return await LaunchCommand(args[1..]);
        default:
            Console.Error.WriteLine($"unknown command: {args[0]}");
            PrintUsage();
            return 2;
    }
}

static void PrintUsage()
{
    Console.WriteLine("""
        Usage:
          atv discover [--seconds N] [--service _companion-link._tcp | _mediaremotetv._tcp | _airplay._tcp]
          atv pair <host> <port> [--name <display-name>]
          atv list
          atv unpair <device-id>
          atv key    <device-id> <host> <port> <button> [--hold-ms N]
          atv swipe  <device-id> <host> <port> <direction> [--duration-ms N]
          atv apps   <device-id> <host> <port>
          atv launch <device-id> <host> <port> <bundle-id>

        Commands:
          discover    Browse the LAN for Apple TVs via mDNS and print what we find.
          pair        Run HAP pair-setup against an Apple TV at host:port. Prompts for the PIN
                      shown on the TV; on success, writes credentials to %APPDATA%.
          list        List paired devices.
          unpair      Forget credentials for a paired device.
          key         Press a button (HID).
                      Buttons: up, down, left, right, select, menu, home, playpause,
                               volumeup, volumedown, wake, sleep, pageup, pagedown, guide
          swipe       Animate a swipe (touchpad). Directions: up, down, left, right.
          apps        Fetch the list of launchable apps on the device.
          launch      Launch an app by bundle id.
        """);
}

static async Task<int> DiscoverCommand(string[] args)
{
    int seconds = 5;
    string service = DeviceDiscovery.CompanionService;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--seconds" or "-s":
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out seconds) || seconds <= 0)
                {
                    Console.Error.WriteLine("--seconds expects a positive integer");
                    return 2;
                }
                break;
            case "--service":
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--service expects a value");
                    return 2;
                }
                service = args[++i];
                break;
            default:
                Console.Error.WriteLine($"unknown discover option: {args[i]}");
                return 2;
        }
    }

    Console.WriteLine($"Browsing {service} for {seconds}s …");
    using var discovery = new DeviceDiscovery(service);
    discovery.Discovered += (_, dev) =>
        Console.WriteLine($"  found  {dev.Name,-24}  {dev.Address}:{dev.Port}  model={dev.ModelFriendly}  rpmd={dev.Rpmd ?? "?"}");

    var devices = await discovery.BrowseAsync(TimeSpan.FromSeconds(seconds));

    Console.WriteLine();
    Console.WriteLine(devices.Count == 0
        ? "No Apple TVs found. (Are you on the same LAN? Some VPNs / corporate networks block mDNS.)"
        : $"Done — {devices.Count} device(s).");

    foreach (var dev in devices)
    {
        Console.WriteLine();
        Console.WriteLine($"  {dev.Name}  ({dev.FullName})");
        Console.WriteLine($"    address : {dev.Address}:{dev.Port}");
        if (dev.TxtRecords.Count > 0)
        {
            Console.WriteLine("    txt     :");
            foreach (var (k, v) in dev.TxtRecords)
                Console.WriteLine($"              {k} = {v}");
        }
    }

    return devices.Count > 0 ? 0 : 1;
}

static async Task<int> PairCommand(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: atv pair <host> <port> [--name <display-name>]");
        return 2;
    }
    string host = args[0];
    if (!int.TryParse(args[1], out int port) || port <= 0 || port > 65535)
    {
        Console.Error.WriteLine($"invalid port: {args[1]}");
        return 2;
    }
    string? name = null;
    for (int i = 2; i < args.Length; i++)
    {
        if ((args[i] == "--name" || args[i] == "-n") && i + 1 < args.Length)
        {
            name = args[++i];
        }
    }

    Console.WriteLine($"Connecting to {host}:{port} …");
    await using var conn = new CompanionConnection(host, port);
    try { await conn.ConnectAsync(); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"failed to connect: {ex.Message}");
        return 3;
    }

    Console.Write("PIN displayed on your Apple TV: ");
    string? pin = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(pin))
    {
        Console.Error.WriteLine("PIN required");
        return 2;
    }

    var pairingId = System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid().ToString());
    var setup = new CompanionPairSetup(conn, timeout: TimeSpan.FromSeconds(30));
    HapCredentials creds;
    try
    {
        creds = await setup.RunAsync(pin, pairingId, clientSeed: null, displayName: name);
    }
    catch (HapPairingException ex)
    {
        Console.Error.WriteLine($"pairing failed: {ex.Message}");
        return 4;
    }

    var deviceIdStr = Convert.ToHexString(creds.AtvIdentifier).ToLowerInvariant();
    var store = new CredentialStore();
    store.Save(new StoredCredentials(
        DeviceId:    deviceIdStr,
        DisplayName: name ?? host,
        Credentials: creds,
        PairedAtUtc: DateTime.UtcNow,
        Host:        host,
        Port:        port));

    Console.WriteLine($"Paired! deviceId={deviceIdStr}");
    Console.WriteLine($"Credentials saved to {store.Root}");
    return 0;
}

static int ListCommand()
{
    var store = new CredentialStore();
    var all = store.LoadAll();
    if (all.Count == 0)
    {
        Console.WriteLine("No paired devices.");
        return 0;
    }
    Console.WriteLine($"{all.Count} paired device(s):");
    foreach (var c in all)
    {
        Console.WriteLine($"  {c.DisplayName,-24}  id={c.DeviceId}  paired={c.PairedAtUtc:u}");
    }
    return 0;
}

static int UnpairCommand(string[] args)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("usage: atv unpair <device-id>");
        return 2;
    }
    var store = new CredentialStore();
    if (!store.Contains(args[0]))
    {
        Console.Error.WriteLine($"no credentials stored for: {args[0]}");
        return 1;
    }
    store.Delete(args[0]);
    Console.WriteLine($"Removed credentials for {args[0]}");
    return 0;
}

static async Task<int> KeyCommand(string[] args)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: atv key <device-id> <host> <port> <button> [--hold-ms N]");
        return 2;
    }
    var deviceId = args[0];
    var host = args[1];
    if (!int.TryParse(args[2], out int port) || port <= 0 || port > 65535)
    {
        Console.Error.WriteLine($"invalid port: {args[2]}");
        return 2;
    }
    if (!Enum.TryParse<HidCommand>(args[3], ignoreCase: true, out var hidCmd))
    {
        Console.Error.WriteLine($"unknown button: {args[3]}");
        return 2;
    }

    int holdMs = 0;
    for (int i = 4; i < args.Length; i++)
    {
        if (args[i] == "--hold-ms" && i + 1 < args.Length && int.TryParse(args[++i], out var hm))
            holdMs = hm;
    }

    var store = new CredentialStore();
    var stored = store.Load(deviceId);
    if (stored is null)
    {
        Console.Error.WriteLine($"no credentials for {deviceId} — run 'atv pair' first");
        return 1;
    }

    await using var conn = new CompanionConnection(host, port);
    try { await conn.ConnectAsync(); }
    catch (Exception ex) { Console.Error.WriteLine($"connect failed: {ex.Message}"); return 3; }

    var verify = new CompanionPairVerify(conn);
    try { await verify.RunAsync(stored.Credentials); }
    catch (HapPairingException ex) { Console.Error.WriteLine($"pair-verify failed: {ex.Message}"); return 4; }

    await using var session = new CompanionSession(conn);
    try
    {
        await session.PressAsync(hidCmd, hold: holdMs > 0 ? TimeSpan.FromMilliseconds(holdMs) : null);
        Console.WriteLine($"Sent {hidCmd}{(holdMs > 0 ? $" (held {holdMs}ms)" : "")}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"key send failed: {ex.Message}");
        return 5;
    }
}

// ─── small helper that opens an encrypted session via stored creds ───
static async Task<(CompanionConnection, CompanionSession)?> OpenSessionAsync(string deviceId, string host, int port)
{
    var store = new CredentialStore();
    var stored = store.Load(deviceId);
    if (stored is null)
    {
        Console.Error.WriteLine($"no credentials for {deviceId} — run 'atv pair' first");
        return null;
    }
    var conn = new CompanionConnection(host, port);
    try { await conn.ConnectAsync(); }
    catch (Exception ex) { Console.Error.WriteLine($"connect failed: {ex.Message}"); await conn.DisposeAsync(); return null; }
    try { await new CompanionPairVerify(conn).RunAsync(stored.Credentials); }
    catch (HapPairingException ex) { Console.Error.WriteLine($"pair-verify failed: {ex.Message}"); await conn.DisposeAsync(); return null; }
    return (conn, new CompanionSession(conn));
}

static async Task<int> SwipeCommand(string[] args)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: atv swipe <device-id> <host> <port> <direction> [--duration-ms N]");
        return 2;
    }
    var deviceId = args[0]; var host = args[1];
    if (!int.TryParse(args[2], out int port)) { Console.Error.WriteLine("invalid port"); return 2; }
    var direction = args[3].ToLowerInvariant();
    int durationMs = 250;
    for (int i = 4; i < args.Length; i++)
        if (args[i] == "--duration-ms" && i + 1 < args.Length && int.TryParse(args[++i], out var d)) durationMs = d;

    (int sx, int sy, int ex, int ey) endpoints = direction switch
    {
        "up"    => (500, 800, 500, 200),
        "down"  => (500, 200, 500, 800),
        "left"  => (800, 500, 200, 500),
        "right" => (200, 500, 800, 500),
        _ => (-1, -1, -1, -1),
    };
    if (endpoints.sx < 0) { Console.Error.WriteLine($"unknown direction: {direction}"); return 2; }

    var opened = await OpenSessionAsync(deviceId, host, port);
    if (opened is null) return 1;
    var (conn, session) = opened.Value;
    try
    {
        await session.SwipeAsync(endpoints.sx, endpoints.sy, endpoints.ex, endpoints.ey, TimeSpan.FromMilliseconds(durationMs));
        Console.WriteLine($"Swiped {direction} over {durationMs}ms");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"swipe failed: {ex.Message}"); return 5; }
    finally { await session.DisposeAsync(); await conn.DisposeAsync(); }
}

static async Task<int> AppsCommand(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: atv apps <device-id> <host> <port>");
        return 2;
    }
    if (!int.TryParse(args[2], out int port)) { Console.Error.WriteLine("invalid port"); return 2; }
    var opened = await OpenSessionAsync(args[0], args[1], port);
    if (opened is null) return 1;
    var (conn, session) = opened.Value;
    try
    {
        var apps = await session.ListAppsAsync();
        if (apps.Count == 0) { Console.WriteLine("(no apps reported)"); return 0; }
        Console.WriteLine($"{apps.Count} app(s):");
        foreach (var (bid, name) in apps)
            Console.WriteLine($"  {name,-40}  {bid}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"apps fetch failed: {ex.Message}"); return 5; }
    finally { await session.DisposeAsync(); await conn.DisposeAsync(); }
}

static async Task<int> LaunchCommand(string[] args)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: atv launch <device-id> <host> <port> <bundle-id>");
        return 2;
    }
    if (!int.TryParse(args[2], out int port)) { Console.Error.WriteLine("invalid port"); return 2; }
    var opened = await OpenSessionAsync(args[0], args[1], port);
    if (opened is null) return 1;
    var (conn, session) = opened.Value;
    try
    {
        await session.LaunchAppAsync(args[3]);
        Console.WriteLine($"Launched {args[3]}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"launch failed: {ex.Message}"); return 5; }
    finally { await session.DisposeAsync(); await conn.DisposeAsync(); }
}
