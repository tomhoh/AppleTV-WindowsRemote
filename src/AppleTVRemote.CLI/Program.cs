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

        Commands:
          discover    Browse the LAN for Apple TVs via mDNS and print what we find.
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
