#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
case "${1:-}" in
  --source|"") ;;
  *) echo 'Usage: ./scripts/test.sh [--source]' >&2; exit 2 ;;
esac
cargo test --workspace --locked
cargo build --workspace --release --locked
cvpp_dotnet run --project tests/interop/InteropTests.csproj -c Release
if [[ "${1:-}" != --source ]]; then
  ./scripts/build.sh
  ./scripts/test-game.sh
fi
