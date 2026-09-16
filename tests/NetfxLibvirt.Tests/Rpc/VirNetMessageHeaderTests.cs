using NetfxLibvirt.Rpc;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Tests.Rpc;

public class VirNetMessageHeaderTests
{
    [Fact]
    public void Encode_ProducesExactlyTwentyFourBytes()
    {
        var header = new VirNetMessageHeader(
            Prog: 0x20008086,
            Vers: 1,
            Proc: 66,
            Type: VirNetMessageType.Call,
            Serial: 1,
            Status: VirNetMessageStatus.Ok);

        var writer = new XdrWriter();
        header.Encode(writer);

        Assert.Equal(VirNetMessageHeader.WireSize, writer.Length);
        Assert.Equal(24, writer.Length);
    }

    [Fact]
    public void Encode_MatchesRealRemoteProgramAuthListCallBytes()
    {
        // REMOTE_PROGRAM (0x20008086), REMOTE_PROTOCOL_VERSION (1),
        // REMOTE_PROC_AUTH_LIST (66), VIR_NET_CALL (0), serial 1, VIR_NET_OK (0) -
        // the exact first frame libvirtd expects on a freshly opened connection.
        var header = new VirNetMessageHeader(
            Prog: 0x20008086,
            Vers: 1,
            Proc: 66,
            Type: VirNetMessageType.Call,
            Serial: 1,
            Status: VirNetMessageStatus.Ok);

        var writer = new XdrWriter();
        header.Encode(writer);

        Assert.Equal(
        [
            0x20, 0x00, 0x80, 0x86, // prog
            0x00, 0x00, 0x00, 0x01, // vers
            0x00, 0x00, 0x00, 0x42, // proc (66)
            0x00, 0x00, 0x00, 0x00, // type (CALL)
            0x00, 0x00, 0x00, 0x01, // serial
            0x00, 0x00, 0x00, 0x00, // status (OK)
        ], writer.ToArray());
    }

    [Fact]
    public void Decode_RoundTripsThroughEncode()
    {
        var header = new VirNetMessageHeader(
            Prog: 0x20008086,
            Vers: 1,
            Proc: 273,
            Type: VirNetMessageType.Reply,
            Serial: 42,
            Status: VirNetMessageStatus.Error);

        var writer = new XdrWriter();
        header.Encode(writer);

        var reader = new XdrReader(writer.ToArray());
        var decoded = VirNetMessageHeader.Decode(reader);

        Assert.Equal(header, decoded);
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(VirNetMessageType.Call, 0)]
    [InlineData(VirNetMessageType.Reply, 1)]
    [InlineData(VirNetMessageType.Message, 2)]
    [InlineData(VirNetMessageType.Stream, 3)]
    [InlineData(VirNetMessageType.CallWithFds, 4)]
    [InlineData(VirNetMessageType.ReplyWithFds, 5)]
    [InlineData(VirNetMessageType.StreamHole, 6)]
    public void MessageType_MatchesUpstreamWireValue(VirNetMessageType type, int expected)
    {
        Assert.Equal(expected, (int)type);
    }

    [Theory]
    [InlineData(VirNetMessageStatus.Ok, 0)]
    [InlineData(VirNetMessageStatus.Error, 1)]
    [InlineData(VirNetMessageStatus.Continue, 2)]
    public void MessageStatus_MatchesUpstreamWireValue(VirNetMessageStatus status, int expected)
    {
        Assert.Equal(expected, (int)status);
    }
}
