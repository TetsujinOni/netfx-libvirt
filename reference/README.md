# Reference material

Vendored/mirrored upstream sources used as ground truth while building this
client — never linked into the shipped library, read (and eventually
machine-parsed, for `upstream-x`) as a reference only.

## `upstream-x/`

The real, unmodified libvirt RPC protocol definitions, fetched directly from
`libvirt/libvirt@master` on 2026-09-15:

| File | Source commit (last touching that path) |
|---|---|
| `virnetprotocol.x` | [`fc920f7`](https://github.com/libvirt/libvirt/commit/fc920f704c19c27ce60a47f2788d40b30ea0d268) |
| `remote_protocol.x` | [`6924b73`](https://github.com/libvirt/libvirt/commit/6924b73d30d126ede2f6496615c666ce686462b5) |

`virnetprotocol.x` defines the 24-byte `virNetMessageHeader` and framing
(ported by hand into `src/NetfxLibvirt/Rpc/`). `remote_protocol.x` (~7200
lines, 200+ procedures) is the actual target of the future `.x`-parsing
codegen — not yet hand-read in full, only grepped for the MVP procedure
numbers so far.

Refetch with:

```bash
curl -fsSL https://raw.githubusercontent.com/libvirt/libvirt/master/src/rpc/virnetprotocol.x -o upstream-x/virnetprotocol.x
curl -fsSL https://raw.githubusercontent.com/libvirt/libvirt/master/src/remote/remote_protocol.x -o upstream-x/remote_protocol.x
```

## `go-libvirt-src/`

Individual files mirrored from `digitalocean/go-libvirt@main` (Apache 2.0),
read to confirm exact wire-level behavior the `.x` files alone leave
ambiguous — algorithm reference only, never compiled or linked here.

| File | Source commit (last touching that path) | Why |
|---|---|---|
| `rpc.go` | [`174fc63`](https://github.com/digitalocean/go-libvirt/commit/174fc63aebd4ef790d76bc26e4c7840bc58803ac) | Call/reply/event routing shape. |
| `socket/socket.go` | [`761cfee`](https://github.com/digitalocean/go-libvirt/commit/761cfeeb596863a450ad7e6f5fff373b82f3f4e3) | Confirmed the length-prefix semantics: the 4-byte `Len` field counts *itself* plus the header plus the payload (`packet{ Len uint32; Header }`, `p.Len = sizeof(Len)+sizeof(Header)+len(payload)`) — this is what `VirNetMessageFraming` in this repo implements. |
| `socket/units.go` | (same package) | Size constants referenced alongside `socket.go`. |

Refetch with:

```bash
curl -fsSL https://raw.githubusercontent.com/digitalocean/go-libvirt/main/rpc.go -o go-libvirt-src/rpc.go
curl -fsSL https://raw.githubusercontent.com/digitalocean/go-libvirt/main/socket/socket.go -o go-libvirt-src/socket.go
curl -fsSL https://raw.githubusercontent.com/digitalocean/go-libvirt/main/socket/units.go -o go-libvirt-src/units.go
```
