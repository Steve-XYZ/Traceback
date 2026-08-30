#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

set -o pipefail
dotnet list Traceback.slnx package --vulnerable --include-transitive --format json \
  | python3 scripts/check-vulnerabilities.py
