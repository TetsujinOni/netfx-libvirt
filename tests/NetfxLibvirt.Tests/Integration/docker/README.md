# Integration test container

Built fresh by Testcontainers on every `dotnet test` run that exercises
`SshLibvirtdIntegrationTests` — never published anywhere, never a real
deployment target. See `docs/plan.md`'s transport section for why this
exists (the short version: real-infra validation that needs zero host
configuration, so it works the same for every contributor and in CI).

**`id_ecdsa` / `id_ecdsa.pub` are a fixed, committed test key pair, not
a real credential.** They authenticate into this throwaway, self-built
container and nothing else — the image is rebuilt from scratch (with this
exact public key baked into `/root/.ssh/authorized_keys`) every time the
tests run, so there is no real secret here to leak. This is the same
reasoning that lets integration test suites commit a fixed test database
password. If a secret scanner flags this file, that's the expected false
positive — do not rotate it as though it were a real key; just confirm it
still matches `authorized_keys` in the `Dockerfile`.
