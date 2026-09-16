using NetfxLibvirt.Generated.Remote;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Tests.Generated;

/// <summary>Round-trips a sample of the MVP procedure DTOs emitted by
/// NetfxLibvirt.ProtocolGen (see tools/NetfxLibvirt.ProtocolGen and
/// src/NetfxLibvirt/Generated/Remote) through real XdrWriter/XdrReader —
/// proof the generator's field-shape-to-wire-call mapping is actually
/// correct, not just that it compiles.</summary>
public class RemoteProcedureDtoTests
{
    [Fact]
    public void RemoteNonnullDomain_RoundTrips()
    {
        var domain = new RemoteNonnullDomain
        {
            Name = "test-vm",
            Uuid = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            Id = 42,
        };

        var writer = new XdrWriter();
        domain.Encode(writer);
        var decoded = RemoteNonnullDomain.Decode(new XdrReader(writer.ToArray()));

        Assert.Equal(domain.Name, decoded.Name);
        Assert.Equal(domain.Uuid, decoded.Uuid);
        Assert.Equal(domain.Id, decoded.Id);
    }

    [Fact]
    public void RemoteNonnullDomain_Uuid_Is16BytesFixedOpaque_NoLengthPrefix()
    {
        // VIR_UUID_BUFLEN = 16 is a well-known external constant (not defined
        // in either vendored .x file — see XdlConstantTable) resolved by the
        // generator; this pins the actual wire size it produced.
        var domain = new RemoteNonnullDomain { Name = "", Uuid = new byte[16], Id = 0 };
        var writer = new XdrWriter();
        domain.Encode(writer);

        // name: 4-byte length prefix + 0 bytes + 0 padding = 4
        // uuid: 16 raw bytes, no length prefix, already 4-byte aligned = 16
        // id: 4
        Assert.Equal(4 + 16 + 4, writer.Length);
    }

    [Fact]
    public void RemoteConnectOpenArgs_NullName_RoundTrips()
    {
        var args = new RemoteConnectOpenArgs { Name = null, Flags = 0 };
        var writer = new XdrWriter();
        args.Encode(writer);
        var decoded = RemoteConnectOpenArgs.Decode(new XdrReader(writer.ToArray()));

        Assert.Null(decoded.Name);
        Assert.Equal(0u, decoded.Flags);
    }

    [Fact]
    public void RemoteConnectOpenArgs_PresentName_RoundTrips()
    {
        var args = new RemoteConnectOpenArgs { Name = "qemu:///system", Flags = 7 };
        var writer = new XdrWriter();
        args.Encode(writer);
        var decoded = RemoteConnectOpenArgs.Decode(new XdrReader(writer.ToArray()));

        Assert.Equal("qemu:///system", decoded.Name);
        Assert.Equal(7u, decoded.Flags);
    }

    [Fact]
    public void RemoteAuthListRet_BoundedEnumList_RoundTrips()
    {
        var ret = new RemoteAuthListRet { Types = [RemoteAuthType.RemoteAuthNone, RemoteAuthType.RemoteAuthSasl] };
        var writer = new XdrWriter();
        ret.Encode(writer);
        var decoded = RemoteAuthListRet.Decode(new XdrReader(writer.ToArray()));

        Assert.Equal(ret.Types, decoded.Types);
    }

    [Fact]
    public void RemoteDomainGetInfoRet_NarrowIntegerFields_RoundTripViaFourByteWireSlots()
    {
        // state/nrVirtCpu are "unsigned char"/"unsigned short" in the .x file
        // — XDR has no sub-32-bit wire representation, so these are encoded
        // as a full 4-byte unsigned int and narrowed back on decode. Values
        // chosen near each C# type's max to catch a truncating cast bug.
        var info = new RemoteDomainGetInfoRet
        {
            State = 255,
            MaxMem = ulong.MaxValue,
            Memory = 1024,
            NrVirtCpu = 65535,
            CpuTime = 123456789,
        };

        var writer = new XdrWriter();
        info.Encode(writer);
        var decoded = RemoteDomainGetInfoRet.Decode(new XdrReader(writer.ToArray()));

        Assert.Equal(info.State, decoded.State);
        Assert.Equal(info.MaxMem, decoded.MaxMem);
        Assert.Equal(info.Memory, decoded.Memory);
        Assert.Equal(info.NrVirtCpu, decoded.NrVirtCpu);
        Assert.Equal(info.CpuTime, decoded.CpuTime);
    }

    [Fact]
    public void RemoteConnectListAllDomainsRet_BoundedStructList_RoundTrips()
    {
        var ret = new RemoteConnectListAllDomainsRet
        {
            Domains =
            [
                new RemoteNonnullDomain { Name = "vm1", Uuid = new byte[16], Id = 1 },
                new RemoteNonnullDomain { Name = "vm2", Uuid = new byte[16], Id = 2 },
            ],
            Ret = 2,
        };

        var writer = new XdrWriter();
        ret.Encode(writer);
        var decoded = RemoteConnectListAllDomainsRet.Decode(new XdrReader(writer.ToArray()));

        Assert.Equal(2, decoded.Domains.Count);
        Assert.Equal("vm1", decoded.Domains[0].Name);
        Assert.Equal("vm2", decoded.Domains[1].Name);
        Assert.Equal(2u, decoded.Ret);
    }
}
