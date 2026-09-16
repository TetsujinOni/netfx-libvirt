namespace NetfxLibvirt.Xdr;

/// <summary>
/// Thrown when data being decoded does not conform to RFC 4506 XDR encoding
/// rules (truncated input, a length that exceeds a declared bound, or a
/// boolean/enum value outside its legal range).
/// </summary>
public sealed class XdrException : Exception
{
    public XdrException(string message) : base(message)
    {
    }
}
