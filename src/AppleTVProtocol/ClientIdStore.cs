// SPDX-License-Identifier: MIT
//
// Stable per-installation client identifier for HAP pair-setup.
//
// The Apple TV stores the clientPairingId we send in pair-setup M5 as
// a HAP controller UUID. Every pairing (initial OR re-pair) creates a
// controller entry on the TV that persists forever in Settings → Users
// and Accounts → Allowed Devices.
//
// Prior Windows builds generated a fresh Guid.NewGuid() for every
// pair-setup attempt, so every re-pair left a stale controller entry
// behind. Over time these accumulate on the TV; the TV then periodically
// polls each stored controller, none of the stale ones respond ("unknown
// device"), and eventually the accumulated stale state degrades or
// crashes the TV.
//
// This store mirrors the Swift sibling's approach (HAPPairing.swift's
// clientID in UserDefaults): generate the UUID ONCE per installation,
// persist it in %APPDATA%\AppleTV-WindowsRemote\client_id, and reuse
// it on every subsequent pair-setup. Re-pairs then update the SAME
// controller entry on the TV instead of creating a new one.

using System;
using System.IO;
using System.Text;

namespace AppleTVProtocol;

public static class ClientIdStore
{
    private const string FileName = "client_id";
    private static readonly object _lock = new();
    private static string? _cached;

    /// <summary>
    /// Load the stable client identifier for this installation,
    /// generating and persisting one on first use. Returned as an
    /// uppercase UUID string (e.g. "550E8400-E29B-41D4-A716-446655440000"),
    /// matching the format pair-setup M5 expects.
    /// </summary>
    public static string LoadOrCreate() => LoadOrCreate(DefaultDirectory());

    public static string LoadOrCreate(string directory)
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;

            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                var raw = File.ReadAllText(path).Trim();
                if (Guid.TryParse(raw, out var parsed))
                {
                    _cached = parsed.ToString().ToUpperInvariant();
                    return _cached;
                }
                // Corrupt file — fall through and rewrite with a fresh id
                // rather than failing. Pair-setup still works; this is
                // just the slow path.
            }

            var id = Guid.NewGuid().ToString().ToUpperInvariant();
            File.WriteAllText(path, id, Encoding.ASCII);
            _cached = id;
            return id;
        }
    }

    /// <summary>
    /// Return the client-id as the UTF-8/ASCII byte representation
    /// expected by <c>CompanionPairSetup.RunAsync(clientPairingId: …)</c>.
    /// </summary>
    public static byte[] LoadOrCreateBytes() =>
        Encoding.ASCII.GetBytes(LoadOrCreate());

    private static string DefaultDirectory()
    {
        // Sibling of the per-device credentials directory. Keep in the
        // user's roaming app-data so re-installs on the same machine
        // keep the identifier (one entry per Windows install on the TV,
        // not per re-install).
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(basePath))
            basePath = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? ".", ".config");
        return Path.Combine(basePath, "AppleTV-WindowsRemote");
    }
}
