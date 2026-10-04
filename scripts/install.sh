#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
cvpp_game_paths "${1:-}"
[[ -f artifacts/dist/cvpp/cvpp.dll && -f artifacts/dist/cvpp/libcvpp_core.so ]] || { echo 'Run ./scripts/build.sh first.' >&2; exit 1; }
game_dir="$(realpath "$game_dir")"
for cvpp_pid in $(pgrep -x SlayTheSpire2 || true); do
  if [[ "$(readlink "/proc/$cvpp_pid/exe" || true)" == "$game_dir/SlayTheSpire2" ]]; then
    echo 'Close the game before installing the mod.' >&2; exit 1
  fi
done
destination="$game_dir/mods/cvpp"
if [[ -d "$destination" ]]; then
  backup="artifacts/backups/cvpp-$(date +%Y%m%d-%H%M%S)"
  mkdir -p artifacts/backups
  cp -a "$destination" "$backup"
  echo "Previous version backup: $backup"
fi
mkdir -p "$destination"
cp artifacts/dist/cvpp/cvpp.dll artifacts/dist/cvpp/cvpp.json artifacts/dist/cvpp/libcvpp_core.so artifacts/dist/cvpp/LICENSE "$destination/"
echo "Installed: $destination"
echo 'Enable Combat Solver ++ in the game Mod menu. F10 opens the route; Esc stops the solver.'
