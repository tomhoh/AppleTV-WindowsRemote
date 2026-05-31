// SPDX-License-Identifier: MIT

using System.Text;
using AppleTVProtocol;

namespace AppleTVProtocol.Tests;

public class CompanionFrameTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void EncodeHeader_RendersBigEndian24BitLength()
    {
        Assert.Equal("07000000", Hex(CompanionFrame.EncodeHeader(CompanionFrameType.U_OPACK, 0)));
        Assert.Equal("07000001", Hex(CompanionFrame.EncodeHeader(CompanionFrameType.U_OPACK, 1)));
        Assert.Equal("0700ffff", Hex(CompanionFrame.EncodeHeader(CompanionFrameType.U_OPACK, 0xFFFF)));
        Assert.Equal("07ffffff", Hex(CompanionFrame.EncodeHeader(CompanionFrameType.U_OPACK, 0xFF_FFFF)));
        Assert.Equal("03000000", Hex(CompanionFrame.EncodeHeader(CompanionFrameType.PS_Start, 0)));
    }

    [Fact]
    public void EncodeHeader_RejectsOversizedLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CompanionFrame.EncodeHeader(CompanionFrameType.U_OPACK, 0x1_000_000));
    }

    [Fact]
    public void EncodeFrame_ConcatHeaderAndPayload()
    {
        var payload = Encoding.ASCII.GetBytes("HELLO");
        var bytes   = CompanionFrame.EncodeFrame(CompanionFrameType.U_OPACK, payload);
        Assert.Equal("0700000548454c4c4f", Hex(bytes));
    }

    [Fact]
    public void TryDecode_ReturnsNullWhenHeaderIncomplete()
    {
        Assert.Null(CompanionFrame.TryDecode(new byte[] { 0x07, 0x00, 0x00 }, out var consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryDecode_ReturnsNullWhenPayloadIncomplete()
    {
        // Says length 10 but only 5 bytes follow
        var buffer = new byte[] { 0x07, 0x00, 0x00, 0x0A, 1, 2, 3, 4, 5 };
        Assert.Null(CompanionFrame.TryDecode(buffer, out var consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryDecode_ExactFrame()
    {
        var buffer = H("0700000548454c4c4f");
        var frame  = CompanionFrame.TryDecode(buffer, out var consumed);
        Assert.NotNull(frame);
        Assert.Equal(CompanionFrameType.U_OPACK, frame!.Value.Type);
        Assert.Equal("HELLO", Encoding.ASCII.GetString(frame.Value.Payload));
        Assert.Equal(buffer.Length, consumed);
    }

    [Fact]
    public void TryDecode_StopsAtFrameBoundary()
    {
        // Two frames concatenated; decoder should consume only the first.
        var first  = H("0700000548454c4c4f");      // U_OPACK "HELLO"
        var second = H("01000000");                // NoOp with empty payload
        var both   = first.Concat(second).ToArray();

        var f1 = CompanionFrame.TryDecode(both, out var consumed1);
        Assert.NotNull(f1);
        Assert.Equal(first.Length, consumed1);

        var f2 = CompanionFrame.TryDecode(both.AsSpan(consumed1), out var consumed2);
        Assert.NotNull(f2);
        Assert.Equal(CompanionFrameType.NoOp, f2!.Value.Type);
        Assert.Empty(f2.Value.Payload);
        Assert.Equal(second.Length, consumed2);
    }

    [Fact]
    public void EncodeDecode_RoundTripsZeroLengthPayload()
    {
        var bytes = CompanionFrame.EncodeFrame(CompanionFrameType.NoOp, Array.Empty<byte>());
        Assert.Equal("01000000", Hex(bytes));
        var frame = CompanionFrame.TryDecode(bytes, out _);
        Assert.Equal(CompanionFrameType.NoOp, frame!.Value.Type);
        Assert.Empty(frame.Value.Payload);
    }

    [Fact]
    public void EncodeDecode_RoundTripsLargePayload()
    {
        var payload = new byte[10_000];
        new Random(0).NextBytes(payload);
        var wire = CompanionFrame.EncodeFrame(CompanionFrameType.E_OPACK, payload);
        var frame = CompanionFrame.TryDecode(wire, out var consumed);
        Assert.Equal(wire.Length, consumed);
        Assert.Equal(CompanionFrameType.E_OPACK, frame!.Value.Type);
        Assert.Equal(Hex(payload), Hex(frame.Value.Payload));
    }
}
