#!/usr/bin/env bash
# Times the document budgets, one command, with the machine record (CPU, cores, ISA flags, load before, during and after).
#
#   (a) markup    a 100-page PDF pair with about 300 changes: read both, compare, mark the new document's pages
#   (b) rendered  the same pair to a rendered redline PDF, inline and side by side, with the peak memory of each
#   (c) docx      a 100-page DOCX contract with about 300 changes to a tracked-changes DOCX (information only)
#   (d) memory    peak working set above the resident inputs, per page, from (a) and (b)
#
# Each part runs in its own process (so its peak memory is its own) on 4 cores (DOTNET_PROCESSOR_COUNT=4), with one untimed warm-up
# and 3 timed repetitions; the table reports the median. Budgets: (a) at most 2 s, (b) at most 2 s inline with a peak of at most 1 GB.
#
# Usage: run-budgets.sh OUT
# Environment:
#   ONEDIFF_BUDGET_PAGES  pages of the pair (default 100); use 10 for a quick check
#   ONEDIFF_BUDGET_REPS   timed repetitions (default 3)
#   ONEDIFF_ARTIFACTS     where the tests are built (they are built here when missing)
# Writes OUT/machine.txt, OUT/<part>.txt for each part, and OUT/budgets.md (the table).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"

if [ -z "${ONEDIFF_BUDGET_INNER:-}" ]; then
  out="$(mkdir -p "$1" && cd "$1" && pwd)"
  ONEDIFF_BUDGET_INNER=1 INTERVAL="${INTERVAL:-15}" exec "$here/record-run.sh" "$out/machine.txt" -- "$0" "$out"
fi

out="$1"
: "${ONEDIFF_ARTIFACTS:?set ONEDIFF_ARTIFACTS}"
pages="${ONEDIFF_BUDGET_PAGES:-100}"
reps="${ONEDIFF_BUDGET_REPS:-3}"
export DOTNET_PROCESSOR_COUNT=4 DOTNET_TieredPGO=1 ONEDIFF_BUDGET_PAGES="$pages" ONEDIFF_BUDGET_REPS="$reps"
export TMPDIR="${TMPDIR:-$out}"

pdf_dll="$ONEDIFF_ARTIFACTS/bin/OneDiff.Pdf.Tests/release/OneDiff.Pdf.Tests.dll"
word_dll="$ONEDIFF_ARTIFACTS/bin/OneDiff.Word.Tests/release/OneDiff.Word.Tests.dll"
for p in Pdf Word; do
  dll="$ONEDIFF_ARTIFACTS/bin/OneDiff.$p.Tests/release/OneDiff.$p.Tests.dll"
  [ -f "$dll" ] || dotnet build "$root/tests/OneDiff.$p.Tests/OneDiff.$p.Tests.csproj" -c Release -m:4 -v quiet -clp:NoSummary
done

dir="$out/pair"
run_part() { # part
  ONEDIFF_BUDGET_PART="$1" ONEDIFF_BUDGET_DIR="$dir" ONEDIFF_BUDGET_OUT="$out/$1.txt" \
    dotnet "$pdf_dll" -class OneDiff.Pdf.Tests.BudgetTests > "$out/$1.log" 2>&1 || { tail -20 "$out/$1.log"; return 1; }
}
run_part generate
for part in markup rendered-inline rendered-side-by-side; do run_part "$part"; done
ONEDIFF_WORD_TIMING="$out/docx.txt" dotnet "$word_dll" -method OneDiff.Word.Tests.TimingTests.ReadAndCompareAHundredPages > "$out/docx.log" 2>&1 \
  || { tail -20 "$out/docx.log"; exit 1; }

# The table: medians over the timed repetitions, against the proposed budgets.
median() { sed -n 's/.*median_total_ms=\([0-9]*\).*/\1/p' "$1"; }
field() { sed -n "s/.*$2=\(-\{0,1\}[0-9]*\).*/\1/p" "$1" | head -1; }
verdict() { if [ "$1" -le "$2" ]; then echo pass; else echo "over budget"; fi; }
docx_median=$(grep '^rep [1-9]' "$out/docx.txt" | sed -n 's/.*read(2)=\([0-9]*\)ms compare=\([0-9]*\)ms.*tracked-docx=\([0-9]*\)ms.*/\1 \2 \3/p' | awk '{print $1+$2+$3}' | sort -n | awk '{t[NR]=$1} END {print t[int((NR+1)/2)]}')
{
  echo "# Budgets (pages: $pages, repetitions: $reps, DOTNET_PROCESSOR_COUNT=4)"
  echo
  echo "| Row | Median ms | Peak working set MB | Bytes per page above the inputs | Budget | Verdict |"
  echo "|---|---|---|---|---|---|"
  m=$(median "$out/markup.txt"); echo "| (a) PDF pair read + compare + on-page markup | $m | $(field "$out/markup.txt" peak_working_set_MB) | $(field "$out/markup.txt" bytes_per_page) | 2000 ms | $(verdict "$m" 2000) |"
  m=$(median "$out/rendered-inline.txt"); pk=$(field "$out/rendered-inline.txt" peak_working_set_MB)
  echo "| (b) same, rendered redline inline | $m | $pk | $(field "$out/rendered-inline.txt" bytes_per_page) | 2000 ms, peak 1024 MB | $(verdict "$m" 2000) / $(verdict "$pk" 1024) |"
  m=$(median "$out/rendered-side-by-side.txt"); echo "| (b) same, rendered redline side by side | $m | $(field "$out/rendered-side-by-side.txt" peak_working_set_MB) | $(field "$out/rendered-side-by-side.txt" bytes_per_page) | information | — |"
  echo "| (c) DOCX contract read + compare + tracked DOCX (information, not in this release) | ${docx_median:-n/a} | $(field "$out/docx.txt" peak_working_set_MB) | — | information | — |"
  echo
  echo "Machine record: machine.txt. Raw lines per repetition: markup.txt, rendered-inline.txt, rendered-side-by-side.txt, docx.txt."
} > "$out/budgets.md"
cat "$out/budgets.md"
