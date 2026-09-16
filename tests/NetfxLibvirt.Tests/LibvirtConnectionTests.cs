using NetfxLibvirt.Generated.Remote;
using NetfxLibvirt.Rpc;
using NetfxLibvirt.Tests.Rpc;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Tests;

public class LibvirtConnectionTests
{
    [Fact]
    public async Task OpenAsync_AuthNoneOffered_SendsAuthListThenConnectOpen_Succeeds()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Ok, EncodeAuthList(RemoteAuthType.RemoteAuthNone));
        stream.QueueReply(serial: 2, VirNetMessageStatus.Ok, payload: []); // CONNECT_OPEN has no ret struct

        await using var connection = await LibvirtConnection.OpenAsync(stream, "qemu:///system", TestContext.Current.CancellationToken);

        var sent = stream.ReadAllSentFrames();
        Assert.Equal(2, sent.Count);
        Assert.Equal((int)RemoteProcedure.RemoteProcAuthList, sent[0].Header.Proc);
        Assert.Equal((int)RemoteProcedure.RemoteProcConnectOpen, sent[1].Header.Proc);
    }

    [Fact]
    public async Task OpenAsync_SendsUriInConnectOpenArgs()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Ok, EncodeAuthList(RemoteAuthType.RemoteAuthNone));
        stream.QueueReply(serial: 2, VirNetMessageStatus.Ok, payload: []);

        await using var connection = await LibvirtConnection.OpenAsync(stream, "qemu:///system", TestContext.Current.CancellationToken);

        var sent = stream.ReadAllSentFrames();
        var connectOpenArgs = RemoteConnectOpenArgs.Decode(new XdrReader(sent[1].Payload));
        Assert.Equal("qemu:///system", connectOpenArgs.Name);
    }

    [Fact]
    public async Task OpenAsync_OnlyUnsupportedAuthOffered_ThrowsNotSupported()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Ok, EncodeAuthList(RemoteAuthType.RemoteAuthPolkit));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => LibvirtConnection.OpenAsync(stream, "qemu:///system", TestContext.Current.CancellationToken));

        Assert.Contains("RemoteAuthPolkit", ex.Message);
    }

    private static byte[] EncodeAuthList(params RemoteAuthType[] types)
    {
        var ret = new RemoteAuthListRet { Types = [.. types] };
        var writer = new XdrWriter();
        ret.Encode(writer);
        return writer.ToArray();
    }
}
