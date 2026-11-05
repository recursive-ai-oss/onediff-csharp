#!/bin/bash
# Dump a pair's changes and, for one change, the glyphs under its new-side node anchor. Usage: dev-explain.sh <pair-id> <change-id>
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 ONEDIFF_PDF_GATE_DIR=$HOME/onediff/eval/pdf-v1
T=$HOME/onediff/u9/art/bin/OneDiff.Pdf.Tests/release/OneDiff.Pdf.Tests.dll
ONEDIFF_PDF_GATE_EXPLAIN=$1 ONEDIFF_PDF_GATE_CHANGE=$2 ONEDIFF_PDF_GATE_OUT=$HOME/onediff/u9/ex-$1.txt dotnet "$T" -method '*ExplainOnePair*' | tail -1
grep '^node' "$HOME/onediff/u9/ex-$1.txt"
grep glyph "$HOME/onediff/u9/ex-$1.txt" | awk '{printf "%s%s ", $2,$3}' | tr -d "'" | cut -c1-900
echo
