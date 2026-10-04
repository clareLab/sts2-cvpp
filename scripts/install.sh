#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
cvpp_game_paths "${1:-}"
[[ -f artifacts/dist/cvpp/cvpp.dll && -f artifacts/dist/cvpp/libcvpp_core.so && -x artifacts/dist/cvpp/cvpp-worker ]] || { echo 'Run ./scripts/build.sh first.' >&2; exit 1; }
game_dir="$(realpath "$game_dir")"
cvpp_running=false
for cvpp_pid in $(pgrep -x SlayTheSpire2 || true); do
  if [[ "$(readlink "/proc/$cvpp_pid/exe" || true)" == "$game_dir/SlayTheSpire2" ]]; then
    cvpp_running=true
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
cvpp_staging="$(mktemp -d "$game_dir/.cvpp-install.XXXXXX")"
trap 'rm -rf -- "$cvpp_staging"' EXIT
for cvpp_file in cvpp.dll cvpp.json libcvpp_core.so cvpp-worker LICENSE; do
  cp "artifacts/dist/cvpp/$cvpp_file" "$cvpp_staging/$cvpp_file"
done
for cvpp_file in cvpp.dll cvpp.json libcvpp_core.so cvpp-worker LICENSE; do
  mv -f -- "$cvpp_staging/$cvpp_file" "$destination/$cvpp_file"
done
echo "Installed: $destination"
if [[ "$cvpp_running" == true ]]; then echo 'Restart the game to load the installed version.'; fi
echo 'Enable Combat Solver ++ in the game Mod menu.'
