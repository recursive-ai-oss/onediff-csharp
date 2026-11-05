#!/bin/bash
# Build the PDF tests and run the pdf-v1 gate runner over a slice. Usage: dev-gate.sh <out-name> [from] [count]
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_PROCESSOR_COUNT=4 ONEDIFF_ARTIFACTS=$HOME/onediff/u9/art
export ONEDIFF_PDF_GATE_DIR=$HOME/onediff/eval/pdf-v1 ONEDIFF_PDF_GATE_OUT=$HOME/onediff/u9/$1.txt ONEDIFF_PDF_GATE_FROM=${2:-0} ONEDIFF_PDF_GATE_COUNT=${3:-100000} ONEDIFF_PDF_GATE_JOBS=4
(cd tests/OneDiff.Pdf.Tests && dotnet build -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | tail -4) || exit 1
rm -f "$ONEDIFF_PDF_GATE_OUT"*
dotnet "$HOME/onediff/u9/art/bin/OneDiff.Pdf.Tests/release/OneDiff.Pdf.Tests.dll" -method '*EveryPairOfTheDirectory*' | tail -3
wc -l "$ONEDIFF_PDF_GATE_OUT"
cut -c1-250 "$ONEDIFF_PDF_GATE_OUT.failures" 2>/dev/null | head -${LINES_MAX:-20}
