using AppleTVProtocol;
using Xunit;

namespace AppleTVProtocol.Tests;

public class Tlv8Tests
{
    // ── Helpers ──

    private static byte[] Hex(string spaced) =>
        Convert.FromHexString(spaced.Replace(" ", "").Replace("\n", ""));

    private static byte[] Repeat(byte b, int n)
    {
        var a = new byte[n];
        Array.Fill(a, b);
        return a;
    }

    // ── Read ──

    [Fact]
    public void Read_EmptyInput_ReturnsEmpty()
    {
        var result = Tlv8.Read(Array.Empty<byte>());
        Assert.Empty(result);
    }

    [Fact]
    public void Read_SingleByteValue_ReadsTagAndValue()
    {
        // HAP M1: SeqNo=0x01 → tag 0x06, length 0x01, value 0x01
        var bytes = Hex("06 01 01");
        var result = Tlv8.Read(bytes);
        Assert.Single(result);
        Assert.Equal(new byte[] { 0x01 }, result[0x06]);
    }

    [Fact]
    public void Read_MultipleTags_PreservesOrder()
    {
        // SeqNo=M3 + Method=PairSetup + Salt=8 random bytes
        var salt = Hex("CA FE BA BE DE AD BE EF");
        var bytes = Hex("06 01 03 00 01 00") .Concat(Hex("02 08").Concat(salt)).ToArray();
        var result = Tlv8.Read(bytes);
        Assert.Equal(3, result.Count);
        // First-seen order: 0x06, 0x00, 0x02
        var keys = result.Keys.ToArray();
        Assert.Equal((byte)0x06, keys[0]);
        Assert.Equal((byte)0x00, keys[1]);
        Assert.Equal((byte)0x02, keys[2]);
        Assert.Equal(new byte[] { 0x03 }, result[0x06]);
        Assert.Equal(new byte[] { 0x00 }, result[0x00]);
        Assert.Equal(salt, result[0x02]);
    }

    [Fact]
    public void Read_TagSplitAcross255ByteChunks_Concatenates()
    {
        // A 300-byte value with tag 0x05 should arrive as one chunk of 255
        // plus a continuation chunk of 45, both tagged 0x05.
        var first  = Repeat(0xAA, 255);
        var second = Repeat(0xBB, 45);
        var bytes = new byte[] { 0x05, 255 }
            .Concat(first)
            .Concat(new byte[] { 0x05, 45 })
            .Concat(second)
            .ToArray();
        var result = Tlv8.Read(bytes);
        Assert.Single(result);
        Assert.Equal(300, result[0x05].Length);
        Assert.Equal(first.Concat(second).ToArray(), result[0x05]);
    }

    [Fact]
    public void Read_ZeroLengthEntry_PreservesEmpty()
    {
        var bytes = Hex("07 00");
        var result = Tlv8.Read(bytes);
        Assert.Single(result);
        Assert.Empty(result[0x07]);
    }

    [Fact]
    public void Read_TruncatedHeader_Throws()
    {
        var bytes = new byte[] { 0x06 };  // missing length byte
        Assert.Throws<ArgumentException>(() => Tlv8.Read(bytes));
    }

    [Fact]
    public void Read_TruncatedValue_Throws()
    {
        var bytes = new byte[] { 0x06, 0x05, 0xAA, 0xBB };  // declares 5, has 2
        Assert.Throws<ArgumentException>(() => Tlv8.Read(bytes));
    }

    // ── Write ──

    [Fact]
    public void Write_Empty_ReturnsEmpty()
    {
        var bytes = Tlv8.Write(new Dictionary<byte, byte[]>());
        Assert.Empty(bytes);
    }

    [Fact]
    public void Write_SingleByteValue_MatchesCanonical()
    {
        var bytes = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            [0x06] = new byte[] { 0x01 },
        });
        Assert.Equal(Hex("06 01 01"), bytes);
    }

    [Fact]
    public void Write_ZeroLengthValue_EmitsTagAndZeroLength()
    {
        // Divergence from pyatv (which drops empty values entirely):
        // we emit `tag 00` so round-trip preserves the entry.
        var bytes = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            [0x07] = Array.Empty<byte>(),
        });
        Assert.Equal(Hex("07 00"), bytes);
    }

    [Fact]
    public void Write_300ByteValue_SplitsInto255Plus45()
    {
        var value = Repeat(0xCC, 300);
        var bytes = Tlv8.Write(new Dictionary<byte, byte[]>
        {
            [0x05] = value,
        });
        // 2 (header) + 255 + 2 (header) + 45 = 304 bytes
        Assert.Equal(304, bytes.Length);
        Assert.Equal(0x05, bytes[0]);
        Assert.Equal(255,  bytes[1]);
        Assert.Equal(0x05, bytes[2 + 255]);
        Assert.Equal(45,   bytes[2 + 255 + 1]);
    }

    [Fact]
    public void Write_MultipleEntries_PreservesIterationOrder()
    {
        var entries = new (byte, byte[])[]
        {
            (0x06, new byte[] { 0x01 }),
            (0x00, new byte[] { 0x00 }),
            (0x02, Hex("CA FE BA BE")),
        };
        var bytes = Tlv8.Write(entries.Select(t => new KeyValuePair<byte, byte[]>(t.Item1, t.Item2)));
        Assert.Equal(Hex("06 01 01 00 01 00 02 04 CA FE BA BE"), bytes);
    }

    // ── Round-trips ──

    [Theory]
    [InlineData(0)]      // empty
    [InlineData(1)]      // single byte
    [InlineData(254)]    // just under chunk boundary
    [InlineData(255)]    // exactly one chunk
    [InlineData(256)]    // just over: split 255 + 1
    [InlineData(500)]    // split 255 + 245
    [InlineData(765)]    // split 255 + 255 + 255 exactly
    [InlineData(1024)]   // split 255 + 255 + 255 + 259-truncated... actually 255*4+4
    public void RoundTrip_ArbitrarySizes(int valueLength)
    {
        var original = new Dictionary<byte, byte[]>
        {
            [(byte)Tlv8Tag.EncryptedData] = Repeat(0x7E, valueLength),
        };
        var bytes = Tlv8.Write(original);
        var parsed = Tlv8.Read(bytes);
        Assert.Single(parsed);
        Assert.Equal(original[(byte)Tlv8Tag.EncryptedData], parsed[(byte)Tlv8Tag.EncryptedData]);
    }

    [Fact]
    public void RoundTrip_RealisticHapM1Request()
    {
        // Typical HAP pair-setup M1 from a controller:
        //   SeqNo  = M1 (0x01)
        //   Method = PairSetupWithAuth (0x01)
        var original = new Dictionary<byte, byte[]>
        {
            [(byte)Tlv8Tag.SeqNo]  = new[] { (byte)Tlv8State.M1 },
            [(byte)Tlv8Tag.Method] = new[] { (byte)Tlv8Method.PairSetupWithAuth },
        };
        var bytes = Tlv8.Write(original);
        var parsed = Tlv8.Read(bytes);
        Assert.Equal(2, parsed.Count);
        Assert.Equal((byte)Tlv8State.M1,                parsed[(byte)Tlv8Tag.SeqNo][0]);
        Assert.Equal((byte)Tlv8Method.PairSetupWithAuth, parsed[(byte)Tlv8Tag.Method][0]);
    }

    [Fact]
    public void RoundTrip_AllTagEnumsMapToExpectedBytes()
    {
        Assert.Equal(0x00, (byte)Tlv8Tag.Method);
        Assert.Equal(0x06, (byte)Tlv8Tag.SeqNo);
        Assert.Equal(0x13, (byte)Tlv8Tag.Flags);
        Assert.Equal(0x10, (byte)Tlv8Flag.TransientPairing);
        Assert.Equal(0x02, (byte)Tlv8Method.PairVerify);
        Assert.Equal(0x06, (byte)Tlv8State.M6);
        Assert.Equal(0x07, (byte)Tlv8ErrorCode.Busy);
    }
}
