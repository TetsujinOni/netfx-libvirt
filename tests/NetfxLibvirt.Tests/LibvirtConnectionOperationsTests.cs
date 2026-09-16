using NetfxLibvirt.Generated.Remote;
using NetfxLibvirt.Rpc;
using NetfxLibvirt.Tests.Rpc;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Tests;

/// <summary>Hermetic tests for the post-handshake operations
/// (<c>docs/plan.md</c> stories 7–10) — same <see cref="FakeDuplexStream"/>
/// approach as <c>LibvirtConnectionTests</c>/<c>VirNetRpcClientTests</c>.
/// Real-libvirtd proof lives in <c>Integration/LibvirtdIntegrationTests</c>.</summary>
public class LibvirtConnectionOperationsTests
{
    private static uint _nextSerial;

    private static async Task<(FakeDuplexStream Stream, LibvirtConnection Connection)> OpenAsync()
    {
        _nextSerial = 1;
        var stream = new FakeDuplexStream();
        QueueOk(stream, EncodeAuthList(RemoteAuthType.RemoteAuthNone));
        QueueOk(stream, []); // CONNECT_OPEN has no ret struct
        var connection = await LibvirtConnection.OpenAsync(stream, "test:///default", TestContext.Current.CancellationToken);
        return (stream, connection);
    }

    private static void QueueOk(FakeDuplexStream stream, byte[] payload) => stream.QueueReply(_nextSerial++, VirNetMessageStatus.Ok, payload);

    private static byte[] EncodeAuthList(params RemoteAuthType[] types) => Encode(new RemoteAuthListRet { Types = [.. types] });

    private static byte[] Encode(RemoteConnectListAllDomainsRet dto) => EncodeCore(dto.Encode);

    private static byte[] Encode(RemoteDomainGetStateRet dto) => EncodeCore(dto.Encode);

    private static byte[] Encode(RemoteDomainLookupByNameRet dto) => EncodeCore(dto.Encode);

    private static byte[] Encode(RemoteDomainGetXmlDescRet dto) => EncodeCore(dto.Encode);

    private static byte[] Encode(RemoteAuthListRet dto) => EncodeCore(dto.Encode);

    private static byte[] Encode(RemoteError dto) => EncodeCore(dto.Encode);

    private static byte[] EncodeCore(Action<XdrWriter> encode)
    {
        var writer = new XdrWriter();
        encode(writer);
        return writer.ToArray();
    }

    private static RemoteNonnullDomain SampleDomain(string name, int id) => new() { Name = name, Uuid = new byte[16], Id = id };

    [Fact]
    public async Task ListDomainsAsync_CombinesListAllDomainsAndPerDomainState()
    {
        var (stream, connection) = await OpenAsync();
        await using var _ = connection;

        QueueOk(stream, Encode(new RemoteConnectListAllDomainsRet
        {
            Domains = [SampleDomain("vm1", 1), SampleDomain("vm2", 2)],
            Ret = 2,
        }));
        QueueOk(stream, Encode(new RemoteDomainGetStateRet { State = 1, Reason = 1 })); // running
        QueueOk(stream, Encode(new RemoteDomainGetStateRet { State = 5, Reason = 1 })); // shut off

        var domains = await connection.ListDomainsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, domains.Count);
        Assert.Equal("vm1", domains[0].Name);
        Assert.Equal(DomainState.Running, domains[0].State);
        Assert.Equal("vm2", domains[1].Name);
        Assert.Equal(DomainState.ShutOff, domains[1].State);

        var sent = stream.ReadAllSentFrames();
        Assert.Equal((int)RemoteProcedure.RemoteProcConnectListAllDomains, sent[2].Header.Proc);
        Assert.Equal((int)RemoteProcedure.RemoteProcDomainGetState, sent[3].Header.Proc);
        Assert.Equal((int)RemoteProcedure.RemoteProcDomainGetState, sent[4].Header.Proc);
    }

    [Fact]
    public async Task ListDomainsAsync_UnrecognizedStateCode_MapsToUnknown()
    {
        var (stream, connection) = await OpenAsync();
        await using var _ = connection;

        QueueOk(stream, Encode(new RemoteConnectListAllDomainsRet { Domains = [SampleDomain("vm1", 1)], Ret = 1 }));
        QueueOk(stream, Encode(new RemoteDomainGetStateRet { State = 999, Reason = 0 }));

        var domains = await connection.ListDomainsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DomainState.Unknown, domains[0].State);
    }

    [Fact]
    public async Task StartDomainAsync_LooksUpThenCreates()
    {
        var (stream, connection) = await OpenAsync();
        await using var _ = connection;

        QueueOk(stream, Encode(new RemoteDomainLookupByNameRet { Dom = SampleDomain("vm1", 1) }));
        QueueOk(stream, []); // DOMAIN_CREATE has no ret struct

        await connection.StartDomainAsync("vm1", TestContext.Current.CancellationToken);

        var sent = stream.ReadAllSentFrames();
        Assert.Equal((int)RemoteProcedure.RemoteProcDomainLookupByName, sent[2].Header.Proc);
        Assert.Equal((int)RemoteProcedure.RemoteProcDomainCreate, sent[3].Header.Proc);
        Assert.Equal("vm1", RemoteDomainLookupByNameArgs.Decode(new XdrReader(sent[2].Payload)).Name);
    }

    [Fact]
    public async Task ShutdownDomainAsync_LooksUpThenShutsDown()
    {
        var (stream, connection) = await OpenAsync();
        await using var _ = connection;

        QueueOk(stream, Encode(new RemoteDomainLookupByNameRet { Dom = SampleDomain("vm1", 1) }));
        QueueOk(stream, []);

        await connection.ShutdownDomainAsync("vm1", TestContext.Current.CancellationToken);

        var sent = stream.ReadAllSentFrames();
        Assert.Equal((int)RemoteProcedure.RemoteProcDomainShutdown, sent[3].Header.Proc);
    }

    [Fact]
    public async Task DestroyDomainAsync_LooksUpThenDestroys()
    {
        var (stream, connection) = await OpenAsync();
        await using var _ = connection;

        QueueOk(stream, Encode(new RemoteDomainLookupByNameRet { Dom = SampleDomain("vm1", 1) }));
        QueueOk(stream, []);

        await connection.DestroyDomainAsync("vm1", TestContext.Current.CancellationToken);

        var sent = stream.ReadAllSentFrames();
        Assert.Equal((int)RemoteProcedure.RemoteProcDomainDestroy, sent[3].Header.Proc);
    }

    [Fact]
    public async Task StartDomainAsync_DomainNotFound_ThrowsLibvirtRpcException()
    {
        var (stream, connection) = await OpenAsync();
        await using var _ = connection;

        var error = new RemoteError
        {
            Code = 42, Domain = 10, Message = "Domain not found", Level = 2,
            Dom = null, Str1 = null, Str2 = null, Str3 = null, Int1 = 0, Int2 = 0, Net = null,
        };
        stream.QueueReply(_nextSerial++, VirNetMessageStatus.Error, Encode(error));

        await Assert.ThrowsAsync<LibvirtRpcException>(
            () => connection.StartDomainAsync("does-not-exist", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDomainXmlAsync_LooksUpThenReturnsXml()
    {
        var (stream, connection) = await OpenAsync();
        await using var _ = connection;

        QueueOk(stream, Encode(new RemoteDomainLookupByNameRet { Dom = SampleDomain("vm1", 1) }));
        QueueOk(stream, Encode(new RemoteDomainGetXmlDescRet { Xml = "<domain type='test'/>" }));

        var xml = await connection.GetDomainXmlAsync("vm1", TestContext.Current.CancellationToken);

        Assert.Equal("<domain type='test'/>", xml);
        var sent = stream.ReadAllSentFrames();
        Assert.Equal((int)RemoteProcedure.RemoteProcDomainGetXmlDesc, sent[3].Header.Proc);
    }

    [Fact]
    public async Task DisconnectAsync_SendsConnectCloseAndIsIdempotent()
    {
        var (stream, connection) = await OpenAsync();
        QueueOk(stream, []); // CONNECT_CLOSE has no ret struct

        await connection.DisconnectAsync(TestContext.Current.CancellationToken);
        await connection.DisconnectAsync(TestContext.Current.CancellationToken); // must not send a second CONNECT_CLOSE

        var sent = stream.ReadAllSentFrames();
        Assert.Equal(3, sent.Count); // AUTH_LIST, CONNECT_OPEN, CONNECT_CLOSE
        Assert.Equal((int)RemoteProcedure.RemoteProcConnectClose, sent[2].Header.Proc);
    }

    [Fact]
    public async Task DisposeAsync_AttemptsConnectCloseButNeverThrows()
    {
        var (stream, connection) = await OpenAsync();

        var error = new RemoteError
        {
            Code = 1, Domain = 0, Message = "internal error", Level = 2,
            Dom = null, Str1 = null, Str2 = null, Str3 = null, Int1 = 0, Int2 = 0, Net = null,
        };
        stream.QueueReply(_nextSerial++, VirNetMessageStatus.Error, Encode(error));

        await connection.DisposeAsync(); // must not throw even though CONNECT_CLOSE fails
    }
}
