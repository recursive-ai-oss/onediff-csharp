#!/bin/bash
# Scores the PDF source on a split of the synthetic gold set: renders the dev documents to PDF once (or uses the evaluation PDFs when
# EVAL=1), compares every EVERY-th pair and scores it. Usage: score-pdf-split.sh <name> [every]   (output under ~/onediff/pdf-split/<name>)
set -u
NAME=$1; EVERY=${2:-1}
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=$HOME/onediff/pdf-split/art
B=$HOME/onediff/pdf-split; O=$B/$NAME; mkdir -p "$O"
D=$HOME/onediff/dev/synth-v1-dev; PD=$B/pdf-dev/pdf
if [ "${EVAL:-}" = 1 ]; then D=$HOME/onediff/eval/synth-v1; PD=$HOME/onediff/eval/pdf-v1/pdf; fi
BIN=$ONEDIFF_ARTIFACTS/bin
for p in tools/OneDiff.GoldSets.Pdf tools/OneDiff.GoldSets; do
  dotnet build -c Release -v quiet -clp:NoSummary -m:4 $p > "$O/build.log" 2>&1 || { tail -5 "$O/build.log"; exit 1; }
done
if [ ! -d "$B/pdf-dev/pdf" ]; then
  dotnet "$BIN/OneDiff.GoldSets.Pdf/release/OneDiff.GoldSets.Pdf.dll" render --data "$D" --out "$B/pdf-dev" --jobs 4 > "$B/render.log" 2>&1
fi
dotnet "$BIN/OneDiff.GoldSets.Pdf/release/OneDiff.GoldSets.Pdf.dll" compare --pdf "$PD" --out "$O/cmp" --every "$EVERY" --jobs 4 2>&1 | tail -2
ls "$O/cmp" | sed -n 's/\.cmp\.json$//p' > "$O/ids"
dotnet "$BIN/OneDiff.GoldSets/release/OneDiff.GoldSets.dll" score --data "$D" --cmp "$O/cmp" --blocks "$PD" --pipeline pdf-dev --ids "$O/ids" --jobs 4 --out "$O/score.json" > "$O/score.txt" 2>&1
grep -E "align|boundary|frag|missed|regions|verify|fallback|pairs" "$O/score.txt" | head -40
