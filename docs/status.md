# netfx-libvirt — Status

**Last updated:** 2026-09-16 (plan stories 1–5 done — see `docs/plan.md`)

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
    `src/rpc/virnetprotocol.x` (vendored in `reference/upstream-x/`, see its
    README for exact source commits). `VirNetMessageFraming` reads/writes
    over any `Stream`, so it's transport-agnostic by construction — the same
    code will serve the Unix-socket, TCP, TLS, and SSH transports.
  - `Rpc/VirNetRpcClient` — the request/reply engine: sends a `Call` frame
    with an auto-incrementing serial number, reads back the matching
    `Reply`, and either returns the raw payload or throws
    `Rpc/LibvirtRpcException` (decoded from the generated `RemoteError` DTO)
    on a `VIR_NET_ERROR` reply. Half-duplex by design — see its class doc.
  - `LibvirtConnection.OpenAsync` — the AUTH_LIST + CONNECT_OPEN handshake
    every libvirt client needs, over any already-connected `Stream`. Only
    `RemoteAuthType.RemoteAuthNone` is supported so far (throws
    `NotSupportedException` naming what libvirtd actually offered
    otherwise) — matches every transport this parity milestone needs.
- [`tests/NetfxLibvirt.Tests`](../tests/NetfxLibvirt.Tests) — xUnit v3, 105
  tests, all passing, fully hermetic (no network, no real libvirtd). Covers
  every XDR primitive by byte-literal assertion *and* round-trip, the real
  `REMOTE_PROC_AUTH_LIST` call header byte-for-byte, message framing
  (length-prefix math, multi-frame streams, truncation, oversized-payload
  rejection), the RPC call engine and connection handshake over a small fake
  duplex `Stream` (`Rpc/FakeDuplexStream`), and a sample of the generated
  procedure DTOs round-tripped through real `XdrWriter`/`XdrReader`.
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
  and `Semantics/RealModuleTests` run the parser/resolver against the real
  vendored `.x` files end to end (not just synthetic fixtures) and lock in
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
  `virnetprotocol.x` and `remote_protocol.x`, fetched from
  `libvirt/libvirt@master`.
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
| RPC call engine | `Rpc/VirNetRpcClient` + `Rpc/LibvirtRpcException` — call/reply correlation by serial number, error decoding from the real `remote_error` wire shape. Hermetic tests over a fake duplex `Stream`. |
| Auth + open handshake | `LibvirtConnection.OpenAsync` — `AUTH_LIST` then `CONNECT_OPEN`; rejects any auth mechanism besides `AuthNone` with a named `NotSupportedException` rather than guessing. Hermetic tests. |

## Validation so far

Everything above is validated against real upstream source (the exact `.x`
protocol definitions and `go-libvirt`'s own wire-level code), not guessed or
inferred from documentation prose alone — see `reference/README.md` for
exact commits. **Not yet validated against a real running `libvirtd`** — no
transport exists yet, so there's nothing to connect with. That's the next
milestone, and per this project's own validation standard (mirroring the
SPICE spike's), no procedure-encoding work should be trusted until it's been
exercised against a real lab hypervisor, not just synthetic bytes.

## Next ready work

See [`docs/plan.md`](plan.md) for the live backlog — a dependency-ordered
list of small (2-point) stories. Current phase: reach feature parity with
the sibling Go/Wails app `virt-desktop`'s `hypervisor.go`, traced from its
actual source (not memory) to the exact RPC procedures it calls. Broader
`remote_protocol.x` coverage (toward go-libvirt-level breadth) and the
TCP/TLS transports are real goals but come *after* that parity milestone —
see `plan.md`'s "After parity" section.

## Out of scope for now

Full libvirt API coverage, storage pool/network management, VM
creation/provisioning, any UI. See
`docs/libvirt-netfx-feasibility.md` in the sibling `uwp-virt-manager` repo
for the full scope rationale.
