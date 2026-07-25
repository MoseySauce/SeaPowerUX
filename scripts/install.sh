#!/usr/bin/env bash
#
# Installs a built SeaPowerUX release into a Sea Power directory. For players, on Linux and the
# Steam Deck.
#
# This is NOT the contributor build script — it installs already-built DLLs from a release
# bundle. To build from source instead, use scripts/deploy-local.sh.
#
# It copies the bundled BepInEx/ tree (plugins + the default layout) over the game's own BepInEx/
# folder. BepInEx 5 (x64) must already be installed and have been run once. See INSTALL.md.
#
# Usage:
#   ./install.sh                 install into the auto-detected game directory
#   ./install.sh --uninstall     remove the pack (leaves BepInEx and your saves alone)
#   ./install.sh --game-dir DIR  install into DIR (the folder containing "Sea Power.exe")
#
# Environment overrides:
#   SEA_POWER_GAME_DIR   game directory, same as --game-dir
#   SEA_POWER_SOURCE     bundle directory containing BepInEx/ (defaults to beside this script)
#   FORCE_INSTALL=1      proceed even if the game appears to be running (not recommended)
set -euo pipefail

FORCE_INSTALL="${FORCE_INSTALL:-0}"
UNINSTALL=0
GAME_DIR_ARG=""

# The pack's own folders under BepInEx/plugins/. Used to verify an install and to know exactly
# what to remove on uninstall — nothing else in plugins/ is touched.
PLUGINS=(
  SeaPowerUX.Core
  SeaPowerUX.TacticalPiP
  SeaPowerUX.FlightDeck
  SeaPowerUX.WindowMemory
  SeaPowerUX.FormationManager
)
DEFAULTS_FILE="com.moseysauce.seapowerux.defaults.cfg"

while [ $# -gt 0 ]; do
  case "$1" in
    --uninstall) UNINSTALL=1 ;;
    --game-dir)  shift; GAME_DIR_ARG="${1:-}" ;;
    -h|--help)
      sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# The bundle: where the BepInEx/ payload lives. In a release zip the script sits beside it; in
# the source tree it sits under scripts/, one level down.
find_source() {
  if [ -n "${SEA_POWER_SOURCE:-}" ]; then printf '%s' "$SEA_POWER_SOURCE"; return; fi
  local c
  for c in "$SCRIPT_DIR" "$SCRIPT_DIR/.."; do
    if [ -d "$c/BepInEx/plugins" ]; then printf '%s' "$c"; return; fi
  done
}

find_game_dir() {
  if [ -n "$GAME_DIR_ARG" ]; then printf '%s' "$GAME_DIR_ARG"; return; fi
  if [ -n "${SEA_POWER_GAME_DIR:-}" ]; then printf '%s' "$SEA_POWER_GAME_DIR"; return; fi

  local candidates=(
    "$HOME/.local/share/Steam/steamapps/common/Sea Power"
    "$HOME/.steam/steam/steamapps/common/Sea Power"
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Sea Power"
    "/run/media/mmcblk0p1/steamapps/common/Sea Power"   # Deck SD card
  )
  local dir
  for dir in "${candidates[@]}"; do
    if [ -d "$dir" ]; then printf '%s' "$dir"; return; fi
  done
}

GAME_DIR="$(find_game_dir)"
if [ -z "$GAME_DIR" ]; then
  cat >&2 <<'EOF'
ERROR: Could not find a Sea Power installation.

Pass the path explicitly:
    ./install.sh --game-dir "/path/to/Sea Power"

That's the folder containing "Sea Power.exe".
EOF
  exit 1
fi

if [ ! -d "$GAME_DIR" ]; then
  echo "ERROR: Not a directory: $GAME_DIR" >&2
  exit 1
fi

# Hot-swapping a DLL under the running process corrupts assembly metadata reads and crashes the
# game in unrelated-looking ways. Refuse unless forced.
if pgrep -x 'Sea Power.exe' >/dev/null 2>&1; then
  echo "ERROR: Sea Power appears to be running. Close it first (or set FORCE_INSTALL=1)." >&2
  [ "$FORCE_INSTALL" = "1" ] || exit 1
fi

# ---------------------------------------------------------------------------------------------
# Uninstall
# ---------------------------------------------------------------------------------------------
if [ "$UNINSTALL" = "1" ]; then
  echo "==> Uninstalling from: $GAME_DIR"
  removed=0
  for p in "${PLUGINS[@]}"; do
    if [ -d "$GAME_DIR/BepInEx/plugins/$p" ]; then
      rm -rf "$GAME_DIR/BepInEx/plugins/$p"
      echo "    removed  plugins/$p"
      removed=1
    fi
  done
  if [ -f "$GAME_DIR/BepInEx/config/$DEFAULTS_FILE" ]; then
    rm -f "$GAME_DIR/BepInEx/config/$DEFAULTS_FILE"
    echo "    removed  config/$DEFAULTS_FILE"
    removed=1
  fi
  # Per-plugin user config (com.moseysauce.seapowerux.*.cfg) is left in place on purpose, so a
  # reinstall keeps your window layout and settings. Delete BepInEx/config/com.moseysauce.* by
  # hand if you want a clean slate.
  if [ "$removed" = "0" ]; then
    echo "    nothing to remove — SeaPowerUX was not installed here."
  fi
  echo "==> Done."
  exit 0
fi

# ---------------------------------------------------------------------------------------------
# Install
# ---------------------------------------------------------------------------------------------
SOURCE="$(find_source)"
if [ -z "$SOURCE" ]; then
  echo "ERROR: Could not find the bundled BepInEx/ payload next to this script." >&2
  echo "       Set SEA_POWER_SOURCE to the folder containing BepInEx/plugins/." >&2
  exit 1
fi

if [ ! -d "$GAME_DIR/BepInEx" ]; then
  cat >&2 <<EOF
ERROR: BepInEx is not installed in:
    $GAME_DIR

Install BepInEx 5 (x64) first and launch the game once so it creates its folders, then re-run
this installer. See INSTALL.md.
EOF
  exit 1
fi

echo "==> Installing SeaPowerUX into: $GAME_DIR"
echo "    from: $SOURCE"

# Merge the payload tree in. cp -R of BepInEx/. onto BepInEx overlays plugins/ and config/ without
# disturbing BepInEx's own files or other mods.
cp -R "$SOURCE/BepInEx/." "$GAME_DIR/BepInEx/"

echo "==> Verifying…"
MISSING=0
for p in "${PLUGINS[@]}"; do
  if [ -f "$GAME_DIR/BepInEx/plugins/$p/$p.dll" ]; then
    echo "    ok  $p"
  else
    echo "    MISSING  $p" >&2
    MISSING=1
  fi
done
[ "$MISSING" = "0" ] || { echo "ERROR: install incomplete." >&2; exit 1; }

echo "==> Done. Start Sea Power. If anything is missing, check BepInEx/LogOutput.log."
