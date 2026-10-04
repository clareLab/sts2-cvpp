#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
mapfile -t cvpp_sources < <(rg --files src tests -g '*.cs')
cvpp_dotnet format whitespace . --folder --include "${cvpp_sources[@]}" --verify-no-changes
ruff check scripts tests
ruff format --check scripts tests
if command -v shellcheck >/dev/null; then shellcheck -x scripts/*.sh
elif command -v nix >/dev/null; then nix shell nixpkgs#shellcheck -c shellcheck -x scripts/*.sh
else echo 'ShellCheck is required.' >&2; exit 1; fi
git diff --check
git diff --cached --check
