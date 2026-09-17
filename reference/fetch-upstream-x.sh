#!/bin/sh
set -e

# Fetches libvirt's two RPC protocol definition files at a pinned commit into
# reference/upstream-x/ — not committed to this repo (see .gitignore and
# reference/README.md for why: both files are Red Hat's LGPL-2.1-or-later
# source, and redistributing them verbatim alongside this project's own
# MIT-licensed code is worth avoiding, not just tolerating).
#
# Run this once after cloning (or whenever the pinned commit below changes)
# before running tools/NetfxLibvirt.ProtocolGen or the
# NetfxLibvirt.ProtocolGen.Tests real-file tests — both skip cleanly if the
# files aren't present yet, matching this project's usual "missing
# prerequisite -> skip, don't fail" pattern for anything needing something
# beyond a bare `dotnet build`.

# Pinned commits (see reference/README.md's "upstream-x/" section for the
# provenance and compatibility-bisection history behind these exact SHAs).
VIRNETPROTOCOL_COMMIT="fc920f704c19c27ce60a47f2788d40b30ea0d268"
REMOTE_PROTOCOL_COMMIT="6924b73d30d126ede2f6496615c666ce686462b5"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DEST_DIR="$SCRIPT_DIR/upstream-x"
mkdir -p "$DEST_DIR"

curl -fsSL "https://raw.githubusercontent.com/libvirt/libvirt/${VIRNETPROTOCOL_COMMIT}/src/rpc/virnetprotocol.x" \
    -o "$DEST_DIR/virnetprotocol.x"
curl -fsSL "https://raw.githubusercontent.com/libvirt/libvirt/${REMOTE_PROTOCOL_COMMIT}/src/remote/remote_protocol.x" \
    -o "$DEST_DIR/remote_protocol.x"

echo "Fetched virnetprotocol.x @ ${VIRNETPROTOCOL_COMMIT}"
echo "Fetched remote_protocol.x @ ${REMOTE_PROTOCOL_COMMIT}"
