#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
cvpp_game_paths "${1:-}"
cargo build --workspace --release --locked
cvpp_dotnet build src/cvpp.csproj -c Release "-p:Sts2DataDir=$data_dir"
