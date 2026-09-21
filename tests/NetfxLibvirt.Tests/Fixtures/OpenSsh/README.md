# OpenSSH fixtures

Real artifacts produced by the real `ssh-keygen` — a CA per key type (ed25519, RSA, ECDSA), host
certificates covering valid / expired / not-yet-valid / wrong principal / empty principals /
wildcard principal / user-cert-as-host / wrong CA / tampered signature / critical option /
SHA-1 CA signature, and plain, hashed, `@cert-authority` and `@revoked` `known_hosts` files.
Everything is for host name `lab.example`.

Only public halves are checked in; the private CA/host keys are throwaway and deleted by
`generate.sh`, which regenerates the whole set consistently (`bash generate.sh`). Nothing here
is a credential for anything real. Copied next to the test DLL by the test project's `.csproj`.

Two private keys ARE kept, both throwaway: `host_ed25519` (the key the `cert-*.pub` files certify)
and `attacker_ed25519` (a key no certificate certifies). They let the sentinel tests make
key-exchange-style signatures the way a server would, to drive SSH.NET's real certificate
verification through its public API — see `OpenSsh/SshNetCertificateVerificationSentinelTests.cs`.
