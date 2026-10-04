#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
./scripts/lint.sh
./scripts/test.sh "$@"
