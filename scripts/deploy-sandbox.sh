#!/usr/bin/env bash
set -euo pipefail

# Build the mod, and install it into the sandbox at .testdata/Mods.
#
# A person did this step by hand before. Thus a sandbox run could measure a binary from
# an earlier session, and it gave no message.
#
# bench.sh did this step only as a side effect, when it emptied the mod directories for
# its own comparison. Thus a normal run, which is not a bench run, had no way to install
# a new build at all.
#
# Usage: deploy-sandbox.sh [client|server|both]. The default is client.
#
# The server side is deliberately NOT the default. Most tests need a vanilla dedicated
# server, to prove that the mod operates with an installation on the client only. Ask for
# the server side when you test the server assist.

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
    # Replace the directory. Do not merge into it. A file that the build no longer makes
    # must go away here also. Without that, an old asset continues after the change that
    # deleted it.
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
