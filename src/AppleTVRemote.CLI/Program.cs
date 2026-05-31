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

        Commands:
          discover    Browse the LAN for Apple TVs via mDNS and print what we find.
          pair        Run HAP pair-setup against an Apple TV at host:port. Prompts for the PIN
                      shown on the TV; on success, writes credentials to %APPDATA%.
          list        List paired devices.
          unpair      Forget credentials for a paired device.
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
        Console.WriteLine($"  found  {dev.Name,-24}  {dev.Address}:{dev.Port}  model={dev.Model ?? "?"}  id={dev.DeviceId ?? "?"}");

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
        PairedAtUtc: DateTime.UtcNow));

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
