#!/usr/bin/env bash
#
# Builds every plugin in Release and assembles a distributable zip:
#
#   SeaPowerUX-v<version>.zip
#     ├── manifest.json, icon.png, README.md, INSTALL.md, CHANGELOG.md, LICENSE
#     ├── install.bat / install.ps1 / install.sh
#     └── BepInEx/plugins/<Plugin>/<Plugin>.dll   (Core + each plugin)
#
# The layout is flat at the zip root: manifest/icon/README for Thunderstore-style hosting, the
# install scripts next to their BepInEx/ payload (which is what install.* look for), and the mod
# DLLs under BepInEx/ so a mod manager or a manual copy drops straight into the game folder.
#
# BepInEx itself is NOT bundled — players install it separately (see INSTALL.md); the dependency is
# declared in manifest.json instead.
#
# Building needs the game's reference assemblies (Seapower-Scripts.dll, Noesis, Unity, BepInEx core)
# to compile against, exactly like scripts/deploy-local.sh. Point at a real install with
# SEA_POWER_GAME_DIR, or let it auto-detect the usual Steam locations. On CI, SEA_POWER_GAME_DIR
# points at a maintainer-provided reference tree — see .github/workflows/build.yml.
#
# Output goes to dist/ (git-ignored).
set -euo pipefail

cd "$(dirname "$0")/.."

CORE="SeaPowerUX.Core"
PLUGINS=(SeaPowerUX.TacticalPiP SeaPowerUX.WindowMemory SeaPowerUX.FlightDeck SeaPowerUX.FormationManager)
OUT="dist"

# --- version, from manifest.json -----------------------------------------------------------------
VERSION="$(sed -n 's/.*"version_number"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' manifest.json | head -1)"
if [ -z "$VERSION" ]; then
  echo "ERROR: could not read version_number from manifest.json" >&2
  exit 1
fi

# --- reference/game directory --------------------------------------------------------------------
find_game_dir() {
  if [ -n "${SEA_POWER_GAME_DIR:-}" ]; then printf '%s' "$SEA_POWER_GAME_DIR"; return; fi
  local c
  for c in \
    "$HOME/.local/share/Steam/steamapps/common/Sea Power" \
    "$HOME/.steam/steam/steamapps/common/Sea Power" \
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Sea Power" \
    "/mnt/c/Program Files (x86)/Steam/steamapps/common/Sea Power" \
    "C:/Program Files (x86)/Steam/steamapps/common/Sea Power"; do
    if [ -d "$c" ]; then printf '%s' "$c"; return; fi
  done
}

GAME_DIR="$(find_game_dir)"
if [ -z "$GAME_DIR" ] || [ ! -f "$GAME_DIR/Sea Power_Data/Managed/Seapower-Scripts.dll" ]; then
  cat >&2 <<EOF
ERROR: no build references found.

Set SEA_POWER_GAME_DIR to a directory containing the game's managed assemblies:
    $GAME_DIR/Sea Power_Data/Managed/Seapower-Scripts.dll
and BepInEx core (BepInEx/core/*.dll). A real Sea Power install works; on CI, provide a
reference tree with the same layout.
EOF
  exit 1
fi

echo "==> Version:   $VERSION"
echo "==> Building against: $GAME_DIR"

# --- build (Core first: plugins reference its deployed DLL) --------------------------------------
dotnet build "$CORE/$CORE.csproj" -c Release -p:GameDir="$GAME_DIR" -v m -nologo
for p in "${PLUGINS[@]}"; do
  dotnet build "$p/$p.csproj" -c Release -p:GameDir="$GAME_DIR" -v m -nologo
done

# --- stage ---------------------------------------------------------------------------------------
STAGE="$OUT/stage"
rm -rf "$STAGE"
mkdir -p "$STAGE/BepInEx/plugins"

for name in "$CORE" "${PLUGINS[@]}"; do
  dll="$name/bin/Release/$name.dll"
  if [ ! -f "$dll" ]; then echo "ERROR: build output missing: $dll" >&2; exit 1; fi
  mkdir -p "$STAGE/BepInEx/plugins/$name"
  cp "$dll" "$STAGE/BepInEx/plugins/$name/"
done

cp scripts/install.bat scripts/install.ps1 scripts/install.sh "$STAGE/"
for f in manifest.json icon.png README.md INSTALL.md CHANGELOG.md LICENSE; do
  [ -f "$f" ] && cp "$f" "$STAGE/"
done

# --- zip -----------------------------------------------------------------------------------------
# Contents are archived flat (files at the zip root). Prefer `zip` (present on the CI runner); fall
# back to Python's zipfile so the script also runs on a box without `zip` installed.
ZIP_ABS="$(cd "$OUT" && pwd)/SeaPowerUX-v$VERSION.zip"
rm -f "$ZIP_ABS"

if command -v zip >/dev/null 2>&1; then
  ( cd "$STAGE" && zip -rq "$ZIP_ABS" . )
elif command -v python3 >/dev/null 2>&1; then
  python3 - "$STAGE" "$ZIP_ABS" <<'PY'
import os, sys, zipfile
stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(stage):
        for f in sorted(files):
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, stage))
PY
else
  echo "ERROR: need 'zip' or 'python3' to create the archive." >&2
  exit 1
fi

echo "==> Wrote $ZIP_ABS"
python3 - "$ZIP_ABS" <<'PY' 2>/dev/null || true
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    for n in z.namelist():
        print("    " + n)
PY
