#!/bin/bash
# Writes the reconstructed-DOCX gate's digest over a small rendered set (DIR/pdf and DIR/docx), so that it can be compared across
# machines: for each of the three pairings (PDF/PDF, Word/PDF, PDF/Word), the SHA-256 of every tracked DOCX built on the new and on
# the old document under Legal and Strict, and one SHA-256 over all of them.
#
#   run-pdfdocx-digest.sh DIR OUT_DIR [DOTNET_DIR]
set -u
SET=${1:?set dir} OUT=${2:?out dir} DN=${3:-$HOME/dotnet10}
SET=$(cd "$SET" && pwd)
export PATH=$DN:$PATH DOTNET_ROOT=$DN DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/art-pdfdocx-digest}
mkdir -p "$OUT"
dotnet build -c Release -v quiet -clp:NoSummary -m:4 tests/OneDiff.Word.Tests 2>&1 | grep -E 'error|Build succ' | head -3
T=$ONEDIFF_ARTIFACTS/bin/OneDiff.Word.Tests/release/OneDiff.Word.Tests.dll
: > "$OUT/all.txt"
for m in pp dp pd; do
  ONEDIFF_PDFDOCX_PDF=$SET ONEDIFF_PDFDOCX_DOCX=$SET ONEDIFF_PDFDOCX_MODE=$m ONEDIFF_PDFDOCX_BUILD=both ONEDIFF_PDFDOCX_SCHEMA=1 ONEDIFF_PDFDOCX_JOBS=2 \
    ONEDIFF_PDFDOCX_OUT="$OUT/$m.txt" dotnet "$T" -method '*EveryPairWritesAReconstructedTrackedDocx*' 2>&1 | grep -E 'failed|Total'
  sed "s/^/$m /" "$OUT/$m.txt" >> "$OUT/all.txt"
done
wc -l "$OUT"/*.failures | tail -1
if command -v sha256sum > /dev/null; then sha256sum "$OUT/all.txt"; else shasum -a 256 "$OUT/all.txt"; fi
