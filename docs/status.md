# netfx-libvirt — Status

**Last updated:** 2026-09-16

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
- [`tests/NetfxLibvirt.Tests`](../tests/NetfxLibvirt.Tests) — xUnit v3, 85
  tests, all passing, fully hermetic (no network, no real libvirtd). Covers
  every XDR primitive by byte-literal assertion *and* round-trip, the real
  `REMOTE_PROC_AUTH_LIST` call header byte-for-byte, and message framing
  (length-prefix math, multi-frame streams, truncation, oversized-payload
  rejection) over an in-memory `Stream`.
- [`tools/NetfxLibvirt.ProtocolGen`](../tools/NetfxLibvirt.ProtocolGen) — the
  `.x` grammar parser (codegen's front end). Hand-written recursive-descent
  `Lexing/XdlLexer` + `Parsing/XdlParser` implementing the same SunRPC/XDR +
  RPCL grammar as go-libvirt's `internal/lvgen/sunrpc.y` (a goyacc/LALR
  grammar mirrored under `reference/go-libvirt-src/sunrpc.y` and used as
  ground truth — not linked, read only), producing an immutable AST
  (`Ast/Xdl*.cs`): const/enum/typedef/struct/union/program definitions, all
  four declaration shapes (simple, fixed array, variable array,
  optional/pointer). Not a library dependency — a dev-time tool, kept out of
  `src/`.
- [`tests/NetfxLibvirt.ProtocolGen.Tests`](../tests/NetfxLibvirt.ProtocolGen.Tests) —
  81 tests. Lexer and parser unit tests against synthetic snippets, plus
  `Parsing/RealProtocolFileTests` — this project's validation standard
  applied to grammar work: parses the real vendored `virnetprotocol.x` and
  `remote_protocol.x` end to end (not just synthetic fixtures), asserts the
  real `virNetMessageHeader` field shape, `REMOTE_PROGRAM`/
  `REMOTE_PROTOCOL_VERSION` const values, and all 9 MVP procedure numbers.
  This caught a real bug a synthetic-only test suite missed: bare `unsigned`
  (XDR shorthand for `unsigned int`, used by `virNetMessageHeader`'s `prog`/
  `vers`/`serial` fields) isn't in go-libvirt's own grammar, because
  go-libvirt's generator only ever parses `remote_protocol.x`, which always
  spells out `unsigned int`/`unsigned hyper`. Fixed in `XdlParser`.
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
| `.x` grammar parser | `NetfxLibvirt.ProtocolGen`'s `XdlLexer`/`XdlParser` — full SunRPC/XDR + RPCL grammar, cross-checked against go-libvirt's own `sunrpc.y`/`lvlexer.go` and proven by parsing the real `virnetprotocol.x` and `remote_protocol.x` end to end (81 tests, `RealProtocolFileTests`). |

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

1. **A semantic layer over the raw `.x` AST**: resolve `XdlDeclaration`
   type-name strings (currently free-text — primitive keywords, or the name
   of another definition) into typed references, fold `const` definitions so
   array bounds resolve to actual numbers, and pull the MVP procedure set
   (below) out of `remote_procedure`'s ~200+ members. This is the layer the
   actual C# emitter will walk — the parser (done) only produces a flat,
   unresolved AST on purpose.
2. **The C# emitter** — walk the resolved MVP procedure set and emit
   `XdrWriter`/`XdrReader` call/reply (de)serialization code matching the
   hand-shape already proven in `tests/NetfxLibvirt.Tests` for
   `REMOTE_PROC_AUTH_LIST`'s call header. MVP set (all confirmed against the
   real `remote_protocol.x`, `REMOTE_PROGRAM = 0x20008086`,
   `REMOTE_PROTOCOL_VERSION = 1`, and now locked in by
   `RealProtocolFileTests`): `REMOTE_PROC_CONNECT_OPEN` (1),
   `REMOTE_PROC_AUTH_LIST` (66), `REMOTE_PROC_CONNECT_GET_CAPABILITIES` (7),
   `REMOTE_PROC_CONNECT_LIST_ALL_DOMAINS` (273),
   `REMOTE_PROC_DOMAIN_GET_INFO` (16), `REMOTE_PROC_DOMAIN_GET_XML_DESC` (14),
   `REMOTE_PROC_DOMAIN_CREATE` (9), `REMOTE_PROC_DOMAIN_SHUTDOWN` (33),
   `REMOTE_PROC_DOMAIN_DESTROY` (12).
3. **Local (Unix socket) transport** — simplest of the four, good first
   proof that `VirNetMessageFraming` actually round-trips against a real
   `libvirtd`. WSL has a real libvirtd reachable; use it before the TCP/TLS/
   SSH transports. Doesn't strictly depend on 1–2 (framing is procedure-
   agnostic) — could be pulled forward if proving the transport layer first
   is more valuable than finishing codegen.
4. **SSH transport via SSH.NET** — probably the most valuable to prove early
   given the actual deployment pattern (`qemu+ssh://root@<host>/system`, per
   the sibling SPICE project's lab environment).
5. TCP and TLS transports.

## Out of scope for now

Full libvirt API coverage, storage pool/network management, VM
creation/provisioning, any UI. See
`docs/libvirt-netfx-feasibility.md` in the sibling `uwp-virt-manager` repo
for the full scope rationale.
