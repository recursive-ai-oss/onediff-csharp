#!/usr/bin/env bash
# Times the DOCX comparison tools (and, once its Word package exists, the engine's own row in DocxBench) on joined gold-set pairs
# of about 5, 40 and 120 pages: document in, tracked-changes document out, in process and single threaded.
#
# Usage: run-docx-bench.sh OUT [BenchmarkDotNet args, e.g. --filter '*Docxodus*']
# Environment:
#   ONEDIFF_DOCX_BENCH   folder with n1, n8, n24 (default: $ONEDIFF_HOME/eval/docx-v1-bench, made by
#                        `OneDiff.GoldSets.Docx bench-pairs --data <gold set> --out <folder> --lang en --sizes 1,8,24`)
#   ONEDIFF_DOCX_BENCH_DLL  built OneDiff.Bench.Docx.dll (default: under ONEDIFF_ARTIFACTS, after
#                        `dotnet build bench/OneDiff.Bench.Docx -c Release`)
# Writes OUT/machine.txt (CPU, cores and load before, during and after) and OUT/bdn/ (BenchmarkDotNet's results).
set -euo pipefail
out="$(mkdir -p "$1" && cd "$1" && pwd)"; shift
here="$(cd "$(dirname "$0")" && pwd)"
dll="${ONEDIFF_DOCX_BENCH_DLL:-${ONEDIFF_ARTIFACTS:?set ONEDIFF_ARTIFACTS or ONEDIFF_DOCX_BENCH_DLL}/bin/OneDiff.Bench.Docx/release/OneDiff.Bench.Docx.dll}"
export ONEDIFF_DOCX_BENCH="${ONEDIFF_DOCX_BENCH:-${ONEDIFF_HOME:-$HOME/.cache/onediff}/eval/docx-v1-bench}"
export TMPDIR="${TMPDIR:-$out}"
"$here/record-run.sh" "$out/machine.txt" -- dotnet "$dll" --artifacts "$out/bdn" --filter '*DocxBench*' "$@"
