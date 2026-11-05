#!/bin/bash
# Runs the rendered-redline re-extraction gate in resumable chunks through the machine's job queue.
# Usage: run-render-gate.sh text|pdf <out dir> [chunk size] [every n-th pair]
#   text: the text corpus in ~/onediff/corpora/text (ONEDIFF_RENDER_TEXT_CORPUS overrides)
#   pdf:  the PDF pair set in ~/onediff/eval/pdf-v1 (ONEDIFF_PDF_GATE_DIR overrides), compared PDF to PDF and rendered
# A chunk whose output file exists is skipped, so the script can be started again after an interruption. A last job runs a spread
# of pairs for cross-machine digests.
set -u
MODE=$1
OUT=$2
CHUNK=${3:-100}
EVERY=${4:-1}
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/art-render}
export ONEDIFF_RENDER_JOBS=${ONEDIFF_RENDER_JOBS:-4}
mkdir -p "$OUT"
if [ "$MODE" = text ]; then
  export ONEDIFF_RENDER_TEXT_CORPUS=${ONEDIFF_RENDER_TEXT_CORPUS:-$HOME/onediff/corpora/text}
  FILTER='*EveryTextCorpusPair*'
  TOTAL=$(ls -d "$ONEDIFF_RENDER_TEXT_CORPUS"/*/*/ | wc -l)
else
  export ONEDIFF_PDF_GATE_DIR=${ONEDIFF_PDF_GATE_DIR:-$HOME/onediff/eval/pdf-v1}
  FILTER='*EveryPdfPairReadsBack*'
  TOTAL=$(ls "$ONEDIFF_PDF_GATE_DIR"/pdf/*.old.pdf | wc -l)
  (cd "$ONEDIFF_PDF_GATE_DIR" && sha256sum manifest.json && cat manifest.sha256) > "$OUT/manifest-check.txt" 2>&1
fi
uptime > "$OUT/load-before.txt"
(cd tests/OneDiff.Pdf.Tests && nice dotnet build -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | tail -3)
T=$ONEDIFF_ARTIFACTS/bin/OneDiff.Pdf.Tests/release/OneDiff.Pdf.Tests.dll
for ((f = 0; f < TOTAL; f += CHUNK)); do
  [ -f "$OUT/chunk-$f.txt" ] && continue
  wq run --class heavy --max 30m --cores 4 --label "onediff render gate $MODE from $f" -- \
    env ONEDIFF_RENDER_FROM=$f ONEDIFF_RENDER_COUNT=$CHUNK ONEDIFF_RENDER_EVERY=$EVERY ONEDIFF_RENDER_OUT="$OUT/chunk-$f.txt" dotnet "$T" -method "$FILTER" 2>&1 | tail -3
done
wq run --class heavy --max 30m --cores 4 --label "onediff render gate $MODE digest slice" -- \
  env ONEDIFF_RENDER_EVERY=37 ONEDIFF_RENDER_OUT="$OUT/digest-$(uname -m).txt" dotnet "$T" -method "$FILTER" 2>&1 | tail -3
uptime > "$OUT/load-after.txt"
grep -h '^#' "$OUT"/chunk-*.txt "$OUT"/digest-*.txt
cat "$OUT"/*.failures 2>/dev/null | wc -l
