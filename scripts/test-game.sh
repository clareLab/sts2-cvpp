#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
cvpp_mode=()
if [[ "${1:-}" == '--benchmark' ]]; then cvpp_mode=(--benchmark); shift; fi
cvpp_game_paths "${1:-}"
cargo build --workspace --release --locked
cvpp_dotnet build src/cvpp.csproj -c Release "-p:Sts2DataDir=$data_dir" -p:CvppSelfTest=true "-p:ArtifactsPath=$PWD/artifacts/integration"
python3 scripts/headless.py "$game_dir" "$data_dir" "${cvpp_mode[@]}"
