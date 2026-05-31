using AppleTVProtocol;
using Xunit;

namespace AppleTVProtocol.Tests;

public class OpackTests
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

    private static T Unpack<T>(byte[] bytes)
    {
        var (value, _) = Opack.Unpack(bytes);
        return (T)value!;
    }

    // ── Pack: byte-exact against pyatv ──
    //
    // Every byte string in these tests was produced by running
    // `pyatv.support.opack.pack(value).hex()` and pinning the output. If
    // any of these fail, we've diverged from upstream.

    [Theory]
    [InlineData("null", null)]
    [InlineData("01", true)]
    [InlineData("02", false)]
    [InlineData("08", 0)]
    [InlineData("2F", 39)]            // last inline value (tag 0x2F = inline 0x27)
    [InlineData("3028", 40)]          // first sized-int value (tag 0x30 + 1 byte)
    [InlineData("30FF", 255)]
    [InlineData("310001", 256)]
    [InlineData("31FFFF", 65535)]
    [InlineData("3200000100", 65536)]
    public void Pack_Primitives_ByteExactWithPyatv(string expectedHex, object? value)
    {
        var expected = expectedHex == "null" ? new byte[] { 0x04 } : Hex(expectedHex);
        Assert.Equal(expected, Opack.Pack(value));
    }

    [Fact]
    public void Pack_Long2Pow40_MatchesPyatv()
    {
        // Outside InlineData because constants can't be long literals there.
        Assert.Equal(Hex("330000000000010000"), Opack.Pack(1L << 40));
    }

    [Fact]
    public void Pack_DoublePi_MatchesPyatv()
    {
        // pyatv encodes Python floats as IEEE 754 doubles (tag 0x36).
        Assert.Equal(Hex("366E861BF0F9210940"), Opack.Pack(3.14159));
    }

    [Fact]
    public void Pack_EmptyString_MatchesPyatv() =>
        Assert.Equal(Hex("40"), Opack.Pack(""));

    [Fact]
    public void Pack_ShortString_MatchesPyatv() =>
        Assert.Equal(Hex("426162"), Opack.Pack("ab"));

    [Fact]
    public void Pack_32CharString_MatchesPyatv()
    {
        // 0x60 = inline-max (32 chars). Tag = 0x40 + 32 = 0x60.
        var expected = new byte[33];
        expected[0] = 0x60;
        Array.Fill(expected, (byte)'a', 1, 32);
        Assert.Equal(expected, Opack.Pack(new string('a', 32)));
    }

    [Fact]
    public void Pack_33CharString_UsesExtendedForm()
    {
        // Tag 0x61 = string with 1-byte length prefix.
        var expected = new byte[35];
        expected[0] = 0x61;
        expected[1] = 33;
        Array.Fill(expected, (byte)'a', 2, 33);
        Assert.Equal(expected, Opack.Pack(new string('a', 33)));
    }

    [Fact]
    public void Pack_EmptyBytes_MatchesPyatv() =>
        Assert.Equal(Hex("70"), Opack.Pack(Array.Empty<byte>()));

    [Fact]
    public void Pack_ShortBytes_MatchesPyatv() =>
        Assert.Equal(Hex("74CAFEBABE"), Opack.Pack(Hex("CAFEBABE")));

    [Fact]
    public void Pack_33Bytes_UsesExtendedForm()
    {
        var expected = new byte[35];
        expected[0] = 0x91;
        expected[1] = 33;
        Array.Fill(expected, (byte)0xAA, 2, 33);
        Assert.Equal(expected, Opack.Pack(Repeat(0xAA, 33)));
    }

    [Fact]
    public void Pack_EmptyList_MatchesPyatv() =>
        Assert.Equal(Hex("D0"), Opack.Pack(new List<object>()));

    [Fact]
    public void Pack_SmallList_MatchesPyatv() =>
        Assert.Equal(Hex("D3090A0B"), Opack.Pack(new List<object> { 1, 2, 3 }));

    [Fact]
    public void Pack_15ElementList_UsesEndlessForm()
    {
        // List of 15 ints 0..14 → 0xDF + 15 inline bytes + terminator 0x03
        var items = Enumerable.Range(0, 15).Cast<object>().ToList();
        Assert.Equal(Hex("DF08090A0B0C0D0E0F1011121314151603"), Opack.Pack(items));
    }

    [Fact]
    public void Pack_EmptyDict_MatchesPyatv() =>
        Assert.Equal(Hex("E0"), Opack.Pack(new Dictionary<object, object?>()));

    [Fact]
    public void Pack_SmallDict_MatchesPyatv()
    {
        var d = new Dictionary<object, object?>
        {
            ["a"] = 1,
            ["b"] = 2,
        };
        Assert.Equal(Hex("E241610941620A"), Opack.Pack(d));
    }

    [Fact]
    public void Pack_NestedDictWithList_MatchesPyatv()
    {
        // {"k": [1, "two", b"\x03"]} → e1 41 6b d3 09 43 74 77 6f 71 03
        var d = new Dictionary<object, object?>
        {
            ["k"] = new List<object?> { 1, "two", new byte[] { 0x03 } },
        };
        Assert.Equal(Hex("E1416BD3094374776F7103"), Opack.Pack(d));
    }

    [Fact]
    public void Pack_DuplicateString_UsesObjectReference()
    {
        // ["foo", "foo"] → d2 43 66 6f 6f a0
        var list = new List<object?> { "foo", "foo" };
        Assert.Equal(Hex("D243666F6FA0"), Opack.Pack(list));
    }

    // ── Unpack ──

    [Theory]
    [InlineData("04",      null)]
    [InlineData("01",      true)]
    [InlineData("02",      false)]
    [InlineData("08",      (long)0)]
    [InlineData("2F",      (long)39)]
    [InlineData("40",      "")]
    [InlineData("426162",  "ab")]
    public void Unpack_Primitives(string hex, object? expected)
    {
        var (value, consumed) = Opack.Unpack(Hex(hex));
        Assert.Equal(expected, value);
        Assert.Equal(Hex(hex).Length, consumed);
    }

    [Fact]
    public void Unpack_SizedInt_ReportsWidth()
    {
        // 0x30 0xFF → SizedInt(255, 1)
        var (value, _) = Opack.Unpack(Hex("30FF"));
        var si = Assert.IsType<SizedInt>(value);
        Assert.Equal(255, si.Value);
        Assert.Equal(1, si.SizeBytes);
    }

    [Fact]
    public void Unpack_Double()
    {
        var (value, _) = Opack.Unpack(Hex("366E861BF0F9210940"));
        Assert.Equal(3.14159, Assert.IsType<double>(value), 6);
    }

    [Fact]
    public void Unpack_ShortBytes()
    {
        var (value, _) = Opack.Unpack(Hex("74CAFEBABE"));
        Assert.Equal(Hex("CAFEBABE"), Assert.IsType<byte[]>(value));
    }

    [Fact]
    public void Unpack_15ElementEndlessList()
    {
        var (value, _) = Opack.Unpack(Hex("DF08090A0B0C0D0E0F1011121314151603"));
        var list = Assert.IsType<List<object?>>(value);
        Assert.Equal(15, list.Count);
        Assert.Equal((long)0, list[0]);
        Assert.Equal((long)14, list[14]);
    }

    [Fact]
    public void Unpack_NestedDictWithList()
    {
        var (value, _) = Opack.Unpack(Hex("E1416BD3094374776F7103"));
        var dict = Assert.IsType<Dictionary<object, object?>>(value);
        var inner = Assert.IsType<List<object?>>(dict["k"]);
        Assert.Equal(3, inner.Count);
        Assert.Equal((long)1, inner[0]);
        Assert.Equal("two", inner[1]);
        Assert.Equal(new byte[] { 0x03 }, Assert.IsType<byte[]>(inner[2]));
    }

    [Fact]
    public void Unpack_ObjectReference_ResolvesToOriginal()
    {
        // d2 43 66 6f 6f a0 → ["foo", "foo"] (second is a reference)
        var (value, _) = Opack.Unpack(Hex("D243666F6FA0"));
        var list = Assert.IsType<List<object?>>(value);
        Assert.Equal(2, list.Count);
        Assert.Equal("foo", list[0]);
        Assert.Equal("foo", list[1]);
    }

    // ── Error paths ──

    [Fact]
    public void Unpack_EmptyInput_Throws() =>
        Assert.Throws<ArgumentException>(() => Opack.Unpack(Array.Empty<byte>()));

    [Fact]
    public void Unpack_UnknownTag_Throws() =>
        // 0x37 is in the int-range hole (0x30-0x33 valid, 0x34 reserved, 0x35-0x36 floats).
        Assert.Throws<ArgumentException>(() => Opack.Unpack(new byte[] { 0x37 }));

    [Fact]
    public void Unpack_TruncatedString_Throws() =>
        // declared length 5, only 2 bytes follow
        Assert.Throws<ArgumentException>(() => Opack.Unpack(new byte[] { 0x45, (byte)'a', (byte)'b' }));

    [Fact]
    public void Unpack_OutOfRangeReference_Throws() =>
        Assert.Throws<ArgumentException>(() => Opack.Unpack(new byte[] { 0xA5 }));

    [Fact]
    public void Unpack_UnterminatedEndlessList_Throws() =>
        // 0xDF (endless list) with no 0x03 terminator and no inner items.
        Assert.Throws<ArgumentException>(() => Opack.Unpack(new byte[] { 0xDF }));

    [Fact]
    public void Pack_UnsupportedType_Throws() =>
        Assert.Throws<ArgumentException>(() => Opack.Pack(new System.IO.MemoryStream()));

    // ── Round-trip ──

    [Fact]
    public void RoundTrip_InlineInts() =>
        AssertListRoundTrip(new object[] { 0, 1, 39 });

    [Fact]
    public void RoundTrip_SizedInts() =>
        AssertListRoundTrip(new object[] { 40, 256, 65536 });

    [Fact]
    public void RoundTrip_MixedTypes() =>
        AssertListRoundTrip(new object[] { true, "hi", new byte[] { 1, 2, 3 } });

    private static void AssertListRoundTrip(object[] elements)
    {
        var list = elements.ToList();
        var bytes = Opack.Pack(list);
        var roundTripped = Unpack<List<object?>>(bytes);
        Assert.Equal(list.Count, roundTripped.Count);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is byte[] expectedBytes)
                Assert.Equal(expectedBytes, Assert.IsType<byte[]>(roundTripped[i]));
            else if (list[i] is int intVal)
            {
                // Inline ints (0..39) round-trip as long; sized ints come back as SizedInt.
                long actual = roundTripped[i] switch
                {
                    long l    => l,
                    SizedInt s => s.Value,
                    _          => throw new Xunit.Sdk.XunitException(
                        $"unexpected type {roundTripped[i]?.GetType()}"),
                };
                Assert.Equal((long)intVal, actual);
            }
            else
                Assert.Equal(list[i], roundTripped[i]);
        }
    }

    [Fact]
    public void RoundTrip_DeeplyNestedDict()
    {
        var inner = new Dictionary<object, object?>
        {
            ["a"] = 1,
            ["b"] = new List<object?> { "x", new byte[] { 0xCA, 0xFE } },
            ["c"] = null,
            ["d"] = true,
            ["e"] = 3.14,
        };
        var outer = new Dictionary<object, object?>
        {
            ["msg"] = "hello",
            ["body"] = inner,
        };
        var bytes = Opack.Pack(outer);
        var parsed = Unpack<Dictionary<object, object?>>(bytes);
        Assert.Equal("hello", parsed["msg"]);
        var innerParsed = Assert.IsType<Dictionary<object, object?>>(parsed["body"]);
        Assert.Equal(5, innerParsed.Count);
        Assert.Equal(true, innerParsed["d"]);
        Assert.Null(innerParsed["c"]);
    }

    [Fact]
    public void RoundTrip_StringInterningProducesShortReferences()
    {
        // Three copies of the same string — only the first should be encoded
        // literally; the next two should be 1-byte references.
        var list = new List<object?> { "abc", "abc", "abc" };
        var bytes = Opack.Pack(list);
        // d3 + "abc"(4 bytes) + ref + ref = 7 bytes total
        Assert.Equal(7, bytes.Length);
        // Round-trip
        var parsed = Unpack<List<object?>>(bytes);
        Assert.Equal(new object?[] { "abc", "abc", "abc" }, parsed);
    }
}
