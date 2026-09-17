namespace NetfxLibvirt.Transport;

/// <summary>A remote SSH server's host key, in a form independent of
/// whichever SSH library <see cref="SshTransport"/> happens to use
/// underneath — so a future library swap (already happened once; see
/// <c>docs/plan.md</c> story 11) never breaks a caller's
/// <see cref="SshHostKeyVerifier"/> implementation.</summary>
/// <param name="AlgorithmName">The host key algorithm, e.g. <c>ssh-ed25519</c>.</param>
/// <param name="KeyLengthBits">The key length in bits.</param>
/// <param name="Sha256Fingerprint">The SHA-256 fingerprint in the same
/// format <c>ssh-keygen -l -E sha256</c> prints (non-padded base64, no
/// <c>SHA256:</c> prefix) — the form most pinning/TOFU stores compare
/// against.</param>
/// <param name="RawKey">The raw host key blob, for callers that need more
/// than the fingerprint (e.g. writing a <c>known_hosts</c>-style entry).</param>
public sealed record SshHostKeyInfo(string AlgorithmName, int KeyLengthBits, string Sha256Fingerprint, byte[] RawKey);
