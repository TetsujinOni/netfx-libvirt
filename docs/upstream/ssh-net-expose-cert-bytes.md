# DRAFT (not filed) — SSH.NET: expose the raw certificate bytes on HostKeyEventArgs

> **Status: not blocking.** netfx-libvirt obtains the exact wire bytes today via supported public API: it wraps the
> factories in `ConnectionInfo.HostKeyAlgorithms` (each receives the wire blob and returns the algorithm object the
> `HostKeyReceived` event is later built from), matching the capture to the event by `ReferenceEquals(algorithm.Certificate,
> e.Certificate)`. Caveat found the hard way: SSH.NET reuses that dictionary to build the *CA key's* algorithm during
> certificate verification, so capture every invocation and match by identity — not "the last one". A convenience
> property would still remove that subtlety for other consumers.

## Request

`Certificate.Bytes` and `Certificate.BytesForSignature` are `internal`, so a
`HostKeyReceived` handler cannot get the certificate exactly as the server sent
it. Proposal: make the encoded certificate available to consumers, e.g.
`public byte[] Certificate.Bytes { get; }` (or `HostKeyEventArgs.CertificateBytes`).
`BytesForSignature` can stay internal if the full blob is public — the signed
prefix is derivable from it.

The object model can't substitute for the bytes: the parse is lossy
(`Certificate.cs`, `LoadData`), so the signed bytes can't be reconstructed:

- the reserved field is read and discarded,
- critical options / extensions go into a `Dictionary`, losing wire order,
- the public key is parsed into a `Key` and would have to be re-encoded.

A re-serialisation would verify *different bytes* than the CA signed.

## Use cases not served by today's surface

1. **Independent signature verification under the consumer's own policy.**
   SSH.NET verifies the CA signature itself, but the consumer may need a
   stricter rule: reject SHA-1 (`ssh-rsa`) CA signatures (OpenSSH's default
   since 8.2), enforce a FIPS-approved algorithm set, or verify with a platform
   or HSM provider. That needs the exact signed bytes plus the signature, and
   only the parse-lossy object model is available. Doing the verification in a
   second, independent parser also removes single-parser-bug risk from a
   security decision.
2. **Audit and evidence.** Recording the certificate exactly as presented (a
   trust-decision log, or writing it to a file so an operator can run
   `ssh-keygen -L -f` on it) needs the original encoding. Re-serialised bytes
   would not reproduce the presented cert's signature, and would not be
   admissible as "what the server sent".
3. *(weaker)* **Handing the certificate to another component** — e.g. a shared
   OpenSSH-format parser/validator, or a fixture-based test of the consumer's own
   policy code — without a lossy round trip.

Note: fingerprint parity is *not* a use case. `ssh-keygen -l` on a certificate
prints the fingerprint of the embedded key, which matches the existing
`FingerPrintSHA256` (checked with OpenSSH 10.2 on an ed25519 host cert).

## Cost

Essentially a getter change: the array is already held (`Bytes` backs
`CertificateHostAlgorithm.Data`). Whether to return the array or a copy is the
maintainers' call (not checked how sibling properties such as `Signature` do it).

## Companion

See `ssh-net-host-cert-docs.md`: docs/example change stating that
`HostKeyReceived` consumers must check type, principals and critical options.
File the docs change first — it's uncontroversial and establishes that trust
belongs to the consumer, which is what makes this exposure the natural next step.
