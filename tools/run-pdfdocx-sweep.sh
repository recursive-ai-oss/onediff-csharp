#!/bin/bash
# Generated-pair sweep for the reconstructed DOCX: for each seed, generates synthetic pairs, renders them to PDF with OnePDF's layout
# engine, and runs the gate (compare under Legal and Strict, reconstruct, track, accept/reject, schema, a second run) over them.
#
#   run-pdfdocx-sweep.sh OUT_DIR SEED... [-- DOCS_PER_LANG]
#
# Needs the OneDiff.GoldSets and OneDiff.GoldSets.Pdf tools and the Word tests built in Release (ONEDIFF_ARTIFACTS).
set -u
OUT=${1:?out dir}; shift
SEEDS=(); DOCS=100
while [ $# -gt 0 ]; do
  if [ "$1" = "--" ]; then DOCS=${2:?docs per language}; break; fi
  SEEDS+=("$1"); shift
done
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/art-pdfdocx}
for p in tools/OneDiff.GoldSets tools/OneDiff.GoldSets.Pdf tests/OneDiff.Word.Tests; do
  dotnet build -c Release -v quiet -clp:NoSummary -m:4 $p 2>&1 | grep -E 'error|Build succ' | head -3
done
BIN=$ONEDIFF_ARTIFACTS/bin
mkdir -p "$OUT"
for seed in "${SEEDS[@]}"; do
  d=$OUT/s$seed
  [ -f "$d/gate.txt" ] && continue
  dotnet $BIN/OneDiff.GoldSets/release/OneDiff.GoldSets.dll generate --out "$d/data" --role train --seed "$seed" --docs-per-lang "$DOCS" 2>&1 | tail -1
  dotnet $BIN/OneDiff.GoldSets.Pdf/release/OneDiff.GoldSets.Pdf.dll render --data "$d/data" --out "$d/pdf" --jobs 4 2>&1 | tail -2
  ONEDIFF_PDFDOCX_PDF=$d/pdf ONEDIFF_PDFDOCX_SCHEMA=1 ONEDIFF_PDFDOCX_OUT=$d/gate.txt ONEDIFF_PDFDOCX_JOBS=4 \
    dotnet $BIN/OneDiff.Word.Tests/release/OneDiff.Word.Tests.dll -method '*EveryPairWritesAReconstructedTrackedDocx*' 2>&1 | grep -E 'failed|Total'
  echo "seed $seed: pairs $(wc -l < "$d/gate.txt") failures $(wc -l < "$d/gate.txt.failures")"
  rm -rf "$d/pdf" "$d/data"
done
