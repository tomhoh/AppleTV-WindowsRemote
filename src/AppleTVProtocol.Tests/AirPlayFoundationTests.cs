// SPDX-License-Identifier: MIT
//
// Unit tests for the AirPlay-MRP foundation codecs that don't need a TV:
// the hand-rolled protobuf reader/writer, the now-playing decoder, the
// binary-plist round-trip, and the HAP session frame codec.

using AppleTVProtocol.AirPlay;

namespace AppleTVProtocol.Tests;

public class AirPlayFoundationTests
{
    // ─── Protobuf ───

    [Fact]
    public void Protobuf_RoundTripsScalarFields()
    {
        var w = new ProtobufWriter();
        w.WriteVarintField(1, 4);
        w.WriteStringField(2, "hello");
        w.WriteBoolField(3, true);
        var bytes = w.ToArray();

        Assert.Equal(4UL, Protobuf.VarintField(bytes, 1));
        Assert.Equal("hello", Protobuf.StringField(bytes, 2));
        Assert.Equal(1UL, Protobuf.VarintField(bytes, 3));   // bool encodes as 1
        Assert.Null(Protobuf.VarintField(bytes, 9));         // absent field
    }

    [Fact]
    public void Protobuf_DoubleField_ReadsBackExactly()
    {
        var w = new ProtobufWriter();
        // Manually emit a wire-type-1 (64-bit) field number 14.
        var inner = new System.IO.MemoryStream();
        // tag = (14 << 3) | 1 = 113 = 0x71
        inner.WriteByte(0x71);
        var d = BitConverter.GetBytes(123.5);   // little-endian on all supported platforms
        inner.Write(d, 0, 8);
        var bytes = inner.ToArray();

        Assert.Equal(123.5, Protobuf.DoubleField(bytes, 14));
    }

    [Fact]
    public void MrpNowPlaying_DecodesSetStateMessage()
    {
        // Build ContentItemMetadata { title=1, album=6, artist=7 }
        var meta = new ProtobufWriter();
        meta.WriteStringField(1, "Song Title");
        meta.WriteStringField(6, "The Album");
        meta.WriteStringField(7, "The Artist");

        // ContentItem { metadata=2 }
        var contentItem = new ProtobufWriter();
        contentItem.WriteMessageField(2, meta);

        // PlaybackQueue { contentItems=2 }
        var pq = new ProtobufWriter();
        pq.WriteMessageField(2, contentItem);

        // SetStateMessage { playbackQueue=3, playbackState=6 }
        var ssm = new ProtobufWriter();
        ssm.WriteMessageField(3, pq);
        ssm.WriteVarintField(6, 1);   // 1 = playing

        // ProtocolMessage { type=1 (=4 SET_STATE), ext field 9 = SetStateMessage }
        var outer = new ProtobufWriter();
        outer.WriteVarintField(1, 4);
        outer.WriteMessageField(9, ssm);

        var update = MrpNowPlayingDecoder.Decode(outer.ToArray());
        Assert.NotNull(update);
        Assert.Equal("Song Title", update!.Title);
        Assert.Equal("The Album",  update.Album);
        Assert.Equal("The Artist", update.Artist);
        Assert.Equal(1, update.PlaybackState);
        Assert.True(update.IsPlaying);
    }

    [Fact]
    public void MrpNowPlaying_IgnoresUnrelatedMessageTypes()
    {
        var outer = new ProtobufWriter();
        outer.WriteVarintField(1, 15);   // DEVICE_INFO — not now-playing
        Assert.Null(MrpNowPlayingDecoder.Decode(outer.ToArray()));
    }

    // ─── Binary plist ───

    [Fact]
    public void BinaryPlist_RoundTripsDict()
    {
        var w = new BinaryPlistWriter();
        var k1 = w.AddString("controlType");
        var v1 = w.AddInt(2);
        var k2 = w.AddString("wantsDedicatedSocket");
        var v2 = w.AddBool(true);
        var k3 = w.AddString("name");
        var v3 = w.AddString("Living Room");
        var root = w.AddDict(new[] { (k1, v1), (k2, v2), (k3, v3) });
        var bytes = w.Build(root);

        var parsed = BinaryPlistReader.Parse(bytes) as Dictionary<string, object?>;
        Assert.NotNull(parsed);
        Assert.Equal(2L, parsed!["controlType"]);
        Assert.Equal(true, parsed["wantsDedicatedSocket"]);
        Assert.Equal("Living Room", parsed["name"]);
    }

    [Fact]
    public void BinaryPlist_RoundTripsNestedDataEnvelope()
    {
        // The {params:{data:<bytes>}} DataStream envelope shape.
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var w = new BinaryPlistWriter();
        var dataRef  = w.AddData(payload);
        var dataKey  = w.AddString("data");
        var inner    = w.AddDict(new[] { (dataKey, dataRef) });
        var paramKey = w.AddString("params");
        var root     = w.AddDict(new[] { (paramKey, inner) });
        var bytes = w.Build(root);

        var parsed = BinaryPlistReader.Parse(bytes) as Dictionary<string, object?>;
        var inn = parsed!["params"] as Dictionary<string, object?>;
        Assert.Equal(payload, inn!["data"] as byte[]);
    }

    // ─── HAP session frame codec ───

    [Fact]
    public void HapSession_EncryptDecryptRoundTrips()
    {
        var keyA = new byte[32]; var keyB = new byte[32];
        for (int i = 0; i < 32; i++) { keyA[i] = (byte)i; keyB[i] = (byte)(i + 100); }

        // Two endpoints: client writes with keyA / reads with keyB; the
        // "server" mirror reads with keyA / writes with keyB.
        var client = new HapSession(writeKey: keyA, readKey: keyB);
        var server = new HapSession(writeKey: keyB, readKey: keyA);

        var msg = System.Text.Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        var wire = client.Encrypt(msg);
        var got  = server.Feed(wire);
        Assert.Equal(msg, got);
    }

    [Fact]
    public void HapSession_SplitsLargePlaintextAcrossFrames()
    {
        var key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)(i * 3);
        var client = new HapSession(writeKey: key, readKey: key);
        var server = new HapSession(writeKey: key, readKey: key);

        var big = new byte[2500];   // > 2× MaxFramePlaintext
        new Random(42).NextBytes(big);
        var wire = client.Encrypt(big);
        var got  = server.Feed(wire);
        Assert.Equal(big, got);
    }

    [Fact]
    public void HapSession_FeedHandlesPartialFrames()
    {
        var key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)(255 - i);
        var client = new HapSession(writeKey: key, readKey: key);
        var server = new HapSession(writeKey: key, readKey: key);

        var msg = System.Text.Encoding.UTF8.GetBytes("partial-frame test payload here");
        var wire = client.Encrypt(msg);

        // Feed one byte at a time — server should buffer until the frame completes.
        var acc = new List<byte>();
        for (int i = 0; i < wire.Length; i++)
            acc.AddRange(server.Feed(new[] { wire[i] }));
        Assert.Equal(msg, acc.ToArray());
    }
}
