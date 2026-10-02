// SPDX-License-Identifier: MIT
//
// Wake-on-LAN helper for sleeping Apple TVs. The companion HID Wake
// keycode doesn't reliably wake a TV once it's already processing
// Sleep (its companion service is winding down), so Wake needs a
// magic packet. We snapshot the TV's MAC via the Windows ARP cache
// right after each successful connect (while the entry is fresh) and
// keep it in-memory for the rest of the session — Windows will expire
// an ARP entry after ~15 min of inactivity, which is often shorter
// than the user's "pressed sleep, went away, came back" window.
// Matches the MACStore + WoL approach in the Mac sibling.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AppleTVProtocol;

public static class WakeOnLan
{
    // host → MAC, populated by CacheMacForHostAsync. Shared across the
    // process, so both the WinUI and WPF variants see the same cache
    // if they ever ran side-by-side (they don't in practice, but the
    // helper is still process-scoped).
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new();

    /// <summary>
    /// Snapshot the MAC for <paramref name="host"/> while it's still
    /// reachable (ARP entry fresh) and remember it for later Wake
    /// attempts after the TV has gone to sleep. Call this right after
    /// a successful companion session is established. No-op if the
    /// ARP lookup fails or the entry can't be resolved.
    /// </summary>
    public static async Task CacheMacForHostAsync(string host)
    {
        try
        {
            var ip = await ResolveIpv4Async(host).ConfigureAwait(false);
            if (ip is null) return;
            var mac = GetMacForIpv4(ip);
            if (mac is not null) _cache[host] = mac;
        }
        catch { /* best-effort — no MAC just means WoL will fall back to live ARP */ }
    }

    /// <summary>
    /// Send a WoL magic packet to the TV at <paramref name="host"/>.
    /// Uses the cached MAC if we snapshotted one during a prior
    /// session; otherwise probes the current ARP cache. Returns true
    /// if a packet was dispatched. Silent on failure (unreachable
    /// host, MAC unknown, firewall) — caller decides what to surface.
    /// </summary>
    public static async Task<bool> SendAsync(string host)
    {
        try
        {
            if (_cache.TryGetValue(host, out var cached))
            {
                await SendMagicPacketAsync(cached).ConfigureAwait(false);
                return true;
            }
            var ip = await ResolveIpv4Async(host).ConfigureAwait(false);
            if (ip is null) return false;
            var mac = GetMacForIpv4(ip);
            if (mac is null) return false;
            _cache[host] = mac;
            await SendMagicPacketAsync(mac).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<IPAddress?> ResolveIpv4Async(string host)
    {
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            return ip;
        var entry = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        return Array.Find(entry, a => a.AddressFamily == AddressFamily.InterNetwork);
    }

    private static byte[]? GetMacForIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var bytes = ip.GetAddressBytes();
        uint destIp = BitConverter.ToUInt32(bytes, 0);
        var mac = new byte[6];
        uint macLen = (uint)mac.Length;
        int ret = SendARP(destIp, 0, mac, ref macLen);
        if (ret != 0 || macLen != 6) return null;
        // SendARP returns zeroed MAC for some unreachable hosts.
        bool allZero = true;
        for (int i = 0; i < 6; i++) if (mac[i] != 0) { allZero = false; break; }
        return allZero ? null : mac;
    }

    private static async Task SendMagicPacketAsync(byte[] mac)
    {
        // Standard magic packet: 6 bytes of 0xFF + MAC repeated 16 times = 102 bytes.
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 1; i <= 16; i++) Buffer.BlockCopy(mac, 0, packet, i * 6, 6);
        using var udp = new UdpClient { EnableBroadcast = true };
        // Port 9 is the conventional WoL port; port 7 is also common —
        // apps typically send to one, routers forward both.
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9))
                 .ConfigureAwait(false);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physicalAddrLen);
}
