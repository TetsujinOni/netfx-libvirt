using System.Buffers.Binary;
using System.Text;

namespace NetfxLibvirt.Transport.OpenSsh;

/// <summary>
/// The tiniest possible slice of the SSH wire format (RFC 4251 §5
/// <c>string</c>: a big-endian uint32 length, then that many bytes) — just
/// enough to read a leading algorithm/type name out of a blob and to slice
/// the embedded public key out of a certificate for a deny-only revocation
/// check. This library deliberately does NOT parse certificates or verify
/// signatures itself (SSH.NET does both, and the policy decisions are made
/// on the very object whose signature SSH.NET verified — never on a second
/// parse of the same bytes, which would open a parser-differential gap; see
/// <c>docs/plan.md</c> story 17). Every read is bounds-checked and returns
/// <see langword="false"/> rather than throwing.
/// </summary>
internal static class SshWire
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryReadString(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (offset < 0 || data.Length - offset < 4)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        if (length > (uint)(data.Length - offset - 4))
        {
            return false;
        }

        value = data.Slice(offset + 4, (int)length);
        offset += 4 + (int)length;
        return true;
    }

    /// <summary>The leading <c>string</c> of <paramref name="blob"/> as text
    /// (a key blob's key type, a signature blob's algorithm name), or
    /// <see langword="null"/> if it's absent, not valid UTF-8, or contains
    /// anything but printable non-space ASCII — these names end up in
    /// space-delimited <c>known_hosts</c> lines, so nothing else is legal.</summary>
    public static string? TryReadLeadingName(ReadOnlySpan<byte> blob)
    {
        var offset = 0;
        if (!TryReadString(blob, ref offset, out var raw) || raw.Length == 0)
        {
            return null;
        }

        foreach (var b in raw)
        {
            if (b is < 0x21 or > 0x7E)
            {
                return null;
            }
        }

        return StrictUtf8.GetString(raw);
    }

    public static byte[] EncodeString(ReadOnlySpan<byte> value)
    {
        var result = new byte[4 + value.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)value.Length);
        value.CopyTo(result.AsSpan(4));
        return result;
    }

    public static byte[] EncodeString(string value) => EncodeString(Encoding.ASCII.GetBytes(value));
}

/// <summary>Structural slice of an OpenSSH certificate blob
/// (PROTOCOL.certkeys): the public key the certificate certifies, as a
/// standard key blob. Used only on the deny side (revocation matching) and
/// for display on the KEX-failure path — never to accept anything.</summary>
internal static class SshCertificateWire
{
    public static bool IsCertificateAlgorithm(string name) => name.EndsWith("-cert-v01@openssh.com", StringComparison.Ordinal);

    public static bool TryGetEmbeddedKeyBlob(ReadOnlySpan<byte> certificate, out byte[] keyBlob)
    {
        keyBlob = [];
        var offset = 0;

        if (!SshWire.TryReadString(certificate, ref offset, out var rawName)
            || !SshWire.TryReadString(certificate, ref offset, out _)) // nonce
        {
            return false;
        }

        var (plainType, fieldCount) = System.Text.Encoding.ASCII.GetString(rawName) switch
        {
            "ssh-ed25519-cert-v01@openssh.com" => ("ssh-ed25519", 1),
            "ssh-rsa-cert-v01@openssh.com" => ("ssh-rsa", 2),
            "ecdsa-sha2-nistp256-cert-v01@openssh.com" => ("ecdsa-sha2-nistp256", 2),
            "ecdsa-sha2-nistp384-cert-v01@openssh.com" => ("ecdsa-sha2-nistp384", 2),
            "ecdsa-sha2-nistp521-cert-v01@openssh.com" => ("ecdsa-sha2-nistp521", 2),
            _ => ((string?)null, 0),
        };

        if (plainType is null)
        {
            return false;
        }

        var start = offset;
        for (var i = 0; i < fieldCount; i++)
        {
            if (!SshWire.TryReadString(certificate, ref offset, out _))
            {
                return false;
            }
        }

        var typeString = SshWire.EncodeString(plainType);
        var fields = certificate[start..offset];
        keyBlob = new byte[typeString.Length + fields.Length];
        typeString.CopyTo(keyBlob, 0);
        fields.CopyTo(keyBlob.AsSpan(typeString.Length));
        return true;
    }
}
