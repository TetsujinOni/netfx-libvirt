using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Rpc;

/// <summary>
/// Mirrors libvirt's <c>virNetMessageHeader</c> struct from
/// <c>src/rpc/virnetprotocol.x</c> — six XDR-encoded 32-bit fields, always
/// exactly <see cref="WireSize"/> bytes on the wire.
/// </summary>
/// <param name="Prog">Unique ID for the program (e.g. <c>REMOTE_PROGRAM</c>, 0x20008086).</param>
/// <param name="Vers">Program version number.</param>
/// <param name="Proc">Unique ID for the procedure within the program.</param>
/// <param name="Type">Type of message.</param>
/// <param name="Serial">Serial number correlating a reply/stream packet with its originating call.</param>
/// <param name="Status">Request/reply/stream status.</param>
public readonly record struct VirNetMessageHeader(
    uint Prog,
    uint Vers,
    int Proc,
    VirNetMessageType Type,
    uint Serial,
    VirNetMessageStatus Status)
{
    /// <summary>Size of the encoded header in bytes (<c>VIR_NET_MESSAGE_HEADER_MAX</c>).</summary>
    public const int WireSize = 24;

    public void Encode(XdrWriter writer)
    {
        writer.WriteUInt(Prog);
        writer.WriteUInt(Vers);
        writer.WriteInt(Proc);
        writer.WriteEnum(Type);
        writer.WriteUInt(Serial);
        writer.WriteEnum(Status);
    }

    public static VirNetMessageHeader Decode(XdrReader reader)
    {
        var prog = reader.ReadUInt();
        var vers = reader.ReadUInt();
        var proc = reader.ReadInt();
        var type = reader.ReadEnum<VirNetMessageType>();
        var serial = reader.ReadUInt();
        var status = reader.ReadEnum<VirNetMessageStatus>();
        return new VirNetMessageHeader(prog, vers, proc, type, serial, status);
    }
}
