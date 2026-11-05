#!/bin/bash
# Runs the reconstructed-DOCX gate over rendered pairs in resumable chunks through the machine's job queue, then a spread of 20 pairs
# for cross-machine digests.
#
#   run-pdfdocx-gate.sh MODE PDF_SET DOCX_SET OUT_DIR [CHUNK] [BUILD]
#
# MODE is pp (old PDF, new PDF), dp (old DOCX, new PDF) or pd (old PDF, new DOCX); PDF_SET and DOCX_SET are the rendered sets
# (DOCX_SET is not read in pp mode), BUILD is new (default) or both.
set -u
MODE=${1:?mode} PDF=${2:?pdf set} DOCX=${3:-} OUT=${4:?out dir} CHUNK=${5:-300} BUILD=${6:-new}
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/art-pdfdocx}
export ONEDIFF_PDFDOCX_PDF=$PDF ONEDIFF_PDFDOCX_DOCX=$DOCX ONEDIFF_PDFDOCX_MODE=$MODE ONEDIFF_PDFDOCX_SCHEMA=1 ONEDIFF_PDFDOCX_BUILD=$BUILD ONEDIFF_PDFDOCX_JOBS=4
mkdir -p "$OUT"
uptime > "$OUT/load-before.txt"
dotnet build -c Release -v quiet -clp:NoSummary -m:4 tests/OneDiff.Word.Tests 2>&1 | tail -3
T=$ONEDIFF_ARTIFACTS/bin/OneDiff.Word.Tests/release/OneDiff.Word.Tests.dll
TOTAL=$(ls "$PDF"/pdf/*.old.pdf | wc -l)
for ((f = 0; f < TOTAL; f += CHUNK)); do
  [ -f "$OUT/$MODE-chunk-$f.txt" ] && continue
  wq run --class heavy --max 30m --cores 4 --label "onediff pdfdocx gate $MODE from $f" -- \
    env ONEDIFF_PDFDOCX_FROM=$f ONEDIFF_PDFDOCX_COUNT=$CHUNK ONEDIFF_PDFDOCX_OUT="$OUT/$MODE-chunk-$f.txt" dotnet "$T" -method '*EveryPairWritesAReconstructedTrackedDocx*' 2>&1 | tail -3
done
wq run --class heavy --max 30m --cores 4 --label "onediff pdfdocx gate $MODE digest slice" -- \
  env ONEDIFF_PDFDOCX_EVERY=56 ONEDIFF_PDFDOCX_BUILD=both ONEDIFF_PDFDOCX_OUT="$OUT/$MODE-digest-$(uname -m).txt" dotnet "$T" -method '*EveryPairWritesAReconstructedTrackedDocx*' 2>&1 | tail -3
uptime > "$OUT/load-after.txt"
wc -l "$OUT"/$MODE-*.txt
cat "$OUT"/$MODE-*.failures 2>/dev/null | wc -l
