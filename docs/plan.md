# netfx-libvirt — Plan

**Last updated:** 2026-09-16 (stories 1–10 done — validated against a real libvirtd)

This is the living backlog. `docs/status.md` describes what's already built;
this file is what's next, broken into small stories in dependency order.
Each story is sized "2 points" — a single concern, completable and testable
on its own, without needing the stories after it to make sense.

## Parity target: `virt-desktop`'s `hypervisor.go`

The concrete goal for this phase isn't "cover more of `remote_protocol.x`"
in the abstract — it's matching what the sibling Go/Wails app
(`D:\work\virt-desktop`) already does today, so `uwp-virt-manager` (or
whatever consumes this library) has no functional gap versus that app. Read
directly from its source, not from memory of it:

- `hypervisor.go` — `Connect`, `Disconnect`, `ListVMs`, `StartVM`,
  `ShutdownVM`, `PowerOffVM`, `GetVMXML`.
- `connection.go` — the transport is `golang.org/x/crypto/ssh` (pure Go,
  no `ssh.exe` shellout — matches our own SSH.NET decision) dialing the
  **remote Unix domain socket** (`/var/run/libvirt/libvirt-sock`) *through*
  the SSH connection via `client.Dial("unix", socketPath)` — i.e. an
  OpenSSH `direct-streamlocal` channel, not a TCP port forward. This is a
  real research risk for the SSH.NET story below (see story 11).
- `app.go` — the orchestration: `ConnectToHost` (SSH dial → socket dial →
  `hv.Connect()`), `GetVMs` (list + per-VM state, `hv.GetVMXML` reused just
  to sniff VNC availability from the XML client-side — no separate libvirt
  call for that), `StartVM`/`ShutdownVM`/`PowerOffVM`/`GetVMXML`,
  `DisconnectFromHost`.

Explicitly **not** in scope here (Wails-app-level, not libvirt-protocol-level):
credential keyring (`secrets.go`), saved-host storage (`hoststore.go`), the
noVNC WebSocket bridge (`novnc_ws_bridge.go`, `console.go`) — VNC/SPICE
console access is `uwp-virt-manager`'s job, not this library's.

Tracing those Go calls down to the actual RPC procedures they use (verified
against the real `remote_protocol.x`, not assumed):

| Go call | Procedure | # | Already emitted? |
|---|---|---|---|
| `authenticate()` (always run before open) | `REMOTE_PROC_AUTH_LIST` | 66 | yes |
| `initLibvirtComms` | `REMOTE_PROC_CONNECT_OPEN` | 1 | yes |
| `Disconnect()` | `REMOTE_PROC_CONNECT_CLOSE` | 2 | yes |
| `Domains()` → `ConnectListAllDomains(1, 3)` | `REMOTE_PROC_CONNECT_LIST_ALL_DOMAINS` | 273 | yes |
| `ListVMs()` per-domain state | `REMOTE_PROC_DOMAIN_GET_STATE` | 212 | yes |
| `StartVM`/`ShutdownVM`/`PowerOffVM`/`GetVMXML` (`h.client.DomainLookupByName`) | `REMOTE_PROC_DOMAIN_LOOKUP_BY_NAME` | 23 | yes |
| `StartVM` | `REMOTE_PROC_DOMAIN_CREATE` | 9 | yes |
| `ShutdownVM` | `REMOTE_PROC_DOMAIN_SHUTDOWN` | 33 | yes |
| `PowerOffVM` | `REMOTE_PROC_DOMAIN_DESTROY` | 12 | yes |
| `GetVMXML` | `REMOTE_PROC_DOMAIN_GET_XML_DESC` | 14 | yes |

`ConnectListDomainsActive = 1`, `ConnectListDomainsInactive = 2` (so
`flags = 3` for "all") — these are public libvirt ABI constants, not in
either vendored `.x` file; confirmed against go-libvirt's own generated
`const.gen.go` as a second independent reference (same standard as
`VIR_UUID_BUFLEN` in `XdlConstantTable`).

`REMOTE_PROC_DOMAIN_GET_INFO`/`REMOTE_PROC_CONNECT_GET_CAPABILITIES`, already
emitted from the original MVP guess, turn out **not** to be on the parity
path — `virt-desktop` doesn't call them. Harmless to keep generated; not
blocking anything below.

## Stories

### Codegen — close the procedure gap

**Status: done (stories 1–3), 2026-09-16.**

**1. Emit `DomainLookupByName` and `DomainGetState` DTOs.**
Add `REMOTE_PROC_DOMAIN_LOOKUP_BY_NAME` and `REMOTE_PROC_DOMAIN_GET_STATE`
to `Program.cs`'s procedure list; regenerate; `RemoteDomainLookupByNameArgs`/
`Ret` and `RemoteDomainGetStateArgs`/`Ret` land in
`src/NetfxLibvirt/Generated/Remote`. `REMOTE_PROC_CONNECT_CLOSE` has no
args/ret struct in the real file — nothing to emit for it, it's just a
procedure number the RPC layer needs (see story 3). Round-trip test each new
DTO the same way `RemoteProcedureDtoTests` already does.

**2. Emit `RemoteError` (+ its `RemoteNonnullNetwork` dependency).**
`remote_error` is the payload of every `VIR_NET_ERROR` reply — the RPC call
engine (story 4) can't decode a failure without it. Confirmed it needs no
new shape-algebra work: every field (`int`, optional bounded string,
optional `remote_domain`/`remote_network`) is already something
`XdlTypeResolver`/`XdlFieldEmitter` handles; `remote_nonnull_network` is a
plain two-field struct. Add `"remote_error"` as an explicit closure root in
`Program.cs` (it's never any procedure's args/ret struct, so
`XdlClosureCollector` won't reach it on its own).

**3. Emit a `RemoteProcedure` number registry.**
A generated `enum RemoteProcedure` (or a `static class` of `int` consts) —
one entry per procedure in `remote_procedure`, not just the ones with
args/ret DTOs — so `VirNetMessageHeader.Proc` never gets a hardcoded magic
number at a call site. Straightforward extension of the existing
`CSharpTypeEmitter.EmitEnum` path (it already emits `remote_procedure`-shaped
enums correctly); the new part is choosing to emit *all* procedure numbers,
not filtering to the MVP set the way struct/enum closure collection does.

Also emitted while doing this (not its own story, needed as a small
dependency of story 4): `RemoteProtocolConstants` — `REMOTE_PROGRAM`/
`REMOTE_PROTOCOL_VERSION` as real `const long`s, via a new
`CSharpTypeEmitter.EmitConstants`, instead of the RPC engine hand-typing
those two magic numbers.

### RPC engine — hermetic, no real libvirtd needed yet

**Status: done (stories 4–5), 2026-09-16.**

**4. `VirNetRpcCall` request/reply engine.**
Built as `VirNetRpcClient` (`src/NetfxLibvirt/Rpc/VirNetRpcClient.cs`). Given
an open `Stream`, sends a `Call` message (`VirNetMessageHeader` +
XDR-encoded args) with an auto-incrementing serial number, awaits the
matching `Reply` frame via the existing `VirNetMessageFraming`, and either
hands back the raw reply payload or throws a `LibvirtRpcException` (new,
`src/NetfxLibvirt/Rpc/LibvirtRpcException.cs`) built from `RemoteError` when
`VirNetMessageStatus.Error` comes back. Half-duplex by design (one call in
flight at a time) — good enough for the parity milestone, revisit if events/
streams need concurrent in-flight messages later. Tested hermetically
(`VirNetRpcClientTests`) over a small fake duplex `Stream`
(`FakeDuplexStream`, `tests/NetfxLibvirt.Tests/Rpc/`) — no network, no real
libvirtd, same discipline as the existing framing tests.

**5. Auth + open handshake.**
`LibvirtConnection.OpenAsync(stream, uri)` (`src/NetfxLibvirt/LibvirtConnection.cs`):
calls `AuthList`, requires the response to include `AuthNone` (throws
`NotSupportedException` naming what was actually offered otherwise —
SASL/Polkit are real gaps, not silently ignored), then `ConnectOpen`. Same
hermetic fake-stream test approach as story 4 (`LibvirtConnectionTests`).

### Transport — first real-libvirtd proof

**Status: done (stories 6–10), 2026-09-16 — validated against a real running libvirtd.**

Environment: WSL2 Ubuntu 22.04's `libvirtd` 8.0.0, the calling user already
in the `libvirt` group (`auth_unix_rw = "none"` — matches
`LibvirtConnection.OpenAsync`'s `AuthNone`-only support, no lab credentials
needed). One real provisioning gap found and closed: the WSL distro had no
.NET SDK, so a Windows-native `dotnet test` can't reach a Unix socket living
inside WSL2's separate kernel namespace regardless. Installed .NET 10 SDK
into WSL via Microsoft's official `dotnet-install.sh` (user-local, no root,
`~/.dotnet`) — a standard, publicly-documented, zero-lab-dependency step,
not a lab account. From there the repo (mounted at `/mnt/d/work/netfx-libvirt`)
builds and runs identically to any Linux host.

Per the open-source-independence requirement (this project must stay
reproducible by any contributor, not depend on the maintainer's private
lab), **`test:///default`** — libvirt's built-in null-hypervisor driver,
real RPC over a real Unix socket, zero virtualization capability required —
is the validation target for stories 6–10, not `qemu:///system` or the lab.
Confirmed the daemon itself (not just `virsh`'s in-process driver fallback)
serves it, by forcing the remote/RPC path explicitly:
`virsh -c 'test+unix:///default?socket=/var/run/libvirt/libvirt-sock' list --all`.
A lab account was not needed for any of stories 6–10.

Compatibility with an 8.0.0-era daemon specifically (not just "some
libvirtd") was verified beforehand by diffing the real `v8.0.0` tag against
our vendored master `.x` files — see `reference/README.md`'s "Compatibility
bisection" section. Zero changes touch anything this project generates.
Follow-on design implication for later stories: treat an "unknown
procedure" `VIR_NET_ERROR` reply as an expected, catchable case
(`LibvirtRpcException`) once the emitted surface grows past what an old
fleet member supports — not something to version-pin the vendored `.x`
files around.

All of `tests/NetfxLibvirt.Tests/Integration/LibvirtdIntegrationTests.cs`
passed against the real daemon (6/6): open, list domains (finds the seeded
`test` domain, correct `Running` state), get its XML, destroy then restart
it and observe the state transitions, disconnect cleanly, and a real
`VIR_ERR_NO_DOMAIN`-shaped error decoding into `LibvirtRpcException` for an
unknown name. These tests are **skipped by default** (via `Assert.Skip`,
gated on the `NETFX_LIBVIRT_TEST_SOCKET` env var) so `dotnet test` stays
100% hermetic and reproducible with zero infrastructure on any contributor's
machine — run them with:
```bash
NETFX_LIBVIRT_TEST_SOCKET=/var/run/libvirt/libvirt-sock \
    dotnet test tests/NetfxLibvirt.Tests --filter "FullyQualifiedName~Integration"
```
from WSL or any Linux host with a `test:///default`-capable libvirtd — never
from a Windows-native process, which can't reach a WSL2 Unix socket.

**6. Local Unix-socket transport.** `UnixSocketTransport`
(`src/NetfxLibvirt/Transport/UnixSocketTransport.cs`) over
`System.Net.Sockets.Socket` + `UnixDomainSocketEndPoint`. Proven against the
real daemon.

**7. `ListDomains()`.** `LibvirtConnection.ListDomainsAsync` — `ConnectListAllDomains`
(flags via the new `ConnectListAllDomainsFlags`) + a `DomainGetState` call
per returned domain, assembled into `DomainSummary` (the same shape
`virt-desktop`'s `VMInfo` carries: name, uuid, id, state via the new
`DomainState` enum — `HasVNC` stays out of scope, see above). Proven against
the real daemon.

**8. `StartDomain`/`ShutdownDomain`/`DestroyDomain`.**
`LibvirtConnection.{StartDomainAsync,ShutdownDomainAsync,DestroyDomainAsync}` —
each: `DomainLookupByName` then `DomainCreate`/`DomainShutdown`/
`DomainDestroy`. Proven against the real daemon (destroy + restart
round-trip, state transitions observed).

**9. `GetDomainXml()`.** `LibvirtConnection.GetDomainXmlAsync` —
`DomainLookupByName` + `DomainGetXMLDesc`. Proven against the real daemon.

**10. `DisconnectAsync()`.** `LibvirtConnection.DisconnectAsync` —
`ConnectClose` + close the transport stream, idempotent.
`LibvirtConnection.DisposeAsync` does the same best-effort (never throws,
even if the graceful close fails). Completes the `LibvirtConnection`
surface to full parity with `hypervisor.go`'s `hypervisorAPI` interface.
Proven against the real daemon.

### Remote deployment — matching `virt-desktop`'s actual pattern

**Status: next.**

**11. SSH transport via SSH.NET.**
The real research risk flagged above: `virt-desktop` doesn't port-forward
TCP, it opens an OpenSSH `direct-streamlocal` channel straight to the remote
Unix socket path. Confirm SSH.NET (`Renci.SshNet`) exposes that channel
type before assuming the API shape — if it doesn't, the fallback is opening
the channel type manually against SSH.NET's lower-level primitives. Resolve
this **first**, before writing the story's implementation, since it changes
the shape of everything else in it.

**12. End-to-end validation against the real lab host.**
Full `Connect → ListDomains → Start/Shutdown/Destroy → GetDomainXml →
Disconnect` path over SSH against a real remote libvirtd
(`qemu+ssh://root@<host>/system`, the sibling SPICE project's lab
environment) — the actual "at parity with virt-desktop" finish line.

## After parity

Once 1–12 are done, `netfx-libvirt` does everything `virt-desktop` does
today. Further procedure coverage (toward go-libvirt-level breadth), TCP/TLS
transports, and anything past this list belongs in a follow-up pass of this
same plan, not bolted on ahead of parity.
