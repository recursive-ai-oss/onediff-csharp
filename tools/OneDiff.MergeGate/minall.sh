#!/usr/bin/env bash
# Shrinks the triples listed in a file (lines "<size> <repo>/<n>") while our base-to-side alignment still differs from git's,
# then prints each shrunk triple as one letter per distinct line, with both alignments.
# usage: [MODE=pairs|merge|diff3|zdiff3] minall.sh <gate.dll> <git> <corpus-root> <list> <out-dir> <python>
set -u
MODE=${MODE:-pairs}
DLL=$1; GIT=$2; C=$3; LIST=$4; OUT=$5; PY=$6
HERE=$(cd "$(dirname "$0")" && pwd)
rm -rf "$OUT"; mkdir -p "$OUT"
while read -r n r; do
  t=$(echo "$r" | tr / _)
  "$PY" "$HERE/ddmin.py" "$DLL" "$GIT" "$C/$r" "$MODE" builtin "$OUT/$t" > "$OUT/$t.log" 2>&1 &
done < "$LIST"
wait
for t in "$OUT"/*/; do
  n=$(basename "$t"); echo "######## $n"
  "$PY" "$HERE/show.py" "$t"
  rm -rf "$OUT/r1" "$OUT/o1"; mkdir -p "$OUT/r1/0"; cp "$t/base" "$t/ours" "$t/theirs" "$OUT/r1/0/"
  if [ "$MODE" = pairs ]; then
    dotnet "$DLL" "$GIT" "$OUT/r1" "$OUT/o1" --jobs=1 --pairs > /dev/null
    cut -f2- "$OUT/o1/pairs.tsv"
  else
    dotnet "$DLL" "$GIT" "$OUT/r1" "$OUT/o1" --jobs=1 > /dev/null
    for f in "$OUT"/o1/diffs/*/"$MODE"; do echo "== git"; cat -A "$f/git.out" | cut -c1-60; echo "== ours"; cat -A "$f/ours.out" | cut -c1-60; done
  fi
done
