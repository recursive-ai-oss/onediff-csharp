#!/usr/bin/env bash
# Runs the W3C Nu Html Checker (vnu 26.11.4, MIT) over HTML files, as a separate check of the pages the writers produce.
# Usage: tools/html-validate.sh <file-or-directory>...
# Fetches the pinned Linux build (embedded Java runtime) into ${ONEDIFF_HOME:-$HOME/onediff}/validator on first use.
# Output: every message, then "errors=N warnings=M files=F". Exit status 1 when there is any error or warning.
set -euo pipefail

home="${ONEDIFF_HOME:-$HOME/onediff}/validator"
zip_sha256="caa4d89d37293927a605a44bcee85470bbc315f35a72d0047c96febea2c22661"
url="https://github.com/validator/validator/releases/download/latest/vnu.linux.zip"

if [ ! -x "$home/vnu-runtime-image/bin/vnu" ]; then
  mkdir -p "$home"
  curl -fsSL -o "$home/vnu.linux.zip" "$url"
  echo "$zip_sha256  $home/vnu.linux.zip" | sha256sum -c - >/dev/null \
    || { echo "vnu.linux.zip does not match the pinned checksum; the release has moved" >&2; exit 2; }
  (cd "$home" && unzip -q -o vnu.linux.zip)
fi

out="$(mktemp "${TMPDIR:-/tmp}/vnu.XXXXXX")"
trap 'rm -f "$out"' EXIT
"$home/vnu-runtime-image/bin/vnu" --format text "$@" >"$out" 2>&1 || true
cat "$out"
errors=$(grep -c '^.*error: ' "$out" || true)
warnings=$(grep -c '^.*warning: ' "$out" || true)
files=0
for p in "$@"; do
  if [ -d "$p" ]; then files=$((files + $(find "$p" -name '*.html' | wc -l))); else files=$((files + 1)); fi
done
echo "errors=$errors warnings=$warnings files=$files"
[ "$errors" -eq 0 ] && [ "$warnings" -eq 0 ]
