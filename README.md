# netfx-libvirt

A pure managed C#/.NET client for [libvirt](https://libvirt.org)'s RPC wire
protocol — **no P/Invoke, no native `libvirt.so`/`libvirt-0.dll`
dependency.** Working name, echoing `go-libvirt`'s own naming pattern.

libvirt's RPC protocol is a documented, XDR-encoded (RFC 4506) wire format —
it doesn't require the native C client library at all.
[`digitalocean/go-libvirt`](https://github.com/digitalocean/go-libvirt)
proves a from-scratch, no-cgo reimplementation works; every existing .NET
libvirt library ([`libvirt-csharp`](https://libvirt.org/csharp.html),
[`libvirt-dotnet`](https://www.nuget.org/packages/libvirt-dotnet/),
[`IDNT.AppBasics.Virtualization.Libvirt`](https://www.nuget.org/packages/IDNT.AppBasics.Virtualization.Libvirt/))
P/Invokes the native library instead, with real reported DLL-load fragility
on Windows. This project exists to close that gap.

## Status

**Parity milestone done**, validated against real infrastructure, not just
synthetic bytes: connect, list domains, start/shutdown/destroy a domain, get
a domain's XML, and disconnect — matching what the sibling Go/Wails app
`virt-desktop` does today. Two working transports (local Unix socket, SSH),
both proven against a real running `libvirtd` — including a real production
host with real domains, over SSH, authenticating with a real account key.

Now in a second phase: broadening RPC procedure coverage toward
`go-libvirt`-level breadth (~450 procedures, not just the ~12 the parity
milestone needed). See [`docs/plan.md`](docs/plan.md) for the full backlog
and [`docs/status.md`](docs/status.md) for a detailed snapshot of what's
built.

## Approach

Codegen-first, mirroring `go-libvirt`'s own strategy: parse libvirt's real
upstream `.x` XDR protocol files (`src/rpc/virnetprotocol.x`,
`src/remote/remote_protocol.x` — vendored in
[`reference/upstream-x`](reference/upstream-x)) and generate C# bindings
mechanically via a Roslyn-based emitter (`tools/NetfxLibvirt.ProtocolGen`),
rerunnable against new libvirt versions rather than hand-ported once.

Transports:
- **Local Unix socket** — done.
- **SSH** (`qemu+ssh://`, execing libvirt's own `virt-ssh-helper` over a
  plain exec channel rather than shelling out to `ssh.exe`) — done, on
  [SSH.NET](https://github.com/sshnet/SSH.NET).
- **Plain TCP, TLS (PKI client certs)** — not yet implemented.

Integration tests that need a real `libvirtd` run against a small,
self-authored Docker image (Ubuntu 22.04 + `libvirt-daemon-system` +
`openssh-server`, no qemu/kvm) via [Testcontainers](https://testcontainers.com/) —
published to GHCR and pulled by default so nobody, including CI, rebuilds it
from scratch on every run. Needs only a working Docker daemon; falls back to
building locally if the published image can't be reached (e.g. a fork).

## Building

```bash
dotnet build
dotnet test
```

Requires the .NET 10 SDK. Most tests are fully hermetic (no external
dependencies); the SSH/libvirtd integration tests additionally need a
working Docker daemon and are skipped cleanly if one isn't available.

## License

Not yet chosen — will be an OSI-approved permissive license (matching the
Apache 2.0 reference implementation).
