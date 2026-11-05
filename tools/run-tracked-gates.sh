#!/bin/bash
# Runs the tracked-changes DOCX gates: accept-all gives the new document, reject-all gives the old one (read back through the Word
# source and compared under Legal and Strict), and the output validates against the ECMA-376 schemas.
#
#   run-tracked-gates.sh SWEEP_PAIRS DOCX_DIR OUT_DIR
#
# Environment: MODES (default "new old") picks the documents to build on; FIRST (default 0) the first generated pair, SWEEP_PAIRS being the last (exclusive). SWEEP_PAIRS is how many generated pairs to run (in chunks of 1000), DOCX_DIR a directory of ID.old.docx / ID.new.docx pairs (pass
# "-" to skip it), OUT_DIR where summaries and the tracked files go. Each gate runs built on the new document and built on the old one.
set -u
PAIRS=${1:?pairs} DOCX=${2:?docx dir} OUT=${3:?out dir}
mkdir -p "$OUT"
dotnet build -c Release -v quiet -clp:NoSummary -m:4 tests/OneDiff.Word.Tests 2>&1 | tail -2
for mode in ${MODES:-new old}; do
  if [ "$DOCX" != "-" ]; then
    ONEDIFF_DOCXV1_DIR=$DOCX ONEDIFF_DOCXV1_OUT=$OUT/tracked ONEDIFF_DOCXV1_SCHEMA=1 ONEDIFF_DOCXV1_BUILD=$mode ONEDIFF_DOCXV1_REPORT=$OUT/docx-$mode.txt \
      dotnet test -c Release --no-build --project tests/OneDiff.Word.Tests --filter-method '*AcceptRejectAndSchema' > /dev/null 2>&1
    head -1 "$OUT/docx-$mode.txt"
    grep -E '^(FAIL|SCHEMA|CRASH)' "$OUT/docx-$mode.txt" | head -5 | cut -c1-300
  fi
  for ((first = ${FIRST:-0}; first < PAIRS; first += 1000)); do
    n=$((PAIRS - first)); [ $n -gt 1000 ] && n=1000
    ONEDIFF_TRACKED_SWEEP=$n ONEDIFF_TRACKED_FIRST=$first ONEDIFF_TRACKED_BUILD=$mode ONEDIFF_TRACKED_SCHEMA=1 ONEDIFF_TRACKED_OUT=$OUT/sweep-$mode-$first.txt \
      dotnet test -c Release --no-build --project tests/OneDiff.Word.Tests --filter-method '*SeededPairsAcceptAndReject' > /dev/null 2>&1
    head -1 "$OUT/sweep-$mode-$first.txt"
    tail -n +2 "$OUT/sweep-$mode-$first.txt" | head -5 | cut -c1-300
  done
done
