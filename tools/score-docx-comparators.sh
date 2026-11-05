#!/bin/bash
# Scores each comparison tool's tracked-changes output on the DOCX gold set: every pair (a pair the tool did not complete counts as
# an empty result), only the pairs it completed, and each rendering look separately.
#
#   score-docx-comparators.sh SYNTH_DIR DOCX_DIR READ_DIR OUT_DIR TOOL...
#
# SYNTH_DIR is the gold set the DOCX set was rendered from, DOCX_DIR the rendered files (with <id>.blocks.json), READ_DIR the
# output of `OneDiff.GoldSets.Docx read` (READ_DIR/<tool>/), OUT_DIR gets <tool>/{all,completed,look0..look5}.json and .txt.
# The all and completed rows use 10,000 bootstrap resamples over documents; the per-look rows use 1,000.
# Set GOLDSETS_DLL to the built OneDiff.GoldSets.dll. Tool names are the ones given to `read --tool`; "onediff" scores a --cmp directory
# (READ_DIR/onediff/<id>.cmp.json) instead.
set -u
SYNTH=$1 DOCX=$2 READ=$3 OUT=$4
shift 4
DLL=${GOLDSETS_DLL:?set GOLDSETS_DLL}
mkdir -p "$OUT"
python3 - "$DOCX" "$OUT" <<'PY'
import glob, json, os, sys
docx, out = sys.argv[1:3]
looks = {}
for f in sorted(glob.glob(os.path.join(docx, "*.blocks.json"))):
    looks.setdefault(json.load(open(f)).get("variant", 0), []).append(os.path.basename(f)[: -len(".blocks.json")])
for v, ids in looks.items():
    open(os.path.join(out, f"ids-look{v}.txt"), "w").write("\n".join(ids) + "\n")
PY
for T in "$@"; do
  mkdir -p "$OUT/$T"
  if [ "$T" = onediff ]; then SRC=(--cmp "$READ/$T"); else SRC=(--changes "$READ/$T"); fi
  if [ -f "$READ/$T/roundtrip.jsonl" ]; then
    python3 - "$READ/$T/roundtrip.jsonl" "$OUT/$T/ids-completed.txt" <<'PY'
import json, sys
open(sys.argv[2], "w").write("\n".join(json.loads(l)["id"] for l in open(sys.argv[1]) if json.loads(l)["opened"]) + "\n")
PY
  else
    ls "$READ/$T" | sed -n 's/\.cmp\.json$//p' > "$OUT/$T/ids-completed.txt"
  fi
  dotnet "$DLL" score --data "$SYNTH" "${SRC[@]}" --blocks "$DOCX" --pipeline "$T" --jobs 4 --out "$OUT/$T/all.json" > "$OUT/$T/all.txt" 2>&1
  dotnet "$DLL" score --data "$SYNTH" "${SRC[@]}" --blocks "$DOCX" --pipeline "$T" --jobs 4 --ids "$OUT/$T/ids-completed.txt" --out "$OUT/$T/completed.json" > "$OUT/$T/completed.txt" 2>&1
  for L in 0 1 2 3 4 5; do
    dotnet "$DLL" score --data "$SYNTH" "${SRC[@]}" --blocks "$DOCX" --pipeline "$T" --jobs 4 --resamples 1000 --ids "$OUT/ids-look$L.txt" --out "$OUT/$T/look$L.json" > "$OUT/$T/look$L.txt" 2>&1
  done
done
