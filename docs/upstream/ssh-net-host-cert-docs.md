# DRAFT (not filed) — SSH.NET: document what host-certificate validation does and does not do

Target: sshnet/SSH.NET docs / wiki, plus a correction to the example in PR #1498's
description (which is what people copy). Written against SSH.NET 2026.0.0;
every claim below was checked against that tag's source.

## Proposed text

### Validating host certificates

When a server presents an OpenSSH host certificate, SSH.NET checks that:

- the key-exchange signature was made by the key inside the certificate (so the
  peer holds that key),
- the certificate's signature verifies against the CA key embedded in it, and
- the current time is within the certificate's validity period.

**These are integrity checks, not trust decisions.** SSH.NET deliberately does
not decide *which* CAs to trust or *whether the certificate is meant for this
host*; that is the application's job, done in `HostKeyReceived`. A handler that
only compares `CertificateAuthorityKeyFingerPrint` will accept:

- a **user** certificate (`Type == User`) signed by your CA,
- a host certificate your CA issued for a **different** host,
- a certificate with **critical options** you don't understand,
- (note) certificates with **no principals** — OpenSSH treats an empty
  principals list as valid for any host; you may want to be stricter.

A complete check:

```csharp
static bool IsTrustedHostCertificate(Certificate cert, string host, ISet<string> trustedCaFingerprints)
{
    // 1. Only a host certificate may authenticate a server.
    if (cert.Type != Certificate.CertificateType.Host)
    {
        return false;
    }

    // 2. The CA must be one you chose to trust.
    if (!trustedCaFingerprints.Contains(cert.CertificateAuthorityKeyFingerPrint))
    {
        return false;
    }

    // 3. The certificate must be issued for this host. OpenSSH treats an empty
    //    principals list as "any host"; requiring an explicit match is stricter.
    //    (Principals may be wildcard patterns; match them if your CA issues those.)
    if (!cert.ValidPrincipals.Any(p => string.Equals(p, host, StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    // 4. Refuse critical options you don't understand (host certs normally have none).
    return cert.CriticalOptions.Count == 0;
}

client.HostKeyReceived += (_, e) =>
    e.CanTrust = e.Certificate is { } cert
        && IsTrustedHostCertificate(cert, client.ConnectionInfo.Host, trustedCaFingerprints);
```

Not covered by SSH.NET or the snippet above: revocation (KRLs / `@revoked`),
and CA signatures made with SHA-1 (`ssh-rsa`), which OpenSSH rejects by default
but SSH.NET accepts.

## Verification notes (for whoever reviews this)

- Type is never checked: `CertificateType` is referenced only inside
  `Certificate.cs`; `CertificateHostAlgorithm.VerifySignatureBlob` checks the KEX
  signature, the validity window, and the CA signature only.
- `ValidPrincipals` and `CriticalOptions` are referenced nowhere outside
  `Certificate.cs`.
- Snippet compiles against 2026.0.0 (`Certificate.CertificateType` is nested).
- SHA-1 CA signatures: **verified** — a real `sshd` presenting a certificate signed with `ssh-keygen -s ca -t ssh-rsa`
  completed a real SSH.NET handshake (netfx-libvirt `OpenSshHostCertificateIntegrationTests.Sha1CaSignature_...`).
- A real `sshd` cannot serve a certificate with an invalid CA signature (OpenSSH verifies it when loading and falls back to
  a plain key), so SSH.NET's signature checks can only be exercised through its public API
  (`ConnectionInfo.HostKeyAlgorithms` factory + `KeyHostAlgorithm.VerifySignature`); see netfx-libvirt
  `SshNetCertificateVerificationSentinelTests` for a worked example against real `ssh-keygen` certificates.
