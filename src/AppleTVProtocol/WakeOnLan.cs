// SPDX-License-Identifier: MIT
//
// Wake-on-LAN helper for sleeping Apple TVs. The companion HID Wake
// keycode only works over a live socket, which the TV tears down ~1 s
// after accepting Sleep — so Wake-after-Sleep needs a magic packet
// instead. ARP cache (populated during the prior connection) gives us
// the TV's MAC without the user ever supplying it, matching the Mac
// sibling's MACStore-plus-WoL approach.

using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AppleTVProtocol;

public static class WakeOnLan
{
    /// <summary>
    /// Resolve <paramref name="host"/> to its MAC via the Windows ARP
    /// cache and send a WoL magic packet to port 9 on the subnet
    /// broadcast. Returns true if a packet was dispatched. Silent on
    /// failure (unreachable host, MAC not cached, firewall) — caller
    /// decides whether to surface anything to the user.
    /// </summary>
    public static async Task<bool> SendAsync(string host)
    {
        try
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                // DNS name — resolve. Blocks briefly, but WoL is a
                // user-initiated action, so a short DNS round-trip is
                // acceptable.
                var entry = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                ip = Array.Find(entry, a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip is null) return false;
            }
            var mac = GetMacForIpv4(ip);
            if (mac is null) return false;
            await SendMagicPacketAsync(mac).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? GetMacForIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var bytes = ip.GetAddressBytes();
        uint destIp = BitConverter.ToUInt32(bytes, 0);
        var mac = new byte[6];
        uint macLen = (uint)mac.Length;
        // SendARP issues an ARP probe if the entry isn't cached, so a
        // reachable host populates the cache even on first call. For
        // a sleeping Apple TV we rely on the entry remaining from the
        // active-session days — Windows typically holds ARP entries
        // for ~15 min of inactivity, which covers casual sleep/wake.
        int ret = SendARP(destIp, 0, mac, ref macLen);
        if (ret != 0 || macLen != 6) return null;
        return mac;
    }

    private static async Task SendMagicPacketAsync(byte[] mac)
    {
        // Standard magic packet: 6 bytes of 0xFF + MAC repeated 16 times = 102 bytes.
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 1; i <= 16; i++) Buffer.BlockCopy(mac, 0, packet, i * 6, 6);
        using var udp = new UdpClient { EnableBroadcast = true };
        // Port 9 is the conventional WoL port; port 7 is also common.
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9))
                 .ConfigureAwait(false);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physicalAddrLen);
}
