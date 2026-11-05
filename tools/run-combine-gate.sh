#!/bin/bash
# Combine and chain gates on the worker, through the machine's job queue. Usage: run-combine-gate.sh <out dir> [set dir]
#   1. builds the gold-set tool and the document tests in Release;
#   2. generates the combine set from the eval originals (seed 20261104) and checks its originals are all eval documents of the synthetic set;
#   3. scores it (invertibility, attribution, sharing and conflict rows, 10,000 resamples);
#   4. runs the seeded property sweeps: 60,000 combine cases and 20,000 chains of 3 to 5 steps;
#   5. prints the digest of the first 20 cases and the timing of a 100-page combine.
set -u
OUT=$1
SET=${2:-$HOME/onediff/eval/combine-v1}
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/u24/art}
mkdir -p "$OUT"
uptime > "$OUT/load-before.txt"
dotnet build tools/OneDiff.GoldSets -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | tail -3
dotnet build tests/OneDiff.Documents.Tests -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | tail -3
G=$ONEDIFF_ARTIFACTS/bin/OneDiff.GoldSets/release/OneDiff.GoldSets.dll
T=$ONEDIFF_ARTIFACTS/bin/OneDiff.Documents.Tests/release/OneDiff.Documents.Tests.dll
if [ ! -f "$SET/manifest.sha256" ]; then
  wq run --class heavy --max 30m --cores 4 --label "onediff u24 generate combine set" -- dotnet "$G" generate-combine --out "$SET" | tee "$OUT/generate.txt"
fi
cat "$SET/manifest.sha256" > "$OUT/set-hash.txt"
python3 - "$SET" <<'E' > "$OUT/eval-check.txt"
import json, sys, os
split = json.load(open(os.path.join(sys.argv[1], 'split.json')))
ev = {(s['lang'], s['doc']) for s in split if s['role'] == 'eval'}
used = set()
for f in os.listdir(os.path.join(sys.argv[1], 'cases')):
    c = json.load(open(os.path.join(sys.argv[1], 'cases', f)))
    used.add((c['lang'], c['baseDoc']))
    assert c['role'] == 'eval'
print('originals', len(used), 'eval documents', len(ev), 'all originals are eval documents:', used <= ev)
E
cat "$OUT/eval-check.txt"
wq run --class heavy --max 30m --cores 4 --label "onediff u24 score combine set" -- dotnet "$G" score-combine --data "$SET" --out "$OUT/combine-v1-eval.json" --jobs 4 | tee "$OUT/score.txt"
for from in 0 30000; do
  wq run --class heavy --max 30m --cores 4 --label "onediff u24 combine sweep $from" -- \
    env ONEDIFF_COMBINE_CASES=30000 ONEDIFF_COMBINE_SEED=$from ONEDIFF_CHAIN_CASES=0 dotnet "$T" -method '*RandomReviewersAlwaysVerify*' 2>&1 | tail -4 | tee "$OUT/sweep-combine-$from.txt"
done
for from in 0 10000; do
  wq run --class heavy --max 30m --cores 4 --label "onediff u24 chain sweep $from" -- \
    env ONEDIFF_CHAIN_CASES=10000 ONEDIFF_CHAIN_SEED=$from dotnet "$T" -method '*RandomChainsAlwaysVerify*' 2>&1 | tail -4 | tee "$OUT/sweep-chain-$from.txt"
done
wq run --class heavy --max 30m --cores 4 --label "onediff u24 digest" -- dotnet "$G" digest-combine --data "$SET" --count 20 | tee "$OUT/digest-$(uname -m).txt"
wq run --class heavy --max 30m --cores 4 --label "onediff u24 indicative timing" -- dotnet "$G" time-combine | tee "$OUT/timing-indicative.txt"
uptime > "$OUT/load-after.txt"
