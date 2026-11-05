#!/bin/bash
# Scores cross-format pairs (a Word file against a PDF rendering of the same synthetic pair) against the gold labels.
#
#   run-cross-score.sh OUT_DIR [EVERY] [MODE...]
#
# Needs the rendered sets under ~/onediff/eval (pdf-v1, docx-v1) and the gold set synth-v1. For each MODE (dp: old DOCX, new PDF;
# pd: old PDF, new DOCX; dd and pp are the same-format references; default dp pd dd pp) it compares every EVERY-th pair (default 5)
# CROSS_SETTINGS (legal or textonly, default legal) picks the compare settings. It writes OUT_DIR/MODE.json with alignment, boundary and fragmentation rows and 10,000 bootstrap resamples over documents.
set -eu
OUT=${1:?out dir}
EVERY=${2:-5}
shift 2 || shift $#
MODES=${*:-dp pd dd pp}
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/art-u30}
E=$HOME/onediff/eval
mkdir -p "$OUT"
dotnet build -c Release -v quiet -clp:NoSummary -m:4 tools/OneDiff.GoldSets.Pdf > "$OUT/build.log" 2>&1 || { tail -5 "$OUT/build.log"; exit 1; }
BIN=$ONEDIFF_ARTIFACTS/bin
for M in $MODES; do
  nice dotnet "$BIN/OneDiff.GoldSets.Pdf/release/OneDiff.GoldSets.Pdf.dll" cross --mode "$M" --pdf "$E/pdf-v1/pdf" --docx "$E/docx-v1/docx" --out "$OUT/$M" --every "$EVERY" --settings "${CROSS_SETTINGS:-legal}" --jobs 4 2>&1 | tail -2
  ls "$OUT/$M" | sed -n 's/\.cmp\.json$//p' > "$OUT/$M.ids"
  nice dotnet "$BIN/OneDiff.GoldSets/release/OneDiff.GoldSets.dll" score --data "$E/synth-v1" --cmp "$OUT/$M" --blocks "$OUT/$M" --pipeline "cross-$M" --ids "$OUT/$M.ids" --jobs 4 --out "$OUT/$M.json" > "$OUT/$M.txt" 2>&1
  grep -E "align.F1|boundary|frag|missed|verify|fallback" "$OUT/$M.txt" | head -20
done
