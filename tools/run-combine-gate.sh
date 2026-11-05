#!/bin/bash
# Combine and chain gates on the worker, through the machine's job queue. Usage: run-combine-gate.sh <set|sweep> <out dir> [set dir]
#   1. builds the gold-set tool and the document tests in Release;
#   2. generates the combine set from the eval originals (seed 20261104) and checks its originals are all eval documents of the synthetic set;
#   3. scores it (invertibility, attribution, sharing and conflict rows, 10,000 resamples);
#   4. (mode sweep) runs the seeded property sweeps: 60,000 combine cases and 20,000 chains of 3 to 5 steps;
#   5. (mode set) prints the digest of the first 20 cases and the timing of a 100-page combine.
set -u
MODE=$1
OUT=$2
SET=${3:-$HOME/onediff/eval/combine-v1}
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/u24/art-$MODE}
mkdir -p "$OUT"
uptime > "$OUT/load-before-$MODE.txt"
G=$ONEDIFF_ARTIFACTS/bin/OneDiff.GoldSets/release/OneDiff.GoldSets.dll
T=$ONEDIFF_ARTIFACTS/bin/OneDiff.Documents.Tests/release/OneDiff.Documents.Tests.dll
if [ "$MODE" = set ]; then
  dotnet build tools/OneDiff.GoldSets -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | tail -3
  # One queued job: generate, check, score, digest and timing.
  wq run --class heavy --max 30m --cores 4 --label "onediff u24 combine set" -- bash -c '
    set -e
    [ -f "$1/manifest.sha256" ] || dotnet "$2" generate-combine --out "$1" | tee "$3/generate.txt"
    cp "$1/manifest.sha256" "$3/set-hash.txt"
    python3 - "$1" <<"PY" | tee "$3/eval-check.txt"
import json, sys, os
split = json.load(open(os.path.join(sys.argv[1], "split.json")))
ev = {(s["lang"], s["doc"]) for s in split if s["role"] == "eval"}
used = set()
for f in os.listdir(os.path.join(sys.argv[1], "cases")):
    c = json.load(open(os.path.join(sys.argv[1], "cases", f)))
    used.add((c["lang"], c["baseDoc"]))
    assert c["role"] == "eval"
print("originals", len(used), "eval documents", len(ev), "all originals are eval documents:", used <= ev)
PY
    dotnet "$2" score-combine --data "$1" --out "$3/combine-v1-eval.json" --jobs 4 | tee "$3/score.txt"
    dotnet "$2" digest-combine --data "$1" --count 20 | tee "$3/digest-$(uname -m).txt"
    dotnet "$2" time-combine | tee "$3/timing-indicative.txt"
  ' _ "$SET" "$G" "$OUT"
else
  dotnet build tests/OneDiff.Documents.Tests -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | tail -3
  wq run --class heavy --max 30m --cores 4 --label "onediff u24 combine and chain sweeps" -- bash -c '
    set -e
    for from in 0 30000; do
      ONEDIFF_COMBINE_CASES=30000 ONEDIFF_COMBINE_SEED=$from dotnet "$1" -method "*RandomReviewersAlwaysVerify*" 2>&1 | tail -4 | tee "$2/sweep-combine-$from.txt"
    done
    for from in 0 10000; do
      ONEDIFF_CHAIN_CASES=10000 ONEDIFF_CHAIN_SEED=$from dotnet "$1" -method "*RandomChainsAlwaysVerify*" 2>&1 | tail -4 | tee "$2/sweep-chain-$from.txt"
    done
  ' _ "$T" "$OUT"
fi
uptime > "$OUT/load-after-$MODE.txt"
