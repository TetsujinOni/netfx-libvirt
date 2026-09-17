# netfx-libvirt — Plan

**Last updated:** 2026-09-17 (stories 1–11 done and validated against real infra; story 11's fixture image published to GHCR and pulled by default, and its SSH library swapped to SSH.NET for real-account Ed25519 auth. Story 12's read-only slice is done against the real lab host; only Start/Shutdown/Destroy against a real host remains open, deliberately deferred — see that story below.)

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
`VirNetMessageStatus.Error` comes back. One call in flight at a time from
the caller's side — but tolerant of unsolicited `Message`/`Stream` frames
arriving interleaved with a call's reply (fixed 2026-09-17, see the Events
story below; originally assumed the very next frame read was always the
reply). Tested hermetically
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

**Status: implemented and validated against real infra, 2026-09-16 — zero host configuration needed.**

**11. SSH transport.** Superseded the original SSH.NET assumption after two
rounds of research:

1. The flagged research risk was real: `virt-desktop` opens an OpenSSH
   `direct-streamlocal` channel straight to the remote Unix socket
   (`golang.org/x/crypto/ssh`'s `Client.Dial("unix", ...)`). Checked three
   candidate libraries against that specific requirement: SSH.NET has no
   native support for it; **`Microsoft.DevTunnels.Ssh`** (Microsoft's own
   pure-managed SSH2 client/server, used in VS Code Remote / Dev Tunnels)
   doesn't either — confirmed by a zero-hit GitHub code search for
   `streamlocal` across its whole repo, and zero issues ever mentioning it;
   **`Tmds.Ssh`** (an independent, modern, AOT-native library) does, via a
   first-class `OpenUnixConnectionAsync` API.
2. Before picking a library around that one feature, checked whether
   `direct-streamlocal` is actually required at all — it isn't. Read
   libvirt's own `src/remote/remote_ssh_helper.c` (vendored in this
   session's reference clone): libvirt's **own official SSH transport**
   doesn't use `direct-streamlocal` either. It execs `virt-ssh-helper <uri>`
   over a plain SSH **exec** channel; the helper connects to the local
   libvirt socket and relays raw bytes over its own stdin/stdout — no
   framing, no handshake. (Confirmed present on the WSL validation host:
   `virt-ssh-helper (libvirt) 8.0.0`.) This is the same shape as how Docker
   reaches a remote daemon over SSH (`docker system dial-stdio`, also a
   plain exec channel) — `virt-desktop`'s `Dial("unix", ...)` is a
   Go-library convenience shortcut to the same destination, not the
   canonical mechanism.

Since a plain SSH `exec` channel is the most universally-supported SSH
client capability there is, the missing-feature objection to
`Microsoft.DevTunnels.Ssh` no longer applies, and it's the library used:
Microsoft-maintained, pure-managed (no P/Invoke, same requirement as
everything else in this project), used in production. In a
security-critical, supply-chain-conscious context, that pedigree was judged
to outweigh `Tmds.Ssh`'s more modern (AOT-native) design and narrower
maintainer base (242 stars) — an explicit, deliberate tradeoff, not a
default.

**Superseded 2026-09-17 — swapped to `SSH.NET`.** Story 12's real-host
validation needed to authenticate as the user's actual lab account, whose
only authorized key is Ed25519. `Microsoft.DevTunnels.Ssh.Keys`' importer
only supports RSA/ECDSA — confirmed directly against its source (zero
Ed25519 or OpenSSH-certificate files anywhere in its tree), not assumed.
Checked whether upstream contribution could close this gap first rather
than defaulting to a library swap: a complete, CLA-signed, cleanly
mergeable PR adding Ed25519 to DevTunnels.Ssh's TypeScript side
([microsoft/dev-tunnels-ssh#124](https://github.com/microsoft/dev-tunnels-ssh/pull/124))
has had zero maintainer engagement for 6+ months, and multiple
community-filed issues on that repo show the same pattern (#91 open since
December 2023, zero comments) — measured evidence this wouldn't close on
any useful timeline, and that PR doesn't even touch the .NET side this
project actually depends on regardless. Forking DevTunnels.Ssh to add the
coverage was also considered and rejected: its real technical strength is
multi-channel/interactive session handling for its native Dev Tunnels use
case, none of which this project uses — one plain exec channel is the
entire requirement, so maintaining a patched fork for capability never
exercised here is sprawl, not a fit. Both `Tmds.Ssh` and `SSH.NET` support
Ed25519 and OpenSSH certificates; `SSH.NET` was picked on the same
supply-chain-concentration reasoning that ruled `Tmds.Ssh` out the first
time (one primary maintainer, small star count) — it's the larger,
longer-established community project of the two. Its `SshCommand` type
supplies exactly the primitive this transport needs — a raw,
non-interactive, non-PTY duplex exec channel (`OutputStream` /
`CreateInputStream()`) — not just the PTY-biased `ShellStream` a shallower
read of its API might suggest. Full test suite (including the four
Testcontainers-backed real-SSH tests) passed unchanged after the swap:
245/0/6, same as before — the public `SshTransport`/`SshTransportOptions`
surface didn't need to change shape, only `SshHostKeyVerifier`'s parameter
type, now `SshHostKeyInfo` (a small library-independent record) instead of
leaking a DevTunnels-specific key type — cheap insurance against this
exact kind of swap happening a third time.

Implementation: `Transport/{SshTransport,SshTransportOptions,SshHostKeyVerifier,SshHostKeyVerifiers,SshHostKeyInfo,SshCommandDuplexStream,SshTransportException}`.
`SshTransport.ConnectAsync` opens an `SshClientSession`, requires the
caller to supply a `VerifyHostKey` callback (no silent-trust default —
deliberately unlike `virt-desktop`'s own `connection.go`, which uses
`ssh.InsecureIgnoreHostKey()` with an unresolved "configure this properly
in production" comment; `SshHostKeyVerifiers.DangerousAcceptAny` exists for
lab use only, named loudly on purpose), authenticates with password or a
private-key file (matching `virt-desktop`'s `SSHConnectionParams`; SSH-agent
support not included yet), execs `virt-ssh-helper <uri>`, and returns the
channel wrapped as a `Stream` (`SshStream`, provided by the library) —
which plugs straight into the already-proven `LibvirtConnection.OpenAsync`
unchanged, since everything from story 4 onward was built transport-agnostic
from the start.

**Real-SSH validation pivoted to Testcontainers, replacing host
configuration entirely** — prompted by a good challenge to the original
"install `openssh-server` in WSL, needs `sudo`" plan: that only works on
*this* machine, and does nothing for a future OSS contributor's machine or
GitHub Actions CI. Neither of those can be assumed to have WSL, a
configured libvirtd, or an interactively-typed `sudo` password — but both
can be assumed to have Docker.

`tests/NetfxLibvirt.Tests/Integration/docker/` is a small, self-authored
image (not a third-party Docker Hub image — better supply-chain posture,
consistent with story 11's own library-choice reasoning): Ubuntu 22.04 (the
same libvirt 8.0.0 baseline the compatibility bisection validated) +
`libvirt-daemon-system` + `openssh-server`, nothing else — no
qemu-system/kvm, no `--privileged`, no `/dev/kvm`, since these tests only
ever touch libvirt's `test:///default` null-hypervisor driver. A fixed,
committed test-only key pair (`id_ecdsa`/`id_ecdsa.pub` — see that
directory's README for why committing a "private" key here is fine: it
authenticates into a container built fresh from this same repo on every
run, not a real secret) is baked into `authorized_keys`.
`Integration/LibvirtdContainerFixture` (an xUnit collection fixture) builds
and starts this image via `Testcontainers`, mapping the SSH port to a
random host port; if Docker itself isn't reachable, tests skip cleanly
instead of failing (same UX as the old env-var gate, just a much lower,
more standard bar). `SshLibvirtdIntegrationTests` now runs against it
**by default, no env var, no manual setup** — and does, every time
`dotnet test` runs on a machine with Docker, including CI once a workflow
exists.

Caught immediately by actually running it: the first test-key generation
used ed25519, and `Microsoft.DevTunnels.Ssh.Keys`' OpenSSH importer only
supports RSA/ECDSA — every test failed with a clear
`NotSupportedException` naming the exact gap. Regenerated as ECDSA P-256;
all 4 tests then passed against the real, freshly-built container: open,
list domains (finds the seeded `test` domain), get its XML, disconnect.

**Fixture-location fix:** the container fixture (and, retrofitted for
consistency, `RealProtocolFileTests`/`RealModuleTests` in
`NetfxLibvirt.ProtocolGen.Tests`) originally located their on-disk fixture
data by walking up from `AppContext.BaseDirectory` looking for
`netfx-libvirt.slnx`. Flagged as a known-fragile pattern (it assumes the
source tree ships alongside the test binaries — breaks for a published
test payload, sharded CI, etc.) — switched to build-time
`CopyToOutputDirectory` instead (`<None Update="Integration\docker\**" ...>`
in `NetfxLibvirt.Tests.csproj`; `<None Include="..\..\reference\upstream-x\*.x" Link="...">`
in `NetfxLibvirt.ProtocolGen.Tests.csproj`), so every fixture path is just
`AppContext.BaseDirectory` plus a fixed relative path, always correct
because the build itself put the files there.

**Image rebuild cost fix:** measured, not assumed — `ImageFromDockerfileBuilder`'s
own default (confirmed by reading its source) is a fresh random image name
plus `PullPolicy.Always` on every call, which guarantees a full rebuild
(cold `apt-get install` and all) every single test run. `docker system df`
showed 0B of build cache in use even across repeated runs on the same
warm local Docker daemon — this was never just a CI concern, it was
costing ~24 seconds on *every* local `dotnet test` too. Fixed by tagging
the image with a SHA-256 hash of its own input files (`Dockerfile`,
`entrypoint.sh`, `id_ecdsa.pub`) and building with `PullPolicy.Missing`:
repeated runs with unchanged inputs reuse the cached image (measured
24s → 4s), and any edit to those files changes the hash, so a stale image
can never silently outlive a Dockerfile change — no manual `docker image
rm` needed.

**GHCR publish, implemented 2026-09-17:** the "revisit once CI exists"
deferral above was reopened once the repo actually landed on GitHub
(`TetsujinOni/netfx-libvirt`, public) and the cost argument was reframed
without needing CI to exist first: this is the Release-Reuse Equivalence
Principle applied to a build artifact instead of a code package — the
image's content changes on the order of "a Dockerfile edit" or "a
backported sshd/libvirtd CVE," while the old approach rebuilt it on the
order of "a test invocation." That mismatch costs more, not less, as
contributor count grows (N independent local builds of unchanged content
vs. one centralized build + N cheap pulls). `.github/workflows/publish-test-fixture-image.yml`
builds and pushes to `ghcr.io/tetsujinoni/netfx-libvirt/test-fixture-libvirtd-sshd:<hash>`
(same content-hash tag scheme as the local build, computed identically via
`cat Dockerfile entrypoint.sh id_ecdsa.pub | sha256sum`) on any push to
`main` touching the Docker context, skipping the build if that hash is
already published. Image acquisition was split out of
`LibvirtdContainerFixture` into `LibvirtdFixtureImageAcquisition` (own
file): it tries the registry pull first by default (`PullPolicy.Missing`),
falling back to a local build on any failure (image not published yet for
a PR that just edited the Dockerfile, no network, fork without registry
access) — no separate "staleness story" needed, since a content-hash tag
that doesn't exist in the registry just falls through to the same local
build path that was already the only path before this. Force local build
directly (skip the doomed pull attempt while iterating on the Dockerfile)
via `NETFX_LIBVIRT_TEST_FORCE_LOCAL_BUILD=1`.

**Validated end-to-end, 2026-09-17:** pushed to `origin/main`
(`76c6473`), the workflow ran green (`gh run watch`) and published
`ghcr.io/tetsujinoni/netfx-libvirt/test-fixture-libvirtd-sshd:b4e8e1b7c4b5`.
An anonymous `docker pull` (no login at all) succeeded — the
package came up **public by default**, not private; the "known gap"
originally written here about GHCR defaulting new packages to private
didn't materialize for a repo-linked package on a public repo, so no
manual visibility fix was needed. Confirmed both acquisition paths
actually take the code path they claim to, not just "tests still pass
either way": after clearing the local Docker image cache entirely, a
plain `dotnet test` run resolved the container under the `ghcr.io/...`
tag (proving the registry-pull path ran, not a silent fallback) in ~10s;
`NETFX_LIBVIRT_TEST_FORCE_LOCAL_BUILD=1 dotnet test` produced the
separate `netfx-libvirt-integration-test-libvirtd` tag (proving the
fallback path still works standalone) in ~30s. Both: 245 succeeded, 0
failed, 6 skipped — identical to the pre-GHCR baseline. An adversarial
security review (`/security-review`, sub-agent identify + filter pass)
of the workflow and acquisition code before pushing found no findings
above low-confidence/non-exploitable (push-to-main + `workflow_dispatch`
only, no `pull_request` trigger, so no fork-PR path reaches the
`packages: write`-scoped token).

**Deliberately not done yet:** migrating `LibvirtdIntegrationTests` (the
raw local Unix-socket transport, stories 6–10) to the same container
approach. A Unix domain socket is a kernel object, not just a file — even
bind-mounted to a host-visible path, a container's socket isn't dialable
from a native Windows process (Docker Desktop's backend is itself a Linux
VM; same fundamental boundary WSL2 has). That migration can only be proven
from a Linux-kernel context (Linux CI, or WSL) that this session doesn't
have direct access to prove against right now, so it's tracked as a
follow-up rather than shipped unverified — this project doesn't trust
infra-adjacent code it hasn't actually run, and just watched that
discipline catch a real bug above. `LibvirtdIntegrationTests` keeps its
existing env-var/WSL-gated design for now, unaffected.

**12. End-to-end validation against the real lab host.**

**Status: read-only slice done and validated, 2026-09-17** — `Connect →
ListDomains → GetDomainXml → Disconnect` against a real production
libvirtd 12.0.0 (`qemu+ssh://tetsujinoni@srv-l-vm01/system`), authenticating
with the user's actual Ed25519 account key (the reason for the SSH.NET
swap above). `Start/Shutdown/Destroy` deliberately **not** exercised here:
the host's `qemu:///system` has no disposable domain — every one of its 8
domains is either real infrastructure (`Win2019-Dev-Oni`, `srv-w-app01`,
`srv-l-db01`) or a maintained fixture another project (`uwp-virt-manager`'s
SPICE suite) depends on staying running. That lifecycle RPC surface was
already proven for real against WSL's `test:///default` in stories 6–10;
what story 12 actually needed to prove — the SSH transport, the exec
channel, and the RPC/error-decoding path against a real, differently
versioned (12.0.0 vs. the 8.0.0 fixture baseline), real-domain-carrying
daemon — is now proven too.

Two real findings surfaced doing this for real rather than assuming it'd
just work:

1. **A validation-script bug, not a library bug, but a genuine API trap.**
   The first two attempts failed with a real, correctly-decoded
   `remote_error`: `Cannot recv data: Host key verification failed.:
   Connection reset by peer`. Root-caused methodically rather than guessed:
   confirmed `CONNECT_OPEN`'s encoding is byte-correct by running the exact
   same call locally against a real (if empty) `qemu` driver in WSL over
   `UnixSocketTransport` (succeeded); confirmed the SSH transport itself is
   fine by running it against `test:///default` on the very same host over
   the very same SSH path (succeeded); confirmed real libvirt clients
   (`virsh`, `virt-manager`) hit no such error on the identical
   `qemu+ssh://` path. The actual mistake: the validation script passed the
   full `qemu+ssh://tetsujinoni@srv-l-vm01/system` string as
   `LibvirtConnection.OpenAsync`'s `uri` argument instead of the
   transport-stripped `qemu:///system` a real client sends — libvirtd's
   qemu driver tried to interpret that string as *its own* outbound SSH
   target and hit a real host-key failure trying to SSH to itself. Fixed
   in the script; documented prominently on `LibvirtConnection.OpenAsync`
   itself (`src/NetfxLibvirt/LibvirtConnection.cs`) so a future caller
   doesn't repeat it — this is an easy trap precisely because the wrong
   value produces a *plausible-sounding but totally unrelated* real error
   rather than an obvious rejection.
2. **`RemoteConnectOpenArgs`/`AUTH_LIST` behave identically against a real,
   busy, multi-domain daemon as against an empty one** — no domain-count-
   dependent surprises, no auth-mechanism divergence (`auth_unix_rw =
   "none"` on this host too, confirmed directly, matching every prior
   validation environment).

`Start`/`Shutdown`/`Destroy` against a real lab host remains open —
deliberately deferred, not blocked: it needs either a disposable domain
provisioned specifically for this (out of scope to arrange unprompted
against someone's live infrastructure) or accepting the stories 6–10
WSL proof as sufficient for that RPC surface. Revisit only if a disposable
target becomes available.

## After parity: protocol breadth toward go-libvirt-level coverage

Once 1–12 are done, `netfx-libvirt` does everything `virt-desktop` does
today. This phase is about the rest of `remote_protocol.x` — go-libvirt's
own generated bindings cover 454 of the real file's 456 `REMOTE_PROC_*`
procedures (confirmed by reading `remote_protocol.gen.go` directly, not
assumed from its README), so "go-libvirt-level breadth" really does mean
the whole surface eventually, not a curated subset. 12 procedures are
already emitted (the parity set); roughly 444 remain, grouped below by
libvirt object type, counts cross-checked against both the vendored `.x`
file and go-libvirt's real coverage.

| Epic | Procedures | Notes |
|---|---|---|
| Storage pools | 23 | self-contained |
| Storage volumes | 15 | depends on pools |
| Networks | 22 | self-contained |
| Network ports | 6 | depends on Networks |
| Node devices | 22 | self-contained |
| Node (host CPU/mem/etc.) | 15 | self-contained |
| Interfaces (host netcfg) | 11 | self-contained |
| NWFilter + bindings | 10 | self-contained |
| Secrets | 9 | self-contained |
| Domain — breadth (get/set/block/lifecycle/save-restore) | ~155 | huge — its own multi-epic sub-plan when reached |
| Domain snapshots | 13 | depends on Domain breadth basics |
| Domain checkpoints | 6 | depends on snapshots |
| Domain migration | 25 | complex, multi-step, higher risk — last |
| Auth (SASL/Polkit) | 5 | only needed past AuthNone-only environments |
| Events (register/deregister + delivery) | 69 | **foundational plumbing**, see below — most of this batch deferred |
| Connect (misc: capabilities, feature-support, CPU baseline/compare, storage-source-finding, …) | ~65 | mixed; picked up alongside whichever epic above actually needs each one |

**Priority order** (utility-for-a-general-admin-tool first, least new
plumbing first): risk-reduce the Events architecture change now (stories
13–14 below) while the codebase is still modest, **then** Storage pools →
Storage volumes → Networks/ports → Node devices → Node → Domain breadth
(own sub-plan) → Interfaces/NWFilter/Secrets → Auth → Migration, with the
remaining 60+ event procedures picked up opportunistically per-epic rather
than as one big batch — e.g. storage-pool lifecycle events land as part of
the Storage pools epic, not deferred to a separate "all events" pass.

### 13. Remove the RPC client's half-duplex assumption.

**Status: done, 2026-09-17.** Pulled forward deliberately, ahead of any
epic above, on a risk-management basis: the interleaved-frame problem only
gets more expensive to retrofit the more calling code accumulates on top
of the old assumption, so fix it while the surface is still small (this
plan's 12 procedures) rather than after Storage/Network/Domain breadth
triples it.

`VirNetRpcClient.CallAsync` used to assume the very next frame read after
sending a call was that call's reply — true only when nothing else talks
on the connection. Real connections can deliver a `Message` (event) or
`Stream` frame unprompted, interleaved with an in-flight call's reply.
Fixed by having `CallAsync` keep reading frames in a loop until one
actually matches its own serial, raising anything else via a new
`UnsolicitedMessageReceived` event instead of misinterpreting it as the
reply (or, for a reply with someone else's serial — shouldn't happen given
one call in flight at a time, but isn't an event either — silently
dropping it and continuing to wait for its own).

Deliberately still cooperative, not a true independent background reader:
nothing reads the stream while no call is in flight, so an event that
arrives with no call currently awaiting a reply is invisible until the
next call happens to be made. A full push-based background reader (a
persistent read loop, a `ConcurrentDictionary<serial, TaskCompletionSource>`
for true overlapping in-flight calls) was drafted and deliberately
backed out: it requires the test double (`FakeDuplexStream`) to model a
real socket's blocking-read-until-data-or-close semantics instead of
EOF-on-drained-queue, and every existing test's "queue all expected
replies, then make all the calls" pattern would need rewriting to
interleave queuing with calling — real work, for a capability (events
delivered with zero calls in flight) nothing in this plan needs yet. Keep
it in mind if a future epic genuinely needs live push delivery outside an
active call; don't build it speculatively now.

Tested hermetically: `FakeDuplexStream` gained `QueueMessage` (queues an
unsolicited `Message`-type frame, serial 0, matching how a real server
frames one) alongside the existing `QueueReply`. Two new
`VirNetRpcClientTests`: an unsolicited message queued before the real
reply is raised via `UnsolicitedMessageReceived` and doesn't corrupt the
call's own result; a reply for the wrong serial queued before the right
one is silently dropped, not raised as unsolicited. Full suite:
252 total, 0 failed, 6 skipped (WSL-gated), matching the pre-change
baseline.

### 14. Validate the architecture with two event procedures.

**Status: not started.** `REMOTE_PROC_CONNECT_DOMAIN_EVENT_CALLBACK_REGISTER_ANY`
/ `..._DEREGISTER_ANY` plus decoding one real event payload
(`remote_domain_event_callback_lifecycle_msg` — domain start/stop/etc.
state transitions, the simplest and most universally-supported event
type) against the existing Testcontainers `test:///default` fixture:
register, then exercise `StartDomainAsync`/`DestroyDomainAsync` (already
proven, stories 6–10) and confirm the lifecycle event actually arrives via
`UnsolicitedMessageReceived` while those calls are in flight — proving
story 13's fix end-to-end against a real daemon, not just hermetically.
The other 60+ event procedures stay deferred, picked up per-epic later
(see the priority-order note above) rather than as a follow-on batch here.
