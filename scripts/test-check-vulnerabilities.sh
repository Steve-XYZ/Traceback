#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
parser="$repo_root/scripts/check-vulnerabilities.py"

python3 "$parser" "$repo_root/tests/fixtures/package-audit-clean.json"

if python3 "$parser" "$repo_root/tests/fixtures/package-audit-vulnerable.json"; then
  echo "vulnerability gate accepted a report containing vulnerabilities" >&2
  exit 1
else
  status=$?
  if [[ $status -ne 1 ]]; then
    echo "vulnerability gate failed with status $status instead of 1 for a vulnerable report" >&2
    exit "$status"
  fi
fi
