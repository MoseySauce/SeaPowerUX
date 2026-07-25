#!/usr/bin/env bash
#
# Builds the pack and installs it into a Sea Power directory on THIS machine.
#
# This is the default workflow for contributors. scripts/deploy-steam.sh does the same thing
# over SSH to a separate test machine, which is a setup most people won't have — nothing here
# needs one.
#
# The game directory is found in this order:
#   1. $SEA_POWER_GAME_DIR
#   2. the first of the usual Steam locations that exists (native, flatpak, common Windows paths
#      under a Proton/Wine prefix)
# If neither finds it, pass it explicitly:
#   SEA_POWER_GAME_DIR="/path/to/Sea Power" scripts/deploy-local.sh
#
# Build outputs are copied by each project's own post-build step, so a plain
# `dotnet build -p:GameDir=...` installs too; this script just resolves the directory, builds
# in dependency order, and copies the files MSBuild doesn't (the default layout).
#
# Overridable via env vars:
#   SEA_POWER_GAME_DIR   Game directory to install into
#   FORCE_DEPLOY=1       Install even if the game appears to be running (not recommended)
set -euo pipefail

FORCE_DEPLOY="${FORCE_DEPLOY:-0}"
DEFAULT_PLUGINS=(SeaPowerUX.TacticalPiP SeaPowerUX.WindowMemory SeaPowerUX.FlightDeck SeaPowerUX.FormationManager)
PLUGINS=("${@:-${DEFAULT_PLUGINS[@]}}")

cd "$(dirname "$0")/.."

find_game_dir() {
  if [ -n "${SEA_POWER_GAME_DIR:-}" ]; then
    printf '%s' "$SEA_POWER_GAME_DIR"
    return
  fi

  local candidates=(
    "$HOME/.local/share/Steam/steamapps/common/Sea Power"
    "$HOME/.steam/steam/steamapps/common/Sea Power"
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Sea Power"
    "/mnt/c/Program Files (x86)/Steam/steamapps/common/Sea Power"
    "C:/Program Files (x86)/Steam/steamapps/common/Sea Power"
  )

  local dir
  for dir in "${candidates[@]}"; do
    if [ -d "$dir" ]; then
      printf '%s' "$dir"
      return
    fi
  done
}

GAME_DIR="$(find_game_dir)"

if [ -z "$GAME_DIR" ]; then
  cat >&2 <<'EOF'
ERROR: Could not find a Sea Power installation.

Set the path explicitly and re-run:
    SEA_POWER_GAME_DIR="/path/to/Sea Power" scripts/deploy-local.sh

The directory is the one containing "Sea Power.exe".
EOF
  exit 1
fi

if [ ! -d "$GAME_DIR/BepInEx" ]; then
  cat >&2 <<EOF
ERROR: BepInEx is not installed in:
    $GAME_DIR

Install BepInEx 5 (x64) first and run the game once so it creates its folders.
EOF
  exit 1
fi

# Hot-swapping a DLL under the running process corrupts assembly metadata reads and crashes the
# game in unrelated-looking ways — see the note in deploy-steam.sh.
if pgrep -x 'Sea Power.exe' >/dev/null 2>&1; then
  echo "ERROR: Sea Power is running. Close it first (or set FORCE_DEPLOY=1)." >&2
  [ "$FORCE_DEPLOY" = "1" ] || exit 1
fi

echo "==> Installing into: $GAME_DIR"

# Core first: the plugins reference its deployed DLL by path, so it must exist on disk before
# they build.
echo "==> Building SeaPowerUX.Core (Release)…"
dotnet build SeaPowerUX.Core/SeaPowerUX.Core.csproj -c Release -p:GameDir="$GAME_DIR"

for PLUGIN in "${PLUGINS[@]}"; do
  echo "==> Building $PLUGIN (Release)…"
  dotnet build "$PLUGIN/$PLUGIN.csproj" -c Release -p:GameDir="$GAME_DIR"
done

echo "==> Verifying…"
MISSING=0
for NAME in SeaPowerUX.Core "${PLUGINS[@]}"; do
  if [ -f "$GAME_DIR/BepInEx/plugins/$NAME/$NAME.dll" ]; then
    echo "    ok  $NAME"
  else
    echo "    MISSING  $NAME" >&2
    MISSING=1
  fi
done

[ "$MISSING" = "0" ] || exit 1

echo "==> Done. Start Sea Power; check BepInEx/LogOutput.log if anything is missing."
