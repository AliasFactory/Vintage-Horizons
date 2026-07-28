#!/usr/bin/env bash
set -euo pipefail

# Build the mod and install it into the sandbox at .testdata/Mods.
#
# This step used to be manual, which meant a sandbox run could silently measure a
# binary from some earlier session. bench.sh only ever did it as a side effect of
# wiping mod directories for its own comparison, so a plain (non-bench) run had no
# way to get a fresh build in at all.
#
# Usage: deploy-sandbox.sh [client|server|both]   (default: client)
#
# The server side is deliberately NOT the default: most testing wants a strictly
# vanilla dedicated server, to prove the mod works as a client-side-only install.
# Ask for it explicitly when testing the server assist.

source "$(dirname "${BASH_SOURCE[0]}")/test-lib.sh"

TARGET="${1:-client}"
case "$TARGET" in
    client|server|both) ;;
    *) echo "usage: $(basename "$0") [client|server|both]" >&2; exit 2 ;;
esac

BUILT="$VH_ROOT/VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons"

echo "Building VintageHorizons..."
(cd "$VH_ROOT" && dotnet build VintageHorizons -v quiet --nologo)

if [[ ! -d "$BUILT" ]]; then
    echo "Build produced no mod folder at $BUILT" >&2
    exit 1
fi

install_into() {
    local dest="$1" label="$2"
    mkdir -p "$dest"
    # Replace rather than merge: a file removed from the build must disappear here
    # too, or a stale asset outlives the change that deleted it.
    rm -rf "${dest:?}/vintagehorizons"
    cp -r "$BUILT" "$dest/"
    echo "  $label: $dest/vintagehorizons"
}

[[ "$TARGET" == "client" || "$TARGET" == "both" ]] && install_into "$VH_SANDBOX/Mods" "client"
[[ "$TARGET" == "server" || "$TARGET" == "both" ]] && install_into "$VH_SANDBOX/server/Mods" "server"

if [[ "$TARGET" == "client" && -d "$VH_SANDBOX/server/Mods/vintagehorizons" ]]; then
    echo "  note: the sandbox SERVER still has the mod installed." >&2
    echo "        rm -rf '$VH_SANDBOX/server/Mods/vintagehorizons' for a vanilla server." >&2
fi

echo "Deployed $(grep -oP '"version":\s*"\K[^"]+' "$VH_ROOT/VintageHorizons/modinfo.json")"
