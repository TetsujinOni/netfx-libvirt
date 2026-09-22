# netfx-libvirt — Plan

**Last updated:** 2026-09-22 (story 20, `ssh_config` `Host` alias resolution, done — see below; story 17, OpenSSH-standard host verification (`known_hosts` + host certificates), done — see below; story 16, async SSH host key verification, done — see below; story 15, RPC streaming / `DOMAIN_OPEN_GRAPHICS`, done hermetically — see below. Stories 1–11 done and validated against real infra; story 11's fixture image published to GHCR and pulled by default, and its SSH library swapped to SSH.NET for real-account Ed25519 auth. Story 12's read-only slice is done against the real lab host; only Start/Shutdown/Destroy against a real host remains open, deliberately deferred — see that story below.)

This is the living backlog. `docs/status.md` describes what's already built;
this file is what's next, broken into small stories in dependency order.
Each story is sized "2 points" — a single concern, completable and testable
on its own, without needing the stories after it to make sense.

## Parity target: `virt-desktop`'s `hypervisor.go`

The concrete goal for this phase isn't "cover more of `remote_protocol.x`"
in the abstract — it's matching what the sibling Go/Wails app
(`D:\work\virt-desktop`) already does today, so `avalonia-virt-manager` (or
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
console access is `avalonia-virt-manager`'s job, not this library's.

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
`srv-l-db01`) or a maintained fixture another project (`avalonia-virt-manager`'s
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

### 15. RPC streaming (`VIR_NET_CONTINUE`) + real remote graphics access.

**Status: done and validated against the real domain, 2026-09-18 — but not
via the mechanism first attempted.** Requested by `avalonia-virt-manager`:
its real SPICE/VNC console MVP fails against libvirt's actual common-case
default, `listen='127.0.0.1'` (confirmed against a real domain,
`Win2019-Dev-Oni` on `srv-l-vm01`) — the graphics server only binds the
hypervisor's loopback interface, so a raw socket dial to the hypervisor's
routable IP gets connection-refused.

**First attempt (superseded, see below): `REMOTE_PROC_DOMAIN_OPEN_GRAPHICS`
as a `VIR_NET_CONTINUE`-signaled RPC stream.** `VirNetRpcClient.CallAsync`
already had an explicit placeholder throwing `NotSupportedException` on a
`VIR_NET_CONTINUE` reply (story 13's own security-review note), which read
as exactly the gap to fill. Built `OpenStreamAsync`/`VirNetRpcStream` and
`LibvirtConnection.OpenGraphicsAsync` on that basis, backed by 19 hermetic
tests. **Running this for real against `srv-l-vm01` immediately surfaced a
genuine libvirt error: `"internal error: No FD available at slot 0"`.**

Root-caused by reading libvirt's actual C client
(`remoteDomainOpenGraphics`/`remoteDomainOpenGraphicsFD` in
`src/remote/remote_driver.c`, fetched fresh rather than assumed) and
go-libvirt's real generated wrapper side by side:
**`REMOTE_PROC_DOMAIN_OPEN_GRAPHICS`/`_FD` are both FD-passing
(`SCM_RIGHTS` ancillary data on the socket itself), not
`VIR_NET_CONTINUE`/stream-based at all** — `remoteDomainOpenGraphics` calls
`callFull(..., fdin, fdinlen, NULL, NULL, ...)`, handing the server a
*local* file descriptor via ancillary data; `remoteDomainOpenGraphicsFD`
reads one back the same way. Neither can work over SSH/TCP — OS-level FD
passing only exists over a real local `AF_UNIX` socket. go-libvirt's own
generated `DomainOpenGraphics`/`DomainOpenGraphicsFd` wrappers confirm this
independently: both call `requestStream(proc, ..., nil, nil)` — no
reader/writer wired at all, unlike go-libvirt's *real* stream users
(`DomainOpenConsoleBidirectional`, `DomainScreenshot`,
`StorageVolUpload`/`Download`, `DomainMigratePrepareTunnel*`, all of which
pass real `io.Reader`/`io.Writer`). The peer session's premise — "libvirt's
own RPC-level graphics-open mechanism... this is how virt-manager itself
reaches it" — didn't hold up against the primary source. There is no
RPC-level remote graphics tunnel in libvirt; real `virt-viewer`/
`virt-manager` reach a loopback-bound graphics server via their own
independent SSH port forward, not any libvirt RPC call.

**Separately, the frame-timing model was also wrong and has been fixed.**
Per libvirt's real client dispatcher (`virNetClientCallDispatchReply` vs.
the structurally separate `virNetClientCallDispatchStream` in
`src/rpc/virnetclient.c`): `VIR_NET_CONTINUE` only ever appears on
`VIR_NET_STREAM`-typed frames, **never on the reply itself** — matching
`virnetprotocol.x`'s own doc comment, which this class's original version
misread. `OpenStreamAsync` now treats the opening call's reply as an
ordinary `Ok`/`Error` (returning both that reply's own payload — some real
stream procedures like `DOMAIN_SCREENSHOT` carry real data there — and the
opened `VirNetRpcStream`), then reads subsequent `Type=Stream` frames for
the actual data. `VirNetRpcStream` itself (the stream-frame read/write
loop: `Continue`-with-data, `Ok`-empty end, the documented
`Continue`-with-empty-payload libvirtd quirk, `Error` mid-stream) was
already correct and needed no changes — kept as real, reusable
infrastructure for the procedures that genuinely use it
(`DOMAIN_OPEN_CONSOLE`, `_SCREENSHOT`, `_MIGRATE_PREPARE_TUNNEL`, etc.),
just not for graphics. `REMOTE_PROC_DOMAIN_OPEN_GRAPHICS` support
(`LibvirtConnection.OpenGraphicsAsync`, `RemoteDomainOpenGraphicsArgs`) was
removed — it's genuinely unusable remotely, and keeping a real-looking API
that silently fails against every non-local connection would be worse than
not having it.

**The real fix: `Transport/SshPortForward`, an independent SSH local port
forward (`ssh -L`'s equivalent) via SSH.NET's `ForwardedPortLocal`**, bound
to an ephemeral local port and pointed at `127.0.0.1:<graphics-port>` as
resolved from the hypervisor's own network — exactly what real
`virt-viewer` does. **Validated end-to-end against the real domain this
story exists for**: connected to `srv-l-vm01`, fetched `Win2019-Dev-Oni`'s
real XML (`<graphics type='vnc' port='5900' autoport='yes'
listen='127.0.0.1'>`), opened the port forward, and read the real VNC
server's actual protocol banner back through it — `RFB 003.008\n`,
byte-exact, the real RFB version-handshake string, not a guess or
placeholder. A hermetic-adjacent integration test
(`SshPortForwardIntegrationTests`, against the Testcontainers fixture)
proves the same mechanism generically by forwarding to the fixture
container's own `sshd` and reading back a real `SSH-2.0-...` banner — real
bytes genuinely crossing the tunnel, without needing qemu/kvm in the
fixture image.

Full suite: 271 total (270 + the new port-forward integration test), 0
failed, 6 skipped (Unix-socket integration tests, WSL-gated as before). The
generalizable lesson: a peer session's stated premise about how a
third-party protocol works is exactly the kind of claim this project's own
validation standard exists to catch before building on it — running
against real infrastructure at the first opportunity (rather than only
after a full implementation plus hermetic tests) surfaced this in minutes.

**Follow-up, same day: `SshPortForward.OpenAsync`/`SshTransport.ConnectAsync`
were swallowing `OperationCanceledException`.** `avalonia-virt-manager`
validated `SshPortForward.OpenAsync` for real (VNC console now renders
through it against `Win2019-Dev-Oni`) and found a real bug wiring it in:
both methods' `catch (Exception ex)` around `client.ConnectAsync(cancellationToken)`
caught cancellation too, rewrapping it as a misleading
`SshTransportException("...authentication...failed")` instead of letting
`OperationCanceledException` surface — so a caller cancelling mid-connect
(e.g. the user navigating away) saw a fake auth failure instead of an
ordinary cancellation. Fixed in both with a
`catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)`
clause ahead of the generic one, rethrowing as-is. Two new hermetic
regression tests (`SshCancellationTests`) prove it with an already-cancelled
token and an unreachable (`TEST-NET-3`) host — no Docker/network needed,
since SSH.NET's own `BaseClient.ConnectAsync` calls
`cancellationToken.ThrowIfCancellationRequested()` before touching the
network. Full suite: 273 total, 0 failed, 6 skipped.

### 16. Async SSH host key verification.

**Status: done, 2026-09-21.** Requested by `avalonia-virt-manager`, which is
replacing `DangerousAcceptAny` with persisted trust-on-first-use pinning: with
only the synchronous `SshHostKeyVerifier` delegate, the first connection to a
host has to be a blind auto-pin — TOFU's weakest point — because a consumer
can't ask the user "Trust this host? SHA256:..." *during* the handshake.

- **`AsyncSshHostKeyVerifier`** (`ValueTask<bool>(SshHostKeyInfo, CancellationToken)`)
  supplied via `SshTransportOptions.VerifyHostKeyAsync`, honored by both
  `SshTransport.ConnectAsync` and `SshPortForward.OpenAsync` (which now share
  one connect-and-verify path, `SshTransport.ConnectClientAsync`, instead of
  two copy-adjacent ones — the duplication that already caused the
  cancellation bug in 77048a0).
- **Exactly one of `VerifyHostKey` / `VerifyHostKeyAsync` must be set.**
  C# can't express that at compile time without breaking existing callers
  (a single `required` property can't be "one of two"), so it's checked at the
  first possible moment — the start of the connect call, before any network
  I/O — and throws `ArgumentException` for neither *or* both. "Never silently
  accept an unverified host key" still holds; the sync verifier is unchanged.
- **Bridging SSH.NET's synchronous `HostKeyReceived` event without
  deadlocking.** The handler blocks on the async verifier, but the verifier is
  started with `Task.Run` (thread pool, no `SynchronizationContext`) so nothing
  it awaits can ever need the blocked thread, and the connect itself is also
  run off the caller's context. The wait is cancellable
  (`Task.WaitAny(task, ct)`), so cancelling the connect returns promptly even
  from a verifier that ignores its token (documented: honor it, or your
  prompt is left orphaned on screen). Mutation-checked: replacing `Task.Run`
  with a direct call makes the deadlock regression test hang and fail.
- **A rejected key is `SshHostKeyRejectedException`** (carries the exact
  `SshHostKeyInfo`, host, port; derives from `SshTransportException`, which
  is therefore no longer `sealed`) — for the sync verifier too, so consumers
  no longer sniff a generic failure. A verifier cancelled by the *connect's*
  token surfaces as `OperationCanceledException` (consistent with 77048a0);
  a throwing verifier (including one cancelled by some *other* token)
  rejects the key and surfaces as `SshTransportException` wrapping the fault.
- **Real finding while verifying the timeout story: SSH.NET bounds the whole
  handshake — including time spent in the host key event — with
  `ConnectionInfo.Timeout` (30 s default).** A person taking a minute to
  answer a trust dialog would have the connect time out underneath the
  prompt. Added `SshTransportOptions.ConnectTimeout` (applied to
  `ConnectionInfo.Timeout`) and documented it on the delegate and both
  properties; an integration test proves a 3 s verifier fails under a 1 s
  timeout and succeeds under 60 s. (The server has its own limit too:
  OpenSSH `LoginGraceTime`, 120 s.)

Tests (20 new; suite now 293 total, 0 failed, 6 skipped): hermetic
(`SshHostKeyVerificationTests`) — validation for neither/both, validation
happens before any network I/O, sync back-compat, async accept, async reject
→ `SshHostKeyRejectedException`, verifier receives the key and the connect's
token, cancellation during the prompt (including a token-ignoring verifier
and a late-faulting one), throwing verifier, foreign-token cancellation, and
the deadlock regression against a blocked `SynchronizationContext` (asserting
the verifier also *ran* with no context); end-to-end over a real SSH handshake
against the Testcontainers `sshd` (`SshHostKeyVerifierIntegrationTests`) —
async accept for both transports, reject for both, sync reject, cancel while
prompting, and the timeout bound.

### 17. OpenSSH-standard host verification (`known_hosts`, `@cert-authority` host certificates, `@revoked`).

**Status: done, 2026-09-21.** Priority request from `avalonia-virt-manager`
(whose product decision is: adopt OpenSSH's own formats and semantics, no
bespoke trust store — the audience is sysadmins/enterprise). Motivation: the
real lab host presents an OpenSSH **host certificate**
(`ssh-ed25519-cert-v01@openssh.com`), the normative enterprise model. Pinning
the cert's fingerprint breaks on every renewal, pinning the embedded key breaks
when the key rotates with the cert, so the trust anchor must be the CA key.

**What was built** (`src/NetfxLibvirt/Transport/OpenSsh/`, namespace
`NetfxLibvirt.Transport.OpenSsh`; final signatures in `docs/status.md`):
`OpenSshKnownHosts` (parse/match/append), `OpenSshPublicKey`,
`OpenSshPattern`, `SshHostCertificate` (facts + policy),
`OpenSshHostKeyVerifier.Create(options)` → `AsyncSshHostKeyVerifier` +
`GetTrustState`, `IOpenSshHostKeyPrompt`; and in `Transport/`,
`SshHostKeyRejectedException.Reason` (+ `SshHostKeyRejectionReason`,
`SshCertificateProblem`) and `SshHostKeyInfo.{Certificate,CertificateBlob}`.

**Scope decision — this library does NOT verify certificate cryptography, and
must not.** Reviewed adversarially and agreed with the user: reimplementing
signature verification/parsing in agent-written code is high cost and adds a
*parser-differential* risk (policy read from a second parse of the same bytes
SSH.NET verified). Instead, SSH.NET does the cryptography and this library adds
the trust policy SSH.NET deliberately leaves to the consumer. Findings that
shaped it (SSH.NET 2026.0.0, sources at tag):

- SSH.NET verifies, *before* raising `HostKeyReceived`: the KEX signature
  against the key **embedded in the certificate** (so the peer provably holds
  it — pinned by a sentinel test), the CA signature (against the CA key
  embedded in the cert — an integrity check, not trust), and the validity
  window (real clock, `UtcNow`).
- SSH.NET does **not** check type (a USER cert is accepted as a host cert),
  principals, critical options, or CA trust; it accepts SHA-1 (`ssh-rsa`) CA
  signatures. All confirmed against real handshakes/real `ssh-keygen` certs.
- Consequently a cert SSH.NET rejects (expired, not-yet-valid, bad signature)
  **never reaches the verifier**. `SshHostKeyVerification` maps it afterwards
  from public facts: validity window → `Expired`/`NotYetValid`; anything else →
  `VerificationFailed` (the honest umbrella; we don't re-run SSH.NET's crypto
  to pick which).
- The raw wire blob is obtained by wrapping the **public**
  `ConnectionInfo.HostKeyAlgorithms` factories (they receive the wire bytes).
  Gotcha found by the integration tests: SSH.NET reuses that same dictionary to
  build the *CA key's* algorithm while verifying the cert, so "the last
  capture" is the CA; captures are matched to the event by object identity /
  exact bytes. No upstream change needed. (Also: the event's `HostKey` is a
  `BigInteger` re-encoding of the key, not the wire bytes; `RawKey` is now the
  exact wire blob.)
- A real `sshd` cannot serve a certificate with a bad CA signature — OpenSSH
  verifies it when loading, and silently falls back to a plain host key. So
  SSH.NET's signature checks are pinned by `SshNetCertificateVerificationSentinelTests`,
  which drive SSH.NET's *real* verification code through its public API
  (`HostKeyAlgorithms` factory + `KeyHostAlgorithm.VerifySignature`) with real
  `ssh-keygen` certificates and real private keys. **If an SSH.NET upgrade
  makes any of those fail, hold the upgrade.**

**Behaviour** (decision table is on `OpenSshHostKeyVerifier`'s doc):
plain-key semantics as OpenSSH (same type + different key ⇒ `ChangedKey` with
`file:line`; other types only ⇒ unknown ⇒ prompt; `@revoked` ⇒ reject);
`RequireCertificateWhenCaCovers` (default true: a plain key from a CA-covered
host is rejected, no TOFU fallback); certificates: HOST type, no critical
options, CA signature algorithm allow-list (SHA-1 `ssh-rsa` only with
`AllowSha1CaSignatures`, default off — deviation from the original request,
which listed `ssh-rsa` as supported), `TimeProvider` window `[after, before)`,
**explicit** principal match (empty principals match nothing — stricter than
OpenSSH), CA pinned by exact key match against `@cert-authority`; no CA
covering the host ⇒ "trust this CA for this host?" and on yes append
`@cert-authority <host|[host]:port> <type> <base64>` (the cert and its embedded
key are never pinned). Trust decisions are serialized per `known_hosts` file
and re-evaluated after taking the lock; a failed write fails the connect.

**Not implemented** (deliberate): matching by IP (`CheckHostIP`), `Host`
canonicalisation/aliases, preferring already-known host key algorithms during
negotiation (ssh reorders `HostKeyAlgorithms`; without it a host known only by
one key type may offer another and prompt — `TrustState.Known` documents this).

**Verification.** 371 tests total (0 failed). New: hermetic tests over
checked-in **real `ssh-keygen` artifacts** (`tests/.../Fixtures/OpenSsh/`,
regenerated by `generate.sh`: ed25519/RSA/ECDSA CAs; valid, expired,
not-yet-valid, wrong principal, empty principals, wildcard principal, user
cert as host, wrong CA, tampered signature, critical option, SHA-1 CA; plain,
hashed, `@cert-authority`, `@revoked` known_hosts) with real OpenSSH as an
**oracle** (`ssh-keygen -F` for host matching incl. hashed entries, `ssh-keygen -L`
for certificate fields — skipped if absent); the sentinels above;
real-handshake tests against real `sshd` presenting real host certificates in
the Testcontainers fixture (every CA type × certified key type; trust-this-CA
flow; wrong type/principal/CA/critical option/SHA-1; expired/not-yet-valid;
plain-key TOFU/changed/revoked/CA-required; concurrent prompts; cancel while
prompting); and an env-gated lab-host test (`LabHostOpenSshVerifierTests`,
skips unless `NETFX_LIBVIRT_LAB_SSH_*` is set — **run and passing against the
real lab host**, `srv-l-vm01`, which presents an `ssh-ed25519-cert-v01@openssh.com`
host certificate signed by an ECDSA CA, confirmed via `ssh -vvv`: exactly the
motivating case). The security-critical logic was **mutation-checked** (13 mutations:
CA-covers policy, changed-key type match, empty-principals, any-CA-accepted,
revocation, post-lock re-evaluation, lock removal, negation veto, validity
boundary, HOST-type, critical options, RawKey revocation, append newline — every
one caught after adding a test for the one that survived). An adversarial
security review found no high-confidence issues; it led to hardening: host
names that could alter a `known_hosts` line (whitespace, `,`, `|`, `*`, brackets…)
are refused, and a CA signature algorithm must belong to the CA's key type.

**Follow-ups / backlog only (not built):** KRL files (`RevokedKeys`);
`ssh_config` parsing beyond `UserKnownHostsFile`; `VerifyHostKeyDNS`/SSHFP;
`KnownHostsCommand`; IP-address matching; preferring known host key
algorithms. (A drafted upstream SSH.NET docs contribution — warning that
`HostKeyReceived` consumers must check certificate type/principals/CA, not
just the CA fingerprint — isn't specific to this project, so it's tracked in
the user's personal backlog instead of here; a companion draft proposing
SSH.NET expose the raw certificate bytes was scrapped outright, since this
library already gets them via the public `ConnectionInfo.HostKeyAlgorithms`
factories — see above.)

### 18. Lab-topology cleanliness verification.

**Status: not started.** Before anything from this repo is published more
broadly (story 19), audit everything checked in or emitted for real-lab
specifics that shouldn't travel with a public package: hostnames
(`srv-l-vm01`, `Win2019-Dev-Oni`, `srv-w-app01`, `srv-l-db01`), the account
name (`tetsujinoni`), URIs, and any fingerprint/key material — across
`docs/plan.md`/`docs/status.md`, test fixtures, and source comments. Real
identifiers in *docs*, in prose explaining what was validated against, are
fine and expected (see story 12, story 17); the concern is anything a
published artifact would carry silently — package metadata, embedded
resources, anything under `src/`. Expected outcome given how the repo is
built (see story 17's fixture generation and story 11's Testcontainers
approach): nothing, since real-infra specifics live only in docs and
env-var-gated test parameters — but verify rather than assume before 19.

### 19. Set up NuGet publishing.

**Status: not started. Depends on story 18.** Today `avalonia-virt-manager`
consumes this repo via a sibling `ProjectReference`
(`..\..\..\netfx-libvirt\src\NetfxLibvirt\NetfxLibvirt.csproj`), not a
package — see `docs/status.md`. Needs: package metadata on
`NetfxLibvirt.csproj` (`PackageId`, `Authors`, `Description`, `RepositoryUrl`,
license expression — already MIT, see the licensing commit — README/release
notes inclusion), a version/release strategy, and a publish workflow
(`.github/workflows/`, alongside the existing `publish-test-fixture-image.yml`
pattern) pushing to NuGet.org gated on a tag or release, with the API key as
a repo secret. Consider whether `SSH.NET`'s `BouncyCastle.Cryptography`
transitive dependency needs calling out for consumers who care about that
(this project's own stance: pure managed, no native interop — BouncyCastle is
managed, so consistent with that, see the async-verifier commit's
`docs/status.md` framing).

### 20. `ssh_config` parsing — `Host` alias resolution.

**Status: done.** Picked up out of order (priority request from
`avalonia-virt-manager`, ahead of stories 18–19) — the `ssh_config` gap
flagged as backlog when story 17 landed. `OpenSshConfig`
(`src/NetfxLibvirt/Transport/OpenSsh/OpenSshConfig.cs`): resolves a `Host`
alias to `HostName`/`User`/`Port`/`IdentityFile`s/`IdentitiesOnly`/
`HostKeyAlias`/`UserKnownHostsFile`/`GlobalKnownHostsFile`/
`StrictHostKeyChecking`/`HashKnownHosts`/`ConnectTimeout`, with top-level
`Include` — everything verified directly against real `ssh -F ... -G`
(Windows OpenSSH, the same oracle-testing pattern as story 17's
`ssh-keygen -F`/`-L`), not assumed from `ssh_config(5)`. Full API in
`docs/status.md`.

**Real findings, each locked in by a test:**

- **`ssh_config` `Host` pattern matching is case-***sensitive***, unlike
  `known_hosts`/certificate-principal matching (case-insensitive, confirmed
  in story 17).** Both use the same glob syntax, easy to assume they share
  case rules too — they don't (`Host LAB` doesn't match a query of `lab`,
  confirmed). `OpenSshPattern.Matches`/`MatchesList` gained an `ignoreCase`
  parameter (default `true`, preserving story 17's existing callers) so
  `OpenSshConfig` alone passes `false`. **Follow-up added to this section's
  backlog below: story 17's certificate-principal matching was never
  verified against a real `sshd`/`ssh-keygen -Y` for case sensitivity, only
  assumed case-insensitive by analogy to `known_hosts` — worth confirming
  before relying on it.**
- **`ssh_config` `Host` pattern *lists* are whitespace-separated**
  (`Host *.example !bad.example`), not comma-separated like `known_hosts`'
  host field (confirmed: a literal comma is just part of one pattern, not a
  separator) — re-joined with commas before reusing `OpenSshPattern`.
- **`HostKeyAlias`**: the *correct*, real-OpenSSH mechanism for "use this
  identity for host-key/certificate trust, not whatever name was typed" —
  `OpenSshConfigHost.HostKeyLookupName` surfaces `HostKeyAlias ?? HostName`
  so a consumer doesn't have to know to ask for it.
  `OpenSshHostKeyVerifierOptions.Host`/`Port` must be resolved to this (or
  `HostName`/`Port`), not the raw alias — documented on both records.
- **`IdentityFile` accumulates across every matching `Host` block, in file
  order; every other directive here is first-match-wins** — both confirmed
  against `ssh -G`, matching `ssh_config(5)`'s "first obtained value" rule.
- **`ssh -G` itself has a real bug/quirk on Windows**: given a drive-letter-
  absolute `Include` path (`C:\...`), it silently treats it as relative and
  prepends `~/.ssh/`, so the `Include` always "matches no files" — this
  library's own path handling (`Path.IsPathFullyQualified`) doesn't have
  that problem, so this one case is asserted directly rather than against
  the oracle (documented in the test).
- **`ssh -G` doesn't tilde-expand `IdentityFile`/`GlobalKnownHostsFile`
  (token substitution happens later, at real connect time), but *does*
  expand `UserKnownHostsFile`** — an inconsistency in `ssh -G` itself, not
  something to replicate. This library expands `~` on all three (a consumer
  needs to actually open the file), documented as a deliberate divergence
  from the raw `-G` dump.
- **A latent, unrelated bug found and fixed while verifying against the
  Testcontainers fixture**: `CertificateSshd.ExecAsync` (story 17) built its
  container shell script with a bare `Split('\n')`, which leaves a stray
  `\r` on every line when the `.cs` source itself has CRLF line endings —
  which it does by default on Windows (`core.autocrlf=true`; `.gitattributes`
  only pins `.sh`/Dockerfile/workflow-yml to LF, not `.cs`). A fresh
  `git worktree add` checkout hit this immediately (`sh: 1: set: Illegal
  option -`); fixed with `string.ReplaceLineEndings("\n")` before splitting.
  Any contributor on a fresh Windows clone would have hit the same thing.

**Deliberately not supported** (see `OpenSshConfig`'s class doc for the
individual reasoning): `Match` (any form, including `Match host` — real ssh
evaluates it, this library doesn't, since `Match exec` would mean running
arbitrary shell commands while parsing a config file); `Include` nested
inside a `Host`/`Match` block (real ssh ANDs the included file's own `Host`
patterns with the enclosing block's — subtle enough that approximating it
risked silently misapplying trust-relevant settings); `%`-token expansion
beyond `~`; `ProxyJump`/`ProxyCommand`; `CanonicalizeHostname`;
algorithm-list directives. Malformed values (`Port notanumber`, an
unrecognized `StrictHostKeyChecking`) are diagnosed **at parse time** and
excluded from resolution — matching `OpenSshKnownHosts`' "resilient parse,
diagnostics for a human, never throw on garbage input, never half-apply a
bad line" precedent, not real `ssh`'s hard-fail-on-bad-config behavior.

**Verification.** 423 tests total, 0 failed (7 skipped: the pre-existing
manually-gated ones). New `OpenSshConfigTests` cross-checked against real
`ssh -G` wherever the two systems' representations are actually comparable
(documented in the test class where they aren't — e.g. `ssh -G` always
prints a *resolved* `User`, defaulting to the local account, where this
library returns `null` for "the config didn't set anything"). Mutation-
checked the security-relevant logic (first-match-wins vs. cumulative
semantics, `HostKeyAlias` fallback, the case-sensitivity fix, nested-Include
rejection, parse-time value validation, the `Include` cycle guard — removing
the cycle guard entirely crashed the test host via real unbounded recursion,
confirming it's load-bearing, not just a nice-to-have). An adversarial
security review (of `ssh_config` specifically — story 17's own review isn't
re-litigated here) found nothing above the confidence bar for a finding;
see `docs/status.md`.

**Backlog:** confirm `SshHostCertificate`'s principal matching case-
sensitivity against a real `sshd` (currently case-insensitive, assumed by
analogy to `known_hosts`, never independently verified — flagged above);
`Match` support (would need a policy for what, if anything, `Match exec` is
allowed to do); scoped `Include`.

**Addendum — multi-key auth (same day).** `avalonia-virt-manager` (building
the host-list feature this story feeds) reported hitting this for real: with
no explicit `IdentityFile`, `ssh_config`'s real candidate list is several
keys tried *in order against the server*, not "pick whichever one happens to
exist locally first" — their stopgap app-side workaround (open a whole new
SSH connection per candidate on auth failure) was correct but costly (a full
extra TCP+KEX+auth round trip per miss) and made their real-lab-host test
suite flakier under connection-burst load.

Two real fixes landed from this, both found by checking against `ssh -G`
rather than assumed:

- **`OpenSshConfig.Resolve` was actually missing ssh's own default
  `IdentityFile` candidate list.** Confirmed: `ssh -G` lists `id_rsa`,
  `id_ecdsa`, `id_ecdsa_sk`, `id_ed25519`, `id_ed25519_sk` even with *no*
  `IdentityFile` directive in the config at all — this library returned an
  empty list instead. Fixed: falls back to that exact list (filtered to
  files that exist locally — confirmed real ssh does the same for its
  *implicit* defaults, but does **not** filter an *explicitly* configured
  `IdentityFile` the same way, so a typo there stays visible rather than
  silently vanishing).
- **`SshTransportOptions` gained `PrivateKeyPaths`** (plural, alongside the
  existing singular `PrivateKeyPath` — mutually exclusive, same "which one
  should decide?" `ArgumentException` pattern as `VerifyHostKey`/
  `VerifyHostKeyAsync`). `BuildAuthenticationMethod` passes every path as
  its own `IPrivateKeySource` to **one** `PrivateKeyAuthenticationMethod` —
  SSH.NET already supports offering multiple keys within a single session
  (`params IPrivateKeySource[] keyFiles`), exactly like real `ssh`; this
  just exposes it, so a consumer wiring `OpenSshConfigHost.IdentityFiles`
  straight through no longer needs the reconnect-per-candidate workaround.

10 new tests (433 total, 0 failed): the default-candidate list itself
(hermetic, via a testable `DefaultIdentityFilesUnder(directory)` seam — the
real `%USERPROFILE%\.ssh` contents aren't asserted directly, since that's
not portable across machines; a separate test confirms `Resolve()` is wired
to it by comparing against that seam's own answer for the real profile
directory, whatever it is), that an explicit `IdentityFile` is never
existence-filtered, and `BuildAuthenticationMethod`'s single-key/multi-key/
ambiguous/password-fallback paths (using the real checked-in `host_ed25519`/
`attacker_ed25519` fixture keys, not fakes). Mutation-checked (dropping the
ambiguity check, dropping the existence filter, and disabling the fallback
entirely were all initially uncaught — each got a test).
