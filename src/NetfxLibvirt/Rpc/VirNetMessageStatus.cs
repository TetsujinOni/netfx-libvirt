namespace NetfxLibvirt.Rpc;

/// <summary>
/// Mirrors libvirt's <c>virNetMessageStatus</c> enum from
/// <c>src/rpc/virnetprotocol.x</c>. Values are load-bearing wire constants —
/// do not renumber.
/// </summary>
public enum VirNetMessageStatus
{
    /// <summary>Calls: always OK. Replies: no error.</summary>
    Ok = 0,

    /// <summary>Replies: an error happened, a <c>remote_error</c> struct follows.</summary>
    Error = 1,

    /// <summary>Streams: more data is still expected.</summary>
    Continue = 2,
}
