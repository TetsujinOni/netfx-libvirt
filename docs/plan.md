# netfx-libvirt — Plan

**Last updated:** 2026-09-16 (stories 1–5 done)

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

**Status: next; environment confirmed ready, 2026-09-16.** Everything above
(stories 1–5) was provable hermetically. Story 6 onward needs an actual
reachable libvirtd — checked directly rather than assumed:

- WSL2 Ubuntu 22.04 already has `libvirtd` 8.0.0 running, the calling user
  in the `libvirt` group (`auth_unix_rw = "none"` — matches
  `LibvirtConnection.OpenAsync`'s `AuthNone`-only support, no lab
  credentials needed), and KVM/nested-virt confirmed working. No package
  provisioning needed.
- Per the open-source-independence requirement (this project must stay
  reproducible by any contributor, not depend on the maintainer's private
  lab): **`test:///default`** — libvirt's built-in null-hypervisor driver,
  already present with a running fake domain, real RPC traffic over a real
  Unix socket, zero virtualization capability required — is the intended
  validation target for this story and stories 7–10's own tests, not
  `qemu:///system`. Real qemu+KVM is available in this WSL instance too and
  fine for the maintainer's own manual sanity pass, but nothing committed
  should depend on it existing. A lab account is not needed for any of
  stories 6–10.
- Compatibility with an 8.0.0-era daemon specifically (not just "some
  libvirtd") was verified by diffing the real `v8.0.0` tag against our
  vendored master `.x` files — see `reference/README.md`'s "Compatibility
  bisection" section. Zero changes touch anything this project generates;
  the WSL daemon is a safe, and genuinely representative (Ubuntu 22.04 LTS,
  broadly deployed), validation target. Follow-on design implication for
  later stories: treat an "unknown procedure" `VIR_NET_ERROR` reply as an
  expected, catchable case (`LibvirtRpcException`) once the emitted surface
  grows past what an old fleet member supports — not something to
  version-pin the vendored `.x` files around.

**6. Local Unix-socket transport.**
`UnixSocketTransport` over `System.Net.Sockets.Socket` +
`UnixDomainSocketEndPoint` (supported on both Windows 10+ and Linux — no
platform shim needed). **First point this project validates against a real
libvirtd** — connect + auth + open against `test:///default` over WSL's
Unix socket, nothing more yet.

**7. `ListDomains()`.**
`ConnectListAllDomains(1, 3)` + a `DomainGetState` call per returned domain,
assembled into the same shape `virt-desktop`'s `VMInfo` carries (name, uuid,
id, state — `HasVNC` is out of scope, see above). Validate against real WSL
libvirtd domains.

**8. `StartDomain`/`ShutdownDomain`/`DestroyDomain`.**
Each: `DomainLookupByName` then `DomainCreate`/`DomainShutdown`/
`DomainDestroy`. Validate against a real (disposable) VM in WSL libvirtd.

**9. `GetDomainXml()`.**
`DomainLookupByName` + `DomainGetXMLDesc`. Validate against real libvirtd.

**10. `DisconnectAsync()`.**
`ConnectClose` + close the transport stream. Completes the
`LibvirtConnection` surface to full parity with `hypervisor.go`'s
`hypervisorAPI` interface.

### Remote deployment — matching `virt-desktop`'s actual pattern

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
