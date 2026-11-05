#!/bin/bash
# Builds the three gold-set tools used by the scan gates into $ONEDIFF_ARTIFACTS and prints their paths.
# Runs on the worker as light work: low priority, capped build parallelism, and it stops itself if a benchmark starts.
set -u
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$HOME/onediff/u25/art}
[ -e "$HOME/WORKER-BENCH.lock" ] && { echo "a benchmark is running; not starting"; exit 3; }
(while sleep 30; do [ -e "$HOME/WORKER-BENCH.lock" ] && kill -- -$$ 2>/dev/null; done) &
WATCH=$!
for p in tools/OneDiff.GoldSets tools/OneDiff.GoldSets.Pdf tools/OneDiff.GoldSets.Scan tests/OneDiff.Ocr.Tests; do
  (cd "$p" && nice dotnet build -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | grep -E 'error|rror\(s\)|arning\(s\)' | sort -u)
done
kill $WATCH 2>/dev/null
ls "$ONEDIFF_ARTIFACTS/bin"
