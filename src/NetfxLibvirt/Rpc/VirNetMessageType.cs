namespace NetfxLibvirt.Rpc;

/// <summary>
/// Mirrors libvirt's <c>virNetMessageType</c> enum from
/// <c>src/rpc/virnetprotocol.x</c>. Values are load-bearing wire constants —
/// do not renumber.
/// </summary>
public enum VirNetMessageType
{
    /// <summary>client -&gt; server. args from a method call</summary>
    Call = 0,

    /// <summary>server -&gt; client. reply/error from a method call</summary>
    Reply = 1,

    /// <summary>either direction. async notification</summary>
    Message = 2,

    /// <summary>either direction. stream data packet</summary>
    Stream = 3,

    /// <summary>client -&gt; server. args from a method call, with passed FDs</summary>
    CallWithFds = 4,

    /// <summary>server -&gt; client. reply/error from a method call, with passed FDs</summary>
    ReplyWithFds = 5,

    /// <summary>either direction, stream hole data packet</summary>
    StreamHole = 6,
}
