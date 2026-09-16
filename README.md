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

Early — XDR codec and RPC message framing are implemented and tested; no
transport or generated procedure bindings yet. See
[`docs/status.md`](docs/status.md) for the current state and next steps.

## Approach

Codegen-first, mirroring `go-libvirt`'s own strategy: parse libvirt's real
upstream `.x` XDR protocol files (`src/rpc/virnetprotocol.x`,
`src/remote/remote_protocol.x` — vendored in
[`reference/upstream-x`](reference/upstream-x)) and generate the ~200+
procedure bindings mechanically, rerunnable against new libvirt versions.

Planned transports: local (Unix socket), plain TCP, TLS (PKI client certs),
and SSH (`qemu+ssh://`, via [SSH.NET](https://github.com/sshnet/SSH.NET) —
no shelling out to `ssh.exe`).

## Building

```bash
dotnet build
dotnet test
```

Requires the .NET 10 SDK.

## License

Not yet chosen — will be an OSI-approved permissive license (matching the
Apache 2.0 reference implementation) before any public release.
