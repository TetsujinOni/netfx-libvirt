using NetfxLibvirt.Rpc;

namespace NetfxLibvirt.Tests.Rpc;

public class VirNetMessageFramingTests
{
    private static readonly VirNetMessageHeader SampleHeader = new(
        Prog: 0x20008086,
        Vers: 1,
        Proc: 66,
        Type: VirNetMessageType.Call,
        Serial: 1,
        Status: VirNetMessageStatus.Ok);

    [Fact]
    public void EncodeFrame_LengthPrefixCountsItselfHeaderAndPayload()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        var frame = VirNetMessageFraming.EncodeFrame(SampleHeader, payload);

        // 4 (length field) + 24 (header) + 5 (payload) = 33, matching go-libvirt's
        // `packet{ Len uint32; Header }` semantics (Len includes itself).
        var expectedTotalLength = 4 + 24 + 5;
        Assert.Equal(expectedTotalLength, frame.Length);

        var encodedLength = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
        Assert.Equal(expectedTotalLength, encodedLength);
    }

    [Fact]
    public void EncodeFrame_EmptyPayload_ProducesLengthPrefixPlusHeaderOnly()
    {
        var frame = VirNetMessageFraming.EncodeFrame(SampleHeader, ReadOnlySpan<byte>.Empty);
        Assert.Equal(4 + 24, frame.Length);
    }

    [Fact]
    public void EncodeFrame_PayloadExceedingMax_Throws()
    {
        var oversized = new byte[VirNetMessageFraming.MaxPayloadSize + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => VirNetMessageFraming.EncodeFrame(SampleHeader, oversized));
    }

    [Fact]
    public async Task WriteThenReadFrameAsync_RoundTrips()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];

        using var stream = new MemoryStream();
        await VirNetMessageFraming.WriteFrameAsync(stream, SampleHeader, payload, TestContext.Current.CancellationToken);
        stream.Position = 0;

        var message = await VirNetMessageFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(SampleHeader, message.Header);
        Assert.Equal(payload, message.Payload);
    }

    [Fact]
    public async Task ReadFrameAsync_ReadsMultipleFramesSequentiallyFromOneStream()
    {
        var headerA = SampleHeader with { Serial = 1 };
        var headerB = SampleHeader with { Serial = 2, Type = VirNetMessageType.Reply };

        using var stream = new MemoryStream();
        await VirNetMessageFraming.WriteFrameAsync(stream, headerA, new byte[] { 1 }, TestContext.Current.CancellationToken);
        await VirNetMessageFraming.WriteFrameAsync(stream, headerB, new byte[] { 2, 3 }, TestContext.Current.CancellationToken);
        stream.Position = 0;

        var first = await VirNetMessageFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken);
        var second = await VirNetMessageFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(headerA, first.Header);
        Assert.Equal(new byte[] { 1 }, first.Payload);
        Assert.Equal(headerB, second.Header);
        Assert.Equal(new byte[] { 2, 3 }, second.Payload);
    }

    [Fact]
    public async Task ReadFrameAsync_TruncatedStream_Throws()
    {
        using var stream = new MemoryStream();
        await VirNetMessageFraming.WriteFrameAsync(stream, SampleHeader, new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        stream.SetLength(stream.Length - 1); // chop off the last payload byte
        stream.Position = 0;

        await Assert.ThrowsAsync<EndOfStreamException>(() => VirNetMessageFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadFrameAsync_LengthPrefixSmallerThanHeader_Throws()
    {
        using var stream = new MemoryStream();
        // A length prefix that claims less than the mandatory 4+24 bytes is structurally invalid.
        await stream.WriteAsync(new byte[] { 0x00, 0x00, 0x00, 0x05 }, TestContext.Current.CancellationToken);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => VirNetMessageFraming.ReadFrameAsync(stream, TestContext.Current.CancellationToken));
    }
}
