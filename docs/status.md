# netfx-libvirt — Status

**Last updated:** 2026-09-16 (plan stories 1–11 done and validated against real infra — see `docs/plan.md`)

## What this is

A pure managed C#/.NET reimplementation of libvirt's RPC client protocol —
no P/Invoke, no native `libvirt.so`/`libvirt-0.dll` dependency. Same
methodology as `digitalocean/go-libvirt` (Apache 2.0, pure Go, no cgo): read
libvirt's real upstream `.x` XDR protocol files and port the algorithm, not
the C code. See `docs/libvirt-netfx-feasibility.md` and
`docs/full-admin-tool-gap-analysis.md` in the sibling `uwp-virt-manager`
repo (`D:\work\uwp-virt-manager`) for the full research/decision record and
motivation behind this project — this repo is a standalone spin-off, not a
subdirectory of that one.

## What exists

Solution `netfx-libvirt.slnx` with four projects:

- [`src/NetfxLibvirt`](../src/NetfxLibvirt) — the library.
  - `Xdr/{XdrWriter,XdrReader,XdrException}` — a from-scratch RFC 4506 XDR
    codec: big-endian 32/64-bit ints, bool, enum, float/double, fixed and
    variable-length opaque data (4-byte padding), strings, fixed and
    variable-length arrays, and XDR "optional-data" (the pointer-type
    pattern used throughout `remote_protocol.x`).
  - `Rpc/{VirNetMessageType,VirNetMessageStatus,VirNetMessageHeader,VirNetMessageFraming}` —
    the 24-byte `virNetMessageHeader` wire struct and the 4-byte
    length-prefixed message framing, both ported directly from libvirt's own
    `src/rpc/virnetprotocol.x` (fetched at a pinned commit into
    `reference/upstream-x/` via `reference/fetch-upstream-x.sh`, not
    committed — see that folder's README for why and the exact source
    commits). `VirNetMessageFraming` reads/writes
    over any `Stream`, so it's transport-agnostic by construction — the same
    code will serve the Unix-socket, TCP, TLS, and SSH transports.
  - `Rpc/VirNetRpcClient` — the request/reply engine: sends a `Call` frame
    with an auto-incrementing serial number, reads back the matching
    `Reply`, and either returns the raw payload or throws
    `Rpc/LibvirtRpcException` (decoded from the generated `RemoteError` DTO)
    on a `VIR_NET_ERROR` reply. One call in flight at a time, but tolerant
    of unsolicited `Message`/`Stream` frames interleaved with a reply — see
    its class doc and `docs/plan.md`'s Events story.
  - `LibvirtConnection` — the full session surface, deliberately matching
    `virt-desktop`'s own `hypervisorAPI` interface (`hypervisor.go`):
    `OpenAsync` (AUTH_LIST + CONNECT_OPEN; only `AuthNone` supported, named
    `NotSupportedException` otherwise), `ListDomainsAsync`,
    `StartDomainAsync`/`ShutdownDomainAsync`/`DestroyDomainAsync`,
    `GetDomainXmlAsync`, `DisconnectAsync` (graceful `CONNECT_CLOSE`,
    idempotent), and a best-effort `DisposeAsync`.
  - `Transport/UnixSocketTransport` — connects to a local libvirtd's Unix
    domain socket via `System.Net.Sockets.Socket` +
    `UnixDomainSocketEndPoint`. This project's first real transport.
  - `Transport/SshTransport` — connects to a remote libvirtd over SSH,
    built on **`SSH.NET`** (originally `Microsoft.DevTunnels.Ssh`; swapped
    2026-09-17 once real-host validation needed Ed25519 auth, which
    DevTunnels.Ssh's key importer doesn't support — full reasoning in
    `docs/plan.md` story 11). Doesn't replicate `virt-desktop`'s
    `direct-streamlocal` channel-open; instead execs libvirt's own
    `virt-ssh-helper <uri>` over a plain SSH exec channel (a non-PTY
    `SshCommand`, not `ShellStream`) — libvirt's own official mechanism
    (confirmed by reading `src/remote/remote_ssh_helper.c`), and the most
    universally-supported SSH capability there is. Requires an explicit
    `VerifyHostKey` callback — no silent-trust default.
  - `ConnectListAllDomainsFlags`, `DomainState` — small hand-written enums
    for libvirt.h public API constants that have no entry in
    `remote_protocol.x` (same situation as `VIR_UUID_BUFLEN`, see
    `XdlConstantTable`'s doc), values cross-checked against go-libvirt's
    `const.gen.go`.
- [`tests/NetfxLibvirt.Tests`](../tests/NetfxLibvirt.Tests) — xUnit v3, 125
  tests. 119 run with **no special setup beyond Docker being available**:
  115 fully hermetic (every XDR primitive by byte-literal assertion *and*
  round-trip, the real `REMOTE_PROC_AUTH_LIST` call header byte-for-byte,
  message framing, the RPC call engine and every `LibvirtConnection`
  operation over a small fake duplex `Stream`, generated procedure DTOs
  round-tripped through real `XdrWriter`/`XdrReader`) plus 4
  (`Integration/SshLibvirtdIntegrationTests`) that run for real over SSH
  against a throwaway libvirtd+sshd container this project builds itself
  (`Integration/docker/`, via `Testcontainers` — see `docs/plan.md`'s
  transport section) every time the suite runs — no WSL, no manual host
  setup, works identically for any contributor and in CI. The remaining 6
  (`Integration/LibvirtdIntegrationTests`, the local Unix-socket transport)
  still need a manually-configured real libvirtd (env-var gated, skip by
  default) — not yet migrated to the container approach; see `plan.md`
  story 11 for why.
- [`tools/NetfxLibvirt.ProtocolGen`](../tools/NetfxLibvirt.ProtocolGen) — the
  codegen tool, three layers:
  - `Lexing/XdlLexer` + `Parsing/XdlParser` — hand-written recursive-descent
    parser for libvirt's `.x` grammar (the same SunRPC/XDR + RPCL grammar as
    go-libvirt's `internal/lvgen/sunrpc.y`, a goyacc/LALR grammar mirrored
    under `reference/go-libvirt-src/sunrpc.y` and used as ground truth — not
    linked, read only), producing an immutable, *unresolved* AST (`Ast/Xdl*.cs`).
  - `Semantics/{XdlModuleBuilder,XdlTypeResolver,XdlConstantTable}` — resolves
    that AST: folds `const` definitions to actual integers (including a
    documented table of constants libvirt's `.x` files reference but never
    define themselves, e.g. `VIR_UUID_BUFLEN` — see `XdlConstantTable`'s doc),
    follows typedef chains to a closed shape algebra (`XdlTypeShape` — fixed/
    bounded opaque, bounded string, struct/enum/union reference, fixed/bounded
    list, optional), and extracts the RPC procedure list from any `*_PROC_*`
    enum with each procedure's `_args`/`_ret` struct names resolved by
    libvirt's own naming convention (not by anything in the grammar — see
    `XdlProcedure`'s doc).
  - `Emission/{CSharpTypeEmitter,XdlFieldEmitter,XdlClosureCollector,CSharpModuleEmitter}` —
    the actual C# emitter, built on **Roslyn** (`Microsoft.CodeAnalysis.CSharp`)
    rather than string templates: the class/property/method/namespace skeleton
    is assembled from typed `SyntaxFactory` nodes (structurally can't be
    malformed), and every leaf fragment (an encode statement, a decode
    expression, a property type) is parsed and diagnostic-checked before
    being spliced in, so a bad fragment fails loudly at generation time
    instead of as a mystery `dotnet build` error later. Taken on deliberately
    despite the dependency weight, because this generator is headed toward
    go-libvirt-level (~200+ procedure) coverage, not staying at today's
    parity-milestone procedure set — see `CSharpTypeEmitter`'s class doc for
    the full rationale.
  - `Program.cs` — the CLI entry point: regenerates
    `src/NetfxLibvirt/Generated/Remote/` from
    `reference/upstream-x/remote_protocol.x`. Run with
    `dotnet run --project tools/NetfxLibvirt.ProtocolGen`.
  - Not a library dependency — a dev-time tool, kept out of `src/`.
- [`src/NetfxLibvirt/Generated/Remote`](../src/NetfxLibvirt/Generated/Remote) —
  the emitter's checked-in output for the `virt-desktop`-parity procedure set
  (`docs/plan.md`): 22 files — the `RemoteProcedure` number registry,
  `RemoteProtocolConstants` (`REMOTE_PROGRAM`/`REMOTE_PROTOCOL_VERSION`), 2
  shared structs (`RemoteNonnullDomain`, `RemoteNonnullNetwork`), 1 enum
  (`RemoteAuthType`), `RemoteError`, and 17 procedure args/ret DTOs — each a
  sealed class (or plain enum) with `Encode(XdrWriter)`/`static Decode(XdrReader)`.
  Regenerate with the CLI above after any upstream `.x` refresh; do not
  hand-edit (`// <auto-generated>`).
- [`tests/NetfxLibvirt.ProtocolGen.Tests`](../tests/NetfxLibvirt.ProtocolGen.Tests) —
  126 tests across all three layers. Highlights: `Parsing/RealProtocolFileTests`
  and `Semantics/RealModuleTests` run the parser/resolver against the real,
  fetched `.x` files end to end (not just synthetic fixtures) and lock in
  the parity procedure set's exact numbers and args/ret struct names;
  `Emission/CSharpTypeEmitterTests` re-parses every emitted sample with
  Roslyn and asserts it's diagnostic-free — the emitter's own regression
  guard. Two real bugs this caught before they became `dotnet build`
  failures: (1) bare `unsigned` (XDR shorthand for `unsigned int`, used by
  `virNetMessageHeader`) isn't in go-libvirt's own grammar, because
  go-libvirt's generator only ever parses `remote_protocol.x`, which always
  spells it out — fixed in `XdlParser`; (2) the enum emitter's first pass
  emitted `long`-suffixed literals (`0L`) as C#'s default `int`-backed enum
  member values, which doesn't compile except for the special zero case —
  fixed in `CSharpTypeEmitter`.
- [`tests/NetfxLibvirt.Tests/Generated`](../tests/NetfxLibvirt.Tests/Generated) —
  round-trips a sample of the generated DTOs through real `XdrWriter`/`XdrReader`
  (`RemoteProcedureDtoTests`): proof the emitter's field-shape-to-wire-call
  mapping is correct, not just that the output compiles.
- [`reference/upstream-x`](../reference/upstream-x) — real, unmodified
  `virnetprotocol.x` and `remote_protocol.x`. Fetched at a pinned commit via
  `reference/fetch-upstream-x.sh`, not committed to this repo (both files
  are Red Hat's LGPL-2.1-or-later source; see that folder's README for why
  redistributing them verbatim alongside this project's own MIT code was
  worth avoiding — decided 2026-09-18, superseding straight vendoring).
- [`reference/go-libvirt-src`](../reference/go-libvirt-src) — individual
  files mirrored from `digitalocean/go-libvirt@main`, used to resolve wire
  details the `.x` files alone leave ambiguous (see its README — this is
  where the length-prefix-includes-itself semantics was confirmed).

## Completed

| Task | What was built |
|---|---|
| Repo setup | Solution skeleton mirroring `uwp-virt-manager`'s own pattern (xUnit v3 + Microsoft.Testing.Platform, `global.json` pinning the MTP test runner). |
| XDR runtime | `XdrWriter`/`XdrReader` — RFC 4506 encode/decode, unit-tested exhaustively (byte-literal + round-trip), including bounds checking against a declared-length-exceeds-remaining-bytes attack (a length claiming more data than the buffer actually holds throws rather than over-reading). |
| `virNetMessageHeader` | `VirNetMessageHeader` struct, `VirNetMessageType`/`VirNetMessageStatus` enums — values confirmed byte-exact against the real upstream `.x` file, not guessed. |
| Message framing | `VirNetMessageFraming.{EncodeFrame,WriteFrameAsync,ReadFrameAsync}` — confirmed against `go-libvirt`'s own `socket.go` that the 4-byte length prefix counts itself, not just header+payload. |
| `.x` grammar parser | `NetfxLibvirt.ProtocolGen`'s `XdlLexer`/`XdlParser` — full SunRPC/XDR + RPCL grammar, cross-checked against go-libvirt's own `sunrpc.y`/`lvlexer.go` and proven by parsing the real `virnetprotocol.x` and `remote_protocol.x` end to end. |
| Semantic resolver | `Semantics/{XdlModuleBuilder,XdlTypeResolver,XdlConstantTable}` — const folding, typedef-chain resolution to a closed shape algebra, procedure/args/ret extraction. Proven against the real file for the full `virt-desktop`-parity procedure set. |
| C# emitter (parity procedure set) | `Emission/*`, Roslyn-based — emits `src/NetfxLibvirt/Generated/Remote/*.cs` (22 files). Compiles as part of the real library build and round-trips real bytes in `tests/NetfxLibvirt.Tests/Generated`. |
| RPC call engine | `Rpc/VirNetRpcClient` + `Rpc/LibvirtRpcException` — call/reply correlation by serial number, error decoding from the real `remote_error` wire shape. |
| Auth + open handshake | `LibvirtConnection.OpenAsync` — `AUTH_LIST` then `CONNECT_OPEN`; rejects any auth mechanism besides `AuthNone` with a named `NotSupportedException` rather than guessing. |
| Local Unix-socket transport | `Transport/UnixSocketTransport`. **This project's first connection to a real, running libvirtd** — see Validation below. |
| Domain operations | `LibvirtConnection.{ListDomainsAsync,StartDomainAsync,ShutdownDomainAsync,DestroyDomainAsync,GetDomainXmlAsync,DisconnectAsync}` — full parity with `virt-desktop`'s `hypervisorAPI`. |
| SSH transport | `Transport/SshTransport` on `SSH.NET` — execs libvirt's own `virt-ssh-helper` over a plain SSH exec channel rather than replicating `virt-desktop`'s `direct-streamlocal` channel-open. Validated against a real, self-built container — see Validation below. |
| Container-based real-infra tests | `Integration/{docker,LibvirtdContainerFixture}` — a small self-authored image (Ubuntu 22.04 + `libvirt-daemon-system` + `openssh-server`, no qemu/kvm, no `--privileged`) built and started via `Testcontainers` on every test run. Zero host configuration; works the same for any contributor and in CI. |

## Validation so far

Protocol/codegen work is validated against real upstream source (the exact
`.x` protocol definitions and `go-libvirt`'s own wire-level code), not
guessed or inferred from documentation prose alone — see
`reference/README.md` for exact commits, including a compatibility
bisection against the real libvirt 8.0.0 (Ubuntu 22.04 LTS) tag.

**Validated against a real running `libvirtd`, 2026-09-16.** Per this
project's own validation standard (mirroring the SPICE spike's), no
procedure-encoding work should be trusted until it's been exercised against
a real hypervisor, not just synthetic bytes — done: `UnixSocketTransport` +
`LibvirtConnection`'s full operation surface all ran against WSL2 Ubuntu
22.04's real `libvirtd` (`test:///default`, chosen specifically so this
validation needs no virtualization capability and stays reproducible by any
contributor — see `docs/plan.md`'s transport section for the full
reasoning and how to re-run it). 6/6 integration tests passed: open, list
domains, get XML, destroy + restart with observed state transitions,
disconnect, and a real unknown-domain error decoding into
`LibvirtRpcException`.

**SSH transport validated against real infrastructure, 2026-09-16 — with
zero host configuration.** The original plan (install `openssh-server` in
WSL by hand) only works on one machine and does nothing for a future OSS
contributor or CI — pivoted to `Testcontainers` instead: a small
self-authored image (`tests/NetfxLibvirt.Tests/Integration/docker/`, no
qemu/kvm, no `--privileged` — these tests only touch libvirt's
`test:///default` null-hypervisor driver) built and started fresh on every
test run, needing nothing but a working Docker daemon. Running it for real
caught a genuine bug immediately: the first test key used ed25519, and
(at the time) `Microsoft.DevTunnels.Ssh.Keys`' OpenSSH importer only
supported RSA/ECDSA — every test failed with a clear, specific exception.
Regenerated as ECDSA P-256; all 4 `SshLibvirtdIntegrationTests` then
passed against the real container: open, list domains, get XML,
disconnect. These now run **by default** (no env var, no manual setup)
whenever `dotnet test` runs on a machine with Docker.

**SSH library swapped to `SSH.NET`, 2026-09-17.** Real-host validation
(story 12) needed to authenticate as the actual lab account, whose only
key is Ed25519 — the same gap the test fixture's key hit above, this time
with no workaround available (it's not a key this project generates).
`Microsoft.DevTunnels.Ssh` has zero Ed25519 or OpenSSH-certificate support
anywhere in its source, confirmed directly; a mergeable, CLA-signed
upstream PR adding it has sat with zero maintainer engagement for 6+
months and doesn't even touch its .NET side. Swapped to `SSH.NET` (over
`Tmds.Ssh`, on the same supply-chain-concentration reasoning that ruled it
out originally) rather than forking DevTunnels.Ssh for capability
(multi-channel/interactive sessions) this project never uses. Full test
suite passed unchanged after the swap: 245/0/6, same as before — see
`docs/plan.md` story 11 for the full trail.

## Next ready work

See [`docs/plan.md`](plan.md) for the live backlog — a dependency-ordered
list of small (2-point) stories. Stories 1–11 (parity codegen, RPC engine,
the full `LibvirtConnection` operation surface, and both the local
Unix-socket and remote SSH transports) are done and proven against real
infra. Next: story 12, end-to-end validation against the real lab host —
the actual parity finish line. Also tracked as a follow-up (not blocking):
migrating `LibvirtdIntegrationTests` (the local Unix-socket transport) to
the same Testcontainers approach story 11 landed — deferred because it can
only be proven from a Linux-kernel host (Linux CI or WSL), which this
session couldn't verify directly. Broader `remote_protocol.x` coverage
(toward go-libvirt-level breadth) and the TCP/TLS transports come *after*
story 12 — see `plan.md`'s "After parity" section.

## Out of scope for now

Full libvirt API coverage, storage pool/network management, VM
creation/provisioning, any UI. See
`docs/libvirt-netfx-feasibility.md` in the sibling `uwp-virt-manager` repo
for the full scope rationale.
