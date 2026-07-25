#!/usr/bin/env bash
#
# Build one or more plugins (plus the shared SeaPowerUX.Core library) and deploy them to the
# Steam Machine over SSH.
#
# The game runs on the Steam Machine (this dev box can barely run it), so we build here against
# the local game DLLs and copy just the plugin DLLs across.
#
# Usage:
#   scripts/deploy-steam.sh                                  # deploy the default plugin set
#   scripts/deploy-steam.sh SeaPowerUX.FormationManager       # deploy just one plugin
#   scripts/deploy-steam.sh SeaPowerUX.TacticalPiP SeaPowerUX.FormationManager
#
# SeaPowerUX.Core is always built+deployed first (regardless of args) since every SeaPowerUX.*
# plugin's csproj resolves its reference against Core's already-deployed DLL, not a project
# reference — so a stale/missing Core breaks every dependent plugin's build.
#
# Requirements:
#   - SSH set up to the Steam Machine (an ~/.ssh/config Host entry; default: "steam").
#   - The game + BepInEx installed on the Steam Machine.
#
# IMPORTANT: quit the game before deploying. Overwriting a plugin DLL while Sea Power is
# running corrupts the metadata Mono reads lazily from the still-mapped file, which surfaces
# as a bogus unrelated crash at mission load, e.g.:
#   CultureNotFoundException: Culture name pectLocked is not supported
#     at Unity.Entities.TypeManager.IsAssemblyReferencingEntities
# ("pectLocked" is a fragment of a string constant inside the swapped assembly — the string
# heap offsets shifted underneath the running process.) This script refuses to deploy while
# the game is running; set FORCE_DEPLOY=1 to override.
#
# Required env vars:
#   SEA_POWER_GAME_DIR   Local game dir used to resolve build references
#   REMOTE_GAME_DIR      Game dir on the Steam Machine
#
# Overridable via env vars:
#   STEAM_HOST           SSH host alias for the Steam Machine (default: steam)
#   FORCE_DEPLOY=1       Deploy even if the game appears to be running (not recommended)
set -euo pipefail

STEAM_HOST="${STEAM_HOST:-steam}"
LOCAL_GAME_DIR="${SEA_POWER_GAME_DIR:?Set SEA_POWER_GAME_DIR to the local Sea Power install dir}"
REMOTE_GAME_DIR="${REMOTE_GAME_DIR:?Set REMOTE_GAME_DIR to the Sea Power install dir on the Steam Machine}"
FORCE_DEPLOY="${FORCE_DEPLOY:-0}"

DEFAULT_PLUGINS=(SeaPowerUX.TacticalPiP SeaPowerUX.WindowMemory SeaPowerUX.FlightDeck SeaPowerUX.FormationManager)
PLUGINS=("${@:-${DEFAULT_PLUGINS[@]}}")

cd "$(dirname "$0")/.."

# Matches on the process NAME (comm), not the full command line, so it can't match this
# script's own ssh/bash wrapper the way `pgrep -f` would.
if ssh "$STEAM_HOST" "pgrep -x 'Sea Power.exe' >/dev/null 2>&1"; then
  echo "ERROR: Sea Power is running on $STEAM_HOST." >&2
  echo "       Deploying now would hot-swap DLLs under the live process and cause a" >&2
  echo "       spurious crash at mission load (see the comment at the top of this script)." >&2
  echo "       Quit the game, then re-run. Override with FORCE_DEPLOY=1 if you're sure." >&2
  [ "$FORCE_DEPLOY" = "1" ] || exit 1
  echo "       FORCE_DEPLOY=1 set — continuing anyway." >&2
fi

deploy_dll() {
  local name="$1" dll_path="$2"
  local remote_dir="$REMOTE_GAME_DIR/BepInEx/plugins/$name"
  echo "==> Deploying $name to $STEAM_HOST:$remote_dir"
  ssh "$STEAM_HOST" "mkdir -p '$remote_dir'"
  # Pipe over ssh rather than scp: the game path contains a space and OpenSSH's SFTP-based scp
  # mishandles quoting of it. A remote-shell redirect is robust.
  ssh "$STEAM_HOST" "cat > '$remote_dir/$name.dll'" < "$dll_path"
}

echo "==> Building SeaPowerUX.Core (Release)…"
dotnet build SeaPowerUX.Core/SeaPowerUX.Core.csproj -c Release -p:GameDir="$LOCAL_GAME_DIR"
deploy_dll "SeaPowerUX.Core" "SeaPowerUX.Core/bin/Release/SeaPowerUX.Core.dll"

for PLUGIN in "${PLUGINS[@]}"; do
  echo "==> Building $PLUGIN (Release)…"
  dotnet build "$PLUGIN/$PLUGIN.csproj" -c Release -p:GameDir="$LOCAL_GAME_DIR"
  deploy_dll "$PLUGIN" "$PLUGIN/bin/Release/$PLUGIN.dll"
done


echo "==> Done. Restart Sea Power on the Steam Machine to load the new build(s)."
