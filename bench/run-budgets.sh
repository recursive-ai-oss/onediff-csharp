#!/usr/bin/env bash
# Times the document budgets, one command, with the machine record (CPU, cores, ISA flags, load before, during and after).
#
#   (a) markup    a 100-page PDF pair with about 300 changes: read both, compare, mark the new document's pages
#   (b) rendered  the same pair to a rendered redline PDF, inline and side by side, with the peak memory of each
#   (c) docx      a 100-page DOCX contract with about 300 changes to a tracked-changes DOCX (information only)
#   (d) memory    peak resident set above a process holding only the inputs, per page, from (a) and (b)
#
# Each part runs in its own process (so its peak memory is its own) on 4 cores (DOTNET_PROCESSOR_COUNT=4), with one untimed warm-up
# and 3 timed repetitions; the table reports the median. Budgets: (a) at most 2 s, (b) at most 2 s inline with a peak of at most 1 GB.
#
# The peak is the operating system's own maximum resident set size of the process, read from /usr/bin/time (-l on macOS, -v on Linux)
# or, on a Linux without GNU time, from VmHWM in /proc/PID/status. It is never taken from the .NET process counters, which read 0 on
# macOS. A run that cannot measure it prints "unmeasured" in the table and exits non-zero, after the timings are written.
#
# Usage: run-budgets.sh OUT
# Environment:
#   ONEDIFF_BUDGET_PAGES  pages of the pair (default 100); use 10 for a quick check
#   ONEDIFF_BUDGET_REPS   timed repetitions (default 3)
#   ONEDIFF_ARTIFACTS     where the tests are built (they are built here when missing)
# Writes OUT/machine.txt, OUT/<part>.txt and OUT/<part>.peak for each part, and OUT/budgets.md (the table).
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

# peak_of_run NAME COMMAND...: runs the command; writes its peak resident set in MB to OUT/NAME.peak when it can be measured.
peak_of_run() {
  local name="$1"; shift
  local tf="$out/$name.time" code=0 kb=""
  rm -f "$out/$name.peak" "$tf"
  case "$(uname -s)" in
    Darwin)
      /usr/bin/time -l -o "$tf" "$@" || code=$?
      # BSD time reports bytes.
      kb=$(awk '/maximum resident set size/ {printf "%d", $1 / 1024}' "$tf" 2>/dev/null || true) ;;
    *)
      if [ -x /usr/bin/time ] && /usr/bin/time -v -o "$tf.probe" true >/dev/null 2>&1; then
        rm -f "$tf.probe"
        /usr/bin/time -v -o "$tf" "$@" || code=$?
        kb=$(awk -F: '/Maximum resident set size/ {gsub(/ /, "", $2); print $2}' "$tf" 2>/dev/null || true)
      else
        # No GNU time: a watcher keeps the largest VmHWM it saw while the command ran.
        "$@" &
        local pid=$! hwm=0 v
        while kill -0 "$pid" 2>/dev/null; do
          v=$(awk '/VmHWM/ {print $2}' "/proc/$pid/status" 2>/dev/null || true)
          [ -n "$v" ] && hwm=$v
          sleep 0.05
        done
        wait "$pid" || code=$?
        echo "$hwm" > "$tf"
        kb=$hwm
      fi ;;
  esac
  if [ -n "$kb" ] && [ "$kb" -gt 0 ] 2>/dev/null; then echo $((kb / 1024)) > "$out/$name.peak"; fi
  return $code
}

run_part() { # part
  ONEDIFF_BUDGET_PART="$1" ONEDIFF_BUDGET_DIR="$dir" ONEDIFF_BUDGET_OUT="$out/$1.txt" \
    peak_of_run "$1" dotnet "$pdf_dll" -class OneDiff.Pdf.Tests.BudgetTests > "$out/$1.log" 2>&1 || { tail -20 "$out/$1.log"; return 1; }
}
run_part generate
for part in baseline markup rendered-inline rendered-side-by-side; do run_part "$part"; done
ONEDIFF_WORD_TIMING="$out/docx.txt" peak_of_run docx dotnet "$word_dll" -method OneDiff.Word.Tests.TimingTests.ReadAndCompareAHundredPages > "$out/docx.log" 2>&1 \
  || { tail -20 "$out/docx.log"; exit 1; }

# The table: medians over the timed repetitions, against the proposed budgets.
median() { sed -n 's/.*median_total_ms=\([0-9]*\).*/\1/p' "$1"; }
verdict() { if [ "$1" -le "$2" ]; then echo pass; else echo "over budget"; fi; }
unmeasured=0
peak() { if [ -s "$out/$1.peak" ] && [ "$(cat "$out/$1.peak")" -gt 0 ]; then cat "$out/$1.peak"; else unmeasured=1; echo unmeasured; fi; }
# Bytes per page: the part's peak above the baseline process (which holds only the inputs), over the pages; refused unless positive.
perpage() {
  local a b; a=$(peak "$1"); b=$(peak baseline)
  if [ "$a" = unmeasured ] || [ "$b" = unmeasured ] || [ "$a" -le "$b" ]; then echo unmeasured; else echo $(( (a - b) * 1048576 / pages_new )); fi
}
pages_new=$(sed -n 's/.*pages_new=\([0-9]*\).*/\1/p' "$out/baseline.txt" | head -1)
docx_median=$(grep '^rep [1-9]' "$out/docx.txt" | sed -n 's/.*read(2)=\([0-9]*\)ms compare=\([0-9]*\)ms.*tracked-docx=\([0-9]*\)ms.*/\1 \2 \3/p' | awk '{print $1+$2+$3}' | sort -n | awk '{t[NR]=$1} END {print t[int((NR+1)/2)]}')

rows=$(
  m=$(median "$out/markup.txt"); echo "| (a) PDF pair read + compare + on-page markup | $m | $(peak markup) | $(perpage markup) | 2000 ms | $(verdict "$m" 2000) |"
  m=$(median "$out/rendered-inline.txt"); pk=$(peak rendered-inline)
  if [ "$pk" = unmeasured ]; then pv=unmeasured; else pv=$(verdict "$pk" 1024); fi
  echo "| (b) same, rendered redline inline | $m | $pk | $(perpage rendered-inline) | 2000 ms, peak 1024 MB | $(verdict "$m" 2000) / $pv |"
  m=$(median "$out/rendered-side-by-side.txt"); echo "| (b) same, rendered redline side by side | $m | $(peak rendered-side-by-side) | $(perpage rendered-side-by-side) | information | — |"
  echo "| (c) DOCX contract read + compare + tracked DOCX (information, not in this release) | ${docx_median:-n/a} | $(peak docx) | — | information | — |"
)
{
  echo "# Budgets (pages: $pages, repetitions: $reps, DOTNET_PROCESSOR_COUNT=4)"
  echo
  echo "| Row | Median ms | Peak resident MB | Bytes per page above the inputs | Budget | Verdict |"
  echo "|---|---|---|---|---|---|"
  echo "$rows"
  echo
  echo "Peak is the process's maximum resident set size from the operating system. Machine record: machine.txt. Raw lines per repetition: markup.txt, rendered-inline.txt, rendered-side-by-side.txt, docx.txt."
} > "$out/budgets.md"
cat "$out/budgets.md"
if grep -q unmeasured "$out/budgets.md"; then
  echo "run-budgets: peak memory could not be measured on this machine (see $out/*.time); the timings above are valid" >&2
  exit 1
fi
