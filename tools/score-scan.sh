#!/bin/bash
# Compare a scan set with the OCR source and score it against the labelled set it was made from; the same pairs read from the digital
# PDFs give the side-by-side rows.  Usage: score-scan.sh <name> <labelled set dir> <scan set dir> <digital pdf-v1 dir> <ids file> [jobs]
# Everything is written under $HOME/onediff/scan/out/<name>. Heavy: submit it through the machine's job queue.
set -u
NAME=$1; DATA=$2; SCAN=$3; DIGITAL=$4; IDS=$5; JOBS=${6:-4}
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=$JOBS DOTNET_CLI_TELEMETRY_OPTOUT=1
A=${ONEDIFF_ARTIFACTS:-$HOME/onediff/scan/art}/bin
OUT=$HOME/onediff/scan/out/$NAME; mkdir -p "$OUT"
SC="dotnet $A/OneDiff.GoldSets.Scan/release/OneDiff.GoldSets.Scan.dll"
GS="dotnet $A/OneDiff.GoldSets/release/OneDiff.GoldSets.dll"
uptime > "$OUT/load-before.txt"
$SC compare --set "$SCAN" --out "$OUT/cmp-ocr" --source "${SRC:-ocr}" --ids "$IDS" --jobs "$JOBS" > "$OUT/compare-ocr.log" 2>&1
$GS score --data "$DATA" --out "$OUT/ocr.json" --pipeline "ocr-scan" --cmp "$OUT/cmp-ocr" --blocks "$SCAN/pdf" --fuzzy-blocks --ids "$IDS" --jobs "$JOBS" > "$OUT/score-ocr.txt" 2>&1
if [ -d "$DIGITAL/pdf" ]; then
  $SC compare --set "$DIGITAL" --out "$OUT/cmp-pdf" --source pdf --ids "$IDS" --jobs "$JOBS" > "$OUT/compare-pdf.log" 2>&1
  $GS score --data "$DATA" --out "$OUT/pdf.json" --pipeline "pdf-digital" --cmp "$OUT/cmp-pdf" --blocks "$DIGITAL/pdf" --ids "$IDS" --jobs "$JOBS" > "$OUT/score-pdf.txt" 2>&1
fi
uptime > "$OUT/load-after.txt"
tail -3 "$OUT"/compare-*.log
