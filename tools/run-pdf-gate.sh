#!/bin/bash
# Runs the PDF gate over a rendered pair set in resumable chunks through the machine's job queue, then a spread of 20 pairs
# for cross-machine digests. Usage: run-pdf-gate.sh <pair-set dir> <out dir> [chunk size]
set -u
SET=$1
OUT=$2
CHUNK=${3:-555}
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/art-gate}
export ONEDIFF_PDF_GATE_DIR=$SET ONEDIFF_PDF_GATE_JOBS=4
mkdir -p "$OUT"
(cd "$SET" && sha256sum manifest.json && cat manifest.sha256) > "$OUT/manifest-check.txt"
uptime > "$OUT/load-before.txt"
(cd tests/OneDiff.Pdf.Tests && dotnet build -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | tail -3)
T=$ONEDIFF_ARTIFACTS/bin/OneDiff.Pdf.Tests/release/OneDiff.Pdf.Tests.dll
TOTAL=$(ls "$SET"/pdf/*.old.pdf | wc -l)
for ((f = 0; f < TOTAL; f += CHUNK)); do
  [ -f "$OUT/chunk-$f.txt" ] && continue
  wq run --class heavy --max 30m --cores 4 --label "onediff pdf gate from $f" -- \
    env ONEDIFF_PDF_GATE_FROM=$f ONEDIFF_PDF_GATE_COUNT=$CHUNK ONEDIFF_PDF_GATE_OUT="$OUT/chunk-$f.txt" dotnet "$T" -method '*EveryPairOfTheDirectory*' 2>&1 | tail -3
done
wq run --class heavy --max 30m --cores 4 --label "onediff pdf gate digest slice" -- \
  env ONEDIFF_PDF_GATE_EVERY=56 ONEDIFF_PDF_GATE_OUT="$OUT/digest-$(uname -m).txt" dotnet "$T" -method '*EveryPairOfTheDirectory*' 2>&1 | tail -3
uptime > "$OUT/load-after.txt"
wc -l "$OUT"/*.txt
cat "$OUT"/*.failures 2>/dev/null | wc -l
