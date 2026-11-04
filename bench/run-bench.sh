#!/usr/bin/env bash
# Runs the whole benchmark pipeline into OUT: the BenchmarkDotNet suites, the inputs/edit-distance listing, peak memory per
# row, and the results table, with the machine record beside them.
#
# Usage: run-bench.sh OUT [BenchmarkDotNet args, e.g. --filter '*LineDiffBench*']
# Environment:
#   ONEDIFF_BENCH_DLL   path to the built OneDiff.Bench.dll (default: artifacts/bin/OneDiff.Bench/release/ under ONEDIFF_ARTIFACTS)
#   ONEDIFF_BENCH_MODE  "smoke" for a short indicative pass; anything else for full BenchmarkDotNet runs
#   ONEDIFF_CORPORA     corpus root (see fetch-corpora.py)
set -euo pipefail
out="$(mkdir -p "$1" && cd "$1" && pwd)"; shift
here="$(cd "$(dirname "$0")" && pwd)"
dll="${ONEDIFF_BENCH_DLL:-${ONEDIFF_ARTIFACTS:?set ONEDIFF_ARTIFACTS or ONEDIFF_BENCH_DLL}/bin/OneDiff.Bench/release/OneDiff.Bench.dll}"

"$here/record-run.sh" "$out/machine.txt" -- dotnet "$dll" --artifacts "$out/bdn" "$@"
dotnet "$dll" info "$out/info.json"
dotnet "$dll" peak-all "$out/peak.json"
dotnet "$dll" report "$out/bdn/results" --peak "$out/peak.json" --info "$out/info.json" --env "$out/machine.txt" --out "$out/results-table.md"
