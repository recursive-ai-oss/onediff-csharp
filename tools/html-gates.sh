#!/usr/bin/env bash
# Worker gate: build the Documents tests, run the HTML sweeps (random + corpus), then validate the kept pages with vnu.
set -uo pipefail
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 DOTNET_PROCESSOR_COUNT=4
U=$HOME/onediff/u12
mkdir -p "$U/logs" "$U/dump" "$U/art"
export ONEDIFF_ARTIFACTS=$U/art
rm -rf "$U/dump"/* "$U/logs/report.txt"
cd "$(dirname "$0")/.."
echo "=== host"; uname -m; nproc; grep -m1 'model name' /proc/cpuinfo; uptime
echo "=== build"
nice dotnet build tests/OneDiff.Documents.Tests/OneDiff.Documents.Tests.csproj -c Release -v quiet -clp:NoSummary -m:4 2>&1 | tail -5
echo "=== sweeps"
ONEDIFF_HTML_CASES=${CASES:-12000} ONEDIFF_HTML_ALLSTYLES=1 ONEDIFF_TEXT_CORPUS=$HOME/onediff/corpora/text ONEDIFF_HTML_GIT_PAIRS=30 \
  ONEDIFF_HTML_DUMP=$U/dump ONEDIFF_HTML_REPORT=$U/logs/report.txt \
  dotnet test --no-build -c Release --project tests/OneDiff.Documents.Tests/OneDiff.Documents.Tests.csproj --filter-class OneDiff.Documents.Tests.Html.HtmlSweepTests 2>&1 | tail -15
cat "$U/logs/report.txt"
echo "=== all html tests"
dotnet test --no-build -c Release --project tests/OneDiff.Documents.Tests/OneDiff.Documents.Tests.csproj 2>&1 | tail -8
echo "=== validator"
ls "$U/dump" | wc -l
cp docs/samples/*.html "$U/dump/" 2>/dev/null
bash tools/html-validate.sh "$U/dump" > "$U/logs/vnu.txt" 2>&1; echo "vnu exit $?"; tail -3 "$U/logs/vnu.txt"; grep -E 'error|warning' "$U/logs/vnu.txt" | sed 's/^.*\(error\|warning\):/\1:/' | sort | uniq -c | sort -rn | head -12
