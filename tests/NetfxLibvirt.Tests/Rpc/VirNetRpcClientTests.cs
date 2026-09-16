using NetfxLibvirt.Generated.Remote;
using NetfxLibvirt.Rpc;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Tests.Rpc;

public class VirNetRpcClientTests
{
    [Fact]
    public async Task CallAsync_WritesCallHeaderWithProgramVersionAndIncrementingSerial()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Ok, payload: []);
        stream.QueueReply(serial: 2, VirNetMessageStatus.Ok, payload: []);
        var client = new VirNetRpcClient(stream);

        await client.CallAsync((int)RemoteProcedure.RemoteProcConnectOpen, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        await client.CallAsync((int)RemoteProcedure.RemoteProcConnectClose, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        var sent = stream.ReadAllSentFrames();
        Assert.Equal(2, sent.Count);
        Assert.Equal((uint)RemoteProtocolConstants.RemoteProgram, sent[0].Header.Prog);
        Assert.Equal((uint)RemoteProtocolConstants.RemoteProtocolVersion, sent[0].Header.Vers);
        Assert.Equal(VirNetMessageType.Call, sent[0].Header.Type);
        Assert.Equal(1u, sent[0].Header.Serial);
        Assert.Equal((int)RemoteProcedure.RemoteProcConnectOpen, sent[0].Header.Proc);
        Assert.Equal(2u, sent[1].Header.Serial);
        Assert.Equal((int)RemoteProcedure.RemoteProcConnectClose, sent[1].Header.Proc);
    }

    [Fact]
    public async Task CallAsync_OkReply_ReturnsRawPayloadForCallerToDecode()
    {
        var ret = new RemoteConnectGetCapabilitiesRet { Capabilities = "<capabilities/>" };
        var writer = new XdrWriter();
        ret.Encode(writer);

        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Ok, writer.ToArray());
        var client = new VirNetRpcClient(stream);

        var payload = await client.CallAsync((int)RemoteProcedure.RemoteProcConnectGetCapabilities, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        var decoded = RemoteConnectGetCapabilitiesRet.Decode(new XdrReader(payload));

        Assert.Equal("<capabilities/>", decoded.Capabilities);
    }

    [Fact]
    public async Task CallAsync_ErrorReply_ThrowsLibvirtRpcExceptionWithDecodedError()
    {
        var error = new RemoteError
        {
            Code = 42,
            Domain = 10,
            Message = "Domain not found",
            Level = 2,
            Dom = null,
            Str1 = null,
            Str2 = null,
            Str3 = null,
            Int1 = 0,
            Int2 = 0,
            Net = null,
        };
        var writer = new XdrWriter();
        error.Encode(writer);

        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Error, writer.ToArray());
        var client = new VirNetRpcClient(stream);

        var ex = await Assert.ThrowsAsync<LibvirtRpcException>(
            () => client.CallAsync((int)RemoteProcedure.RemoteProcDomainLookupByName, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(42, ex.Error.Code);
        Assert.Equal("Domain not found", ex.Message);
    }

    [Fact]
    public async Task CallAsync_MismatchedSerial_Throws()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 99, VirNetMessageStatus.Ok, payload: []);
        var client = new VirNetRpcClient(stream);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallAsync((int)RemoteProcedure.RemoteProcConnectOpen, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallAsync_ContinueReply_ThrowsNotSupported()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => client.CallAsync((int)RemoteProcedure.RemoteProcDomainOpenConsole, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));
    }
}
