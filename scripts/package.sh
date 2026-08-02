#!/usr/bin/env bash
# Build the Release configuration, and package the mod as a zip file for ModDB.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_DIR"

VERSION=$(grep -oP '"version":\s*"\K[^"]+' VintageHorizons/modinfo.json)
OUT="$REPO_DIR/dist"
MOD_DIR="VintageHorizons/bin/Release/net10.0/Mods/vintagehorizons"

dotnet build VintageHorizons -c Release

mkdir -p "$OUT"
ZIP="$OUT/vintagehorizons_${VERSION}.zip"
rm -f "$ZIP"

# A zip file for ModDB holds the mod files at the root of the archive, with no folder
# around them. It never holds a DLL of the game, because each reference uses
# Private=false.
python3 - "$MOD_DIR" "$ZIP" <<'EOF'
import os, sys, zipfile
mod_dir, zip_path = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(mod_dir):
        for f in files:
            if f.endswith(".pdb"):
                continue
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, mod_dir))
    print("packaged:", zip_path)
    for info in z.infolist():
        print(f"  {info.file_size:>9}  {info.filename}")
EOF
