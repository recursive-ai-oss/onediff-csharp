#!/bin/bash
# The OneNLP recognizer gates on a synthetic gold set, through the machine's job queue. Usage: run-nlp-gate.sh <set dir> <out dir> [split-role label]
#   1. nlp-gate: Legal + the recognizer on every pair: Verify at fallback 0, Check, two identical runs, difference from built-in per language;
#   2. score with the built-in recognizer and with --recognizer nlp (semantic, boundary and alignment rows, 10,000 resamples);
#   3. a digest of the first 20 pairs, for comparing machines.
# Runs on x64 or arm64 alike (the digest line carries the architecture in its file name).
set -u
SET=$1
OUT=$2
export PATH=${DOTNET_BIN:-$HOME/dotnet10}:$PATH DOTNET_ROOT=${DOTNET_BIN:-$HOME/dotnet10} DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/nlp/art}
mkdir -p "$OUT"
uptime > "$OUT/load-before.txt"
dotnet build tools/OneDiff.GoldSets -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | tail -3
G=$ONEDIFF_ARTIFACTS/bin/OneDiff.GoldSets/release/OneDiff.GoldSets.dll
wq run --class heavy --max 30m --cores 4 --label "onediff u26 nlp gate" -- bash -c '
  set -e
  dotnet "$1" nlp-gate --data "$2" --jobs 4 --show 200 | tee "$3/nlp-gate.txt"
  dotnet "$1" score --data "$2" --out "$3/builtin.json" --jobs 4 > "$3/score-builtin.txt"
  dotnet "$1" score --data "$2" --out "$3/nlp.json" --recognizer nlp --jobs 4 > "$3/score-nlp.txt"
  dotnet "$1" nlp-gate --data "$2" --digest-only --count 20 | tee "$3/digest-$(uname -m).txt"
' _ "$G" "$SET" "$OUT"
uptime > "$OUT/load-after.txt"
