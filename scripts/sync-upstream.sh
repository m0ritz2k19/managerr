#!/usr/bin/env bash
set -euo pipefail

# Synchronize all pulled upstream *arr & companion reference repositories
# Usage: ./scripts/sync-upstream.sh [--update]

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [[ "${1:-}" == "--update" ]]; then
  echo "[managerr] Fetching latest upstream commits (shallow)..."
  git submodule update --init --remote --depth 1
else
  echo "[managerr] Initializing pinned upstream submodules (shallow)..."
  git submodule update --init --depth 1
fi

echo "[managerr] Upstream repository status:"
git submodule status
