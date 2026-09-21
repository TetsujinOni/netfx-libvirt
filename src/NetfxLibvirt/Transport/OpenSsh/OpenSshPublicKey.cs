using System.Security.Cryptography;

namespace NetfxLibvirt.Transport.OpenSsh;

/// <summary>
/// A public key as it appears in <c>known_hosts</c> / <c>.pub</c> files: a
/// key type name plus the opaque SSH wire blob. Deliberately opaque — keys
/// are compared by exact blob equality (what an operator wrote into
/// <c>known_hosts</c> against what the server sent), never re-encoded or
/// cryptographically interpreted here. Works for any key type, including
/// ones this library can't otherwise use (e.g. <c>ssh-dss</c>,
/// <c>sk-*</c>), since a <c>known_hosts</c> file may legitimately contain
/// them.
/// </summary>
public sealed class OpenSshPublicKey : IEquatable<OpenSshPublicKey>
{
    private readonly byte[] _blob;

    private OpenSshPublicKey(string keyType, byte[] blob)
    {
        KeyType = keyType;
        _blob = blob;
    }

    /// <summary>The key type name, e.g. <c>ssh-ed25519</c>, <c>ssh-rsa</c>, <c>ecdsa-sha2-nistp256</c>.</summary>
    public string KeyType { get; }

    /// <summary>The key's SSH wire blob (the thing that's base64-encoded in <c>known_hosts</c>).</summary>
    public ReadOnlyMemory<byte> Blob => _blob;

    /// <summary>The fingerprint exactly as <c>ssh-keygen -l</c> prints it: <c>SHA256:</c> + unpadded base64 of SHA-256(blob).</summary>
    public string Sha256Fingerprint => FingerprintOf(_blob);

    /// <summary>The blob, base64-encoded as it is written in <c>known_hosts</c>.</summary>
    public string ToBase64() => Convert.ToBase64String(_blob);

    /// <exception cref="FormatException">The blob doesn't start with a valid key type name.</exception>
    public static OpenSshPublicKey FromBlob(ReadOnlySpan<byte> blob)
    {
        var type = SshWire.TryReadLeadingName(blob) ?? throw new FormatException("The public key blob doesn't begin with a valid key type name.");
        return new OpenSshPublicKey(type, blob.ToArray());
    }

    /// <summary>Parses the base64 field of a <c>known_hosts</c> line; the blob's own leading type must equal <paramref name="expectedKeyType"/> when given.</summary>
    public static bool TryFromBase64(string base64, string? expectedKeyType, out OpenSshPublicKey key)
    {
        key = null!;
        Span<byte> buffer = new byte[base64.Length]; // decoded size <= encoded size
        if (!Convert.TryFromBase64String(base64, buffer, out var written))
        {
            return false;
        }

        var blob = buffer[..written];
        var type = SshWire.TryReadLeadingName(blob);
        if (type is null || (expectedKeyType is not null && !string.Equals(type, expectedKeyType, StringComparison.Ordinal)))
        {
            return false;
        }

        key = new OpenSshPublicKey(type, blob.ToArray());
        return true;
    }

    /// <summary><c>SHA256:</c> fingerprint of an arbitrary blob, same format as <see cref="Sha256Fingerprint"/>.</summary>
    public static string FingerprintOf(ReadOnlySpan<byte> blob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    public bool Equals(OpenSshPublicKey? other) => other is not null && _blob.AsSpan().SequenceEqual(other._blob);

    public override bool Equals(object? obj) => Equals(obj as OpenSshPublicKey);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_blob);
        return hash.ToHashCode();
    }

    public override string ToString() => $"{KeyType} {Sha256Fingerprint}";
}
