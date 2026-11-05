#!/bin/bash
# Our two DOCX rows on the DOCX gold set, scored the way the free comparators are: (a) the engine's comparisons of the rendered
# files, (b) our tracked-changes output read back by the same reader. Run from the repository root (inside `wq run --class heavy`).
#
#   run-docx-rows.sh OUT_DIR [SETTINGS] [BUILD]       SETTINGS Legal|Strict (default Legal), BUILD new|old (default new)
# COMPARATORS="docxodus officeimo wmlcomparer libreoffice" also reads and scores those tools' outputs ($EVAL/cmp-docx-v1/<tool>/ID.docx).
#
# Needs ONEDIFF_ARTIFACTS, the eval sets under ~/onediff/eval (synth-v1, docx-v1) and dotnet on PATH. Writes OUT_DIR/{tracked,read,score}.
set -eu
OUT=${1:?out dir} SET=${2:-Legal} BUILD=${3:-new}
EVAL=${ONEDIFF_EVAL:-$HOME/onediff/eval}
SYNTH=$EVAL/synth-v1 DOCX=$EVAL/docx-v1/docx
mkdir -p "$OUT"
dotnet build -c Release -v quiet -clp:NoSummary -m:4 tests/OneDiff.Word.Tests 2>&1 | tail -2
dotnet build -c Release -v quiet -clp:NoSummary -m:4 tools/OneDiff.GoldSets 2>&1 | tail -2
dotnet build -c Release -v quiet -clp:NoSummary -m:4 tools/OneDiff.GoldSets.Docx 2>&1 | tail -2
[ -n "${SKIP_GATE:-}" ] || ONEDIFF_DOCXV1_DIR=$DOCX ONEDIFF_DOCXV1_OUT=$OUT/tracked ONEDIFF_DOCXV1_CMP=1 ONEDIFF_DOCXV1_BUILD=$BUILD ONEDIFF_DOCXV1_REPORT=$OUT/gate.txt \
  dotnet test -c Release --no-build --project tests/OneDiff.Word.Tests --filter-method '*AcceptRejectAndSchema' > "$OUT/gate.log" 2>&1 || true
head -1 "$OUT/gate.txt"
ART=${ONEDIFF_ARTIFACTS:?}
GS=$(find "$ART/bin/OneDiff.GoldSets" -iname OneDiff.GoldSets.dll -ipath '*release*' | head -1)
GD=$(find "$ART/bin/OneDiff.GoldSets.Docx" -iname OneDiff.GoldSets.Docx.dll -ipath '*release*' | head -1)
mkdir -p "$OUT/read/onediff-tracked" "$OUT/read/onediff-engine"
dotnet "$GD" read --tool onediff-tracked --tracked "$OUT/tracked/$SET-${BUILD^}" --docx "$DOCX" --out "$OUT/read/onediff-tracked" --jobs 4 > "$OUT/read.log" 2>&1
cp "$OUT/tracked/$SET-cmp/"*.cmp.json "$OUT/read/onediff-engine/"
# The comparators' outputs (from tools/run-docx-comparators or the earlier runs) go through the same reader, so every tool is read alike.
for T in ${COMPARATORS:-}; do
  mkdir -p "$OUT/read/$T"
  dotnet "$GD" read --tool "$T" --tracked "$EVAL/cmp-docx-v1/$T" --docx "$DOCX" --out "$OUT/read/$T" --jobs 4 > "$OUT/read-$T.log" 2>&1
done
GOLDSETS_DLL=$GS tools/score-docx-comparators.sh "$SYNTH" "$DOCX" "$OUT/read" "$OUT/score" onediff-tracked onediff-engine ${COMPARATORS:-}
# Paired rows against each comparator: the same documents resampled once for both tools.
for MINE in onediff-tracked onediff-engine; do
  for T in ${COMPARATORS:-}; do
    dotnet "$GS" pair --a "$OUT/score/$MINE/all.docs.json" --b "$OUT/score/$T/all.docs.json" --name-a "$MINE" --name-b "$T" > "$OUT/pair-$MINE-vs-$T.md"
  done
done
grep -hE '^(all|block map)' "$OUT/score/onediff-tracked/all.txt" "$OUT/score/onediff-engine/all.txt" | head -60
