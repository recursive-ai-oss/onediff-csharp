#!/usr/bin/env bash
# Publishes the browser test app and runs it in headless Chrome. Exit code 0 only when every test passed.
#   run.sh [output-dir]
# Needs the wasm-tools workload and either `uv` (which fetches the browser driver itself) or python3 with `playwright` installed.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${1:-${ONEDIFF_ARTIFACTS:-${TMPDIR:-/var/tmp}}/wasm-publish}

dotnet publish "$here" -c Release -o "$out"

if command -v uv >/dev/null 2>&1; then
  uv run "$here/run.py" "$out/wwwroot"
else
  python3 "$here/run.py" "$out/wwwroot"
fi
