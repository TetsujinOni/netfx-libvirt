# Reference material

Vendored/mirrored upstream sources used as ground truth while building this
client — never linked into the shipped library, read (and eventually
machine-parsed, for `upstream-x`) as a reference only.

## `upstream-x/`

The real, unmodified libvirt RPC protocol definitions, at a pinned commit
originally fetched from `libvirt/libvirt@master` on 2026-09-15:

| File | Source commit (last touching that path) |
|---|---|
| `virnetprotocol.x` | [`fc920f7`](https://github.com/libvirt/libvirt/commit/fc920f704c19c27ce60a47f2788d40b30ea0d268) |
| `remote_protocol.x` | [`6924b73`](https://github.com/libvirt/libvirt/commit/6924b73d30d126ede2f6496615c666ce686462b5) |

`virnetprotocol.x` defines the 24-byte `virNetMessageHeader` and framing
(ported by hand into `src/NetfxLibvirt/Rpc/`). `remote_protocol.x` is what
`tools/NetfxLibvirt.ProtocolGen` mechanically parses to emit
`src/NetfxLibvirt/Generated/Remote`.

**Fetched at build/dev time, not committed to this repo** (2026-09-18,
superseding the original vendoring above). Both files carry their own
`Copyright (C) Red Hat, Inc.` / LGPL-2.1-or-later header — real, checked
directly in the files themselves, not assumed. Reimplementing a protocol
by parsing a specification file, without copying its own source into what
you ship, is generally understood not to create an LGPL-derivative work of
the specification (and `go-libvirt` — Apache-2.0 — has operated on exactly
that basis for years, generating from these same files without any LGPL
entanglement in its own output). But *redistributing the specification file
itself* verbatim, inside a repo otherwise licensed MIT, is a different and
easily-avoidable thing to also be doing — confirmed directly against
`go-libvirt`'s own generator (`internal/lvgen/gen/main.go`): it never
vendors/commits the `.x` files either, requiring a `$LIBVIRT_SOURCE` env
var pointing at a separately-obtained local libvirt checkout instead, read
only at generation time. This project does the equivalent via a pinned-
commit fetch (better suited to running in GitHub Actions than requiring a
full local libvirt checkout, and libvirt itself moved to GitLab CI + Meson
some time ago — a different enough CI ecosystem that mirroring its own
tooling wasn't the right fit either): run

```bash
reference/fetch-upstream-x.sh
```

once after cloning (or whenever the pinned commits above change) before
running the generator or `NetfxLibvirt.ProtocolGen.Tests`'
`RealProtocolFileTests`/`RealModuleTests` — both skip cleanly, not fail, if
the fetch hasn't happened yet. A future CI workflow that runs
`dotnet test` needs to run this script first for those two test classes to
actually exercise anything real rather than skip.

### Compatibility bisection: master vs. libvirt v8.0.0 (2026-09-16)

Why: our WSL validation environment (`docs/plan.md` story 6) runs libvirt
8.0.0 — the version Ubuntu 22.04 LTS ships, and a reasonable proxy for the
oldest still-common enterprise fleet member this project needs to not have
quirks against. Rather than assume master-vendored `.x` files are safe to
validate against an older real daemon, diffed both files against the real
[`v8.0.0` tag](https://github.com/libvirt/libvirt/tree/v8.0.0):

- `virnetprotocol.x`: **byte-identical**. The framing/header layer every
  transport depends on hasn't changed at all since 8.0.0.
- `remote_protocol.x`: 254 added lines / 11 changed lines over ~4 years.
  The additions are new procedures appended at the end
  (`REMOTE_PROC_DOMAIN_SAVE_PARAMS` = 440 onward) plus a few new `const`s
  and doc/ACL-annotation additions to existing procedures' metadata
  comments — never a renumbering. The 11 changed lines are all the same
  field rename (`unsigned hyper resource;` → `bandwidth;`, same type),
  confined to `remote_domain_migrate_*` structs. **Zero changes anywhere
  touch any procedure or struct this project currently generates**
  (`src/NetfxLibvirt/Generated/Remote`).

Conclusion: libvirt's RPC procedures are additive-only and never
renumbered/removed — there's no real "pick an old vs. new baseline"
tradeoff, since master is a strict superset of what an 8.0.0-era daemon
needs. The actual compatibility risk is calling a procedure a specific
daemon doesn't implement *yet* (something newer than what it ships), which
libvirt itself surfaces as an ordinary `VIR_NET_ERROR` reply — exactly what
`LibvirtRpcException`/`RemoteError` already decode. The design implication:
treat "unknown procedure" as an expected, catchable failure mode as the
emitted surface grows, not something to version-pin around.

## `go-libvirt-src/`

Individual files mirrored from `digitalocean/go-libvirt@main` (Apache 2.0),
read to confirm exact wire-level behavior the `.x` files alone leave
ambiguous — algorithm reference only, never compiled or linked here.

| File | Source commit (last touching that path) | Why |
|---|---|---|
| `rpc.go` | [`174fc63`](https://github.com/digitalocean/go-libvirt/commit/174fc63aebd4ef790d76bc26e4c7840bc58803ac) | Call/reply/event routing shape. |
| `socket/socket.go` | [`761cfee`](https://github.com/digitalocean/go-libvirt/commit/761cfeeb596863a450ad7e6f5fff373b82f3f4e3) | Confirmed the length-prefix semantics: the 4-byte `Len` field counts *itself* plus the header plus the payload (`packet{ Len uint32; Header }`, `p.Len = sizeof(Len)+sizeof(Header)+len(payload)`) — this is what `VirNetMessageFraming` in this repo implements. |
| `socket/units.go` | (same package) | Size constants referenced alongside `socket.go`. |
| `sunrpc.y` | [`1a83157`](https://github.com/digitalocean/go-libvirt/commit/1a83157e18586d0cd4638e102f0c48a1b1785040) (HEAD of a depth-1 clone taken 2026-09-16 — not necessarily the exact last-touching commit; refetch and re-resolve if precision matters) | The goyacc/LALR grammar for the SunRPC/XDR + RPCL `.x` language, from `internal/lvgen/`. Ground truth for `tools/NetfxLibvirt.ProtocolGen`'s hand-written recursive-descent `XdlParser` — same grammar, different parsing strategy (C# has no goyacc equivalent, and the grammar has no ambiguity that needs one). |
| `lvlexer.go` | (same commit) | The lexer paired with `sunrpc.y` — a Rob Pike-style state-machine scanner. Ground truth for `XdlLexer`, including the `<PROGRAM>_PROC_<NAME>` procedure-identifier heuristic and the `%`-directive-line skip. |

Refetch with:

```bash
curl -fsSL https://raw.githubusercontent.com/digitalocean/go-libvirt/main/rpc.go -o go-libvirt-src/rpc.go
curl -fsSL https://raw.githubusercontent.com/digitalocean/go-libvirt/main/socket/socket.go -o go-libvirt-src/socket.go
curl -fsSL https://raw.githubusercontent.com/digitalocean/go-libvirt/main/socket/units.go -o go-libvirt-src/units.go
curl -fsSL https://raw.githubusercontent.com/digitalocean/go-libvirt/main/internal/lvgen/sunrpc.y -o go-libvirt-src/sunrpc.y
curl -fsSL https://raw.githubusercontent.com/digitalocean/go-libvirt/main/internal/lvgen/lvlexer.go -o go-libvirt-src/lvlexer.go
```
