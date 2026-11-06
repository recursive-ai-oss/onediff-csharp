#!/usr/bin/env bash
# Publishes each browser test app (the core, .Documents, .Pdf and .Ocr suites) and runs it in headless Chrome.
# Exit code 0 only when every suite ran tests and none failed.
#   run.sh [output-dir] [suite ...]      suites default to: Core Documents Pdf Ocr
# Needs the wasm-tools workload and either `uv` (which fetches the browser driver itself) or python3 with `playwright` installed.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${1:-${ONEDIFF_ARTIFACTS:-${TMPDIR:-/var/tmp}}/wasm-publish}
shift || true
suites=("$@")
[ ${#suites[@]} -gt 0 ] || suites=(Core Documents Pdf Ocr)

status=0
for suite in "${suites[@]}"; do
  echo "=== suite $suite"
  # Each suite gets its own build folder: suites resolve different package versions, and a shared intermediate folder would let
  # one suite's converted assemblies (webcil) be reused by the next when their timestamps are newer.
  ONEDIFF_ARTIFACTS="$out/art-$suite" dotnet publish "$here" -c Release -p:WasmSuite="$suite" -o "$out/$suite" -v quiet -clp:NoSummary
  if command -v uv >/dev/null 2>&1; then
    uv run "$here/run.py" "$out/$suite/wwwroot" || status=1
  else
    python3 "$here/run.py" "$out/$suite/wwwroot" || status=1
  fi
done
exit $status
