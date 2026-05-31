// SPDX-License-Identifier: MIT
//
// Tests for the record-parsing path of DeviceDiscovery. We don't fire
// real mDNS queries from the test process (no actual TV on CI's network)
// but we do test the message → AppleTVDevice path that runs inside the
// ServiceInstanceDiscovered handler.

using System.Net;
using AppleTVProtocol;
using Makaretu.Dns;

namespace AppleTVProtocol.Tests;

public class DiscoveryTests
{
    private const string InstanceName = "Living Room._companion-link._tcp.local";
    private const string TargetHost   = "AppleTV.local";

    private static Message BuildFullyResolvedResponse(IPAddress address, ushort port = 49152)
    {
        var msg = new Message();
        msg.Answers.Add(new SRVRecord
        {
            Name   = InstanceName,
            Port   = port,
            Target = TargetHost,
        });
        msg.Answers.Add(new TXTRecord
        {
            Name    = InstanceName,
            Strings = new() { "deviceid=AA:BB:CC:DD:EE:FF", "model=AppleTV6,2", "rpHN=feedface", "ffl" },
        });
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            msg.Answers.Add(new ARecord { Name = TargetHost, Address = address });
        }
        else
        {
            msg.Answers.Add(new AAAARecord { Name = TargetHost, Address = address });
        }
        return msg;
    }

    [Fact]
    public void TryResolveFromMessage_BuildsDeviceFromBundledRecords()
    {
        var ip = IPAddress.Parse("192.168.1.42");
        var msg = BuildFullyResolvedResponse(ip, port: 7000);

        var dev = DeviceDiscovery.TryResolveFromMessage(InstanceName, msg);

        Assert.NotNull(dev);
        Assert.Equal("Living Room", dev!.Name);
        Assert.Equal(InstanceName, dev.FullName);
        Assert.Equal(ip, dev.Address);
        Assert.Equal(7000, dev.Port);
        Assert.Equal("AA:BB:CC:DD:EE:FF", dev.DeviceId);
        Assert.Equal("AppleTV6,2", dev.TxtRecords["model"]);
        Assert.Equal("feedface", dev.TxtRecords["rpHN"]);
        Assert.Equal(string.Empty, dev.TxtRecords["ffl"]);  // boolean-style TXT
    }

    [Fact]
    public void TryResolveFromMessage_AcceptsAdditionalRecordsSection()
    {
        // Bonjour responders sometimes split SRV/TXT into Answers and A/AAAA
        // into AdditionalRecords. The resolver should look at both.
        var ip = IPAddress.Parse("10.0.0.5");
        var msg = new Message();
        msg.Answers.Add(new SRVRecord { Name = InstanceName, Port = 49152, Target = TargetHost });
        msg.Answers.Add(new TXTRecord { Name = InstanceName, Strings = new() { "deviceid=DEAD" } });
        msg.AdditionalRecords.Add(new ARecord { Name = TargetHost, Address = ip });

        var dev = DeviceDiscovery.TryResolveFromMessage(InstanceName, msg);
        Assert.NotNull(dev);
        Assert.Equal(ip, dev!.Address);
    }

    [Fact]
    public void TryResolveFromMessage_ReturnsNullWhenSrvMissing()
    {
        var msg = new Message();
        msg.Answers.Add(new TXTRecord { Name = InstanceName, Strings = new() { "deviceid=X" } });
        msg.Answers.Add(new ARecord   { Name = TargetHost,   Address = IPAddress.Loopback });

        Assert.Null(DeviceDiscovery.TryResolveFromMessage(InstanceName, msg));
    }

    [Fact]
    public void TryResolveFromMessage_ReturnsNullWhenAddressMissing()
    {
        var msg = new Message();
        msg.Answers.Add(new SRVRecord { Name = InstanceName, Port = 7000, Target = TargetHost });
        msg.Answers.Add(new TXTRecord { Name = InstanceName, Strings = new() { "deviceid=X" } });

        Assert.Null(DeviceDiscovery.TryResolveFromMessage(InstanceName, msg));
    }

    [Fact]
    public void TryResolveFromMessage_HandlesMissingTxtRecord()
    {
        var msg = new Message();
        msg.Answers.Add(new SRVRecord { Name = InstanceName, Port = 7000, Target = TargetHost });
        msg.Answers.Add(new ARecord   { Name = TargetHost,   Address = IPAddress.Loopback });

        var dev = DeviceDiscovery.TryResolveFromMessage(InstanceName, msg);
        Assert.NotNull(dev);
        Assert.Empty(dev!.TxtRecords);
        Assert.Null(dev.DeviceId);
        Assert.Null(dev.Rpmd);
    }

    [Fact]
    public void TryResolveFromMessage_PreferIPv4WhenBothPresent()
    {
        var v4 = IPAddress.Parse("192.168.1.10");
        var v6 = IPAddress.Parse("fe80::1");
        var msg = new Message();
        msg.Answers.Add(new SRVRecord  { Name = InstanceName, Port = 7000, Target = TargetHost });
        msg.Answers.Add(new TXTRecord  { Name = InstanceName, Strings = new() { "model=AppleTV11,1" } });
        msg.Answers.Add(new ARecord    { Name = TargetHost, Address = v4 });
        msg.Answers.Add(new AAAARecord { Name = TargetHost, Address = v6 });

        var dev = DeviceDiscovery.TryResolveFromMessage(InstanceName, msg);
        Assert.NotNull(dev);
        Assert.Equal(v4, dev!.Address);
    }

    [Fact]
    public void TryResolveFromMessage_FallsBackToIPv6WhenNoIPv4()
    {
        var v6 = IPAddress.Parse("fe80::abcd");
        var msg = new Message();
        msg.Answers.Add(new SRVRecord  { Name = InstanceName, Port = 7000, Target = TargetHost });
        msg.Answers.Add(new TXTRecord  { Name = InstanceName, Strings = new() { "deviceid=X" } });
        msg.Answers.Add(new AAAARecord { Name = TargetHost, Address = v6 });

        var dev = DeviceDiscovery.TryResolveFromMessage(InstanceName, msg);
        Assert.NotNull(dev);
        Assert.Equal(v6, dev!.Address);
    }

    [Fact]
    public void ParseTxtRecord_ParsesKeyValuesAndBooleanEntries()
    {
        var txt = new TXTRecord
        {
            Name = InstanceName,
            Strings = new() { "a=1", "b=hello=world", "flag", "", "x=" },
        };
        var map = DeviceDiscovery.ParseTxtRecord(txt);
        Assert.Equal("1", map["a"]);
        Assert.Equal("hello=world", map["b"]);   // only first '=' is the separator
        Assert.Equal(string.Empty, map["flag"]); // boolean-style entry
        Assert.Equal(string.Empty, map["x"]);
        Assert.False(map.ContainsKey(string.Empty));   // empty string ignored
    }

    [Fact]
    public void ParseTxtRecord_ReturnsEmptyForNullInput()
    {
        var map = DeviceDiscovery.ParseTxtRecord(null);
        Assert.Empty(map);
    }
}
