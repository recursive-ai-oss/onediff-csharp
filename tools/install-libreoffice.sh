#!/usr/bin/env bash
# Installs a pinned LibreOffice release for the black-box comparison rows. Nothing is installed system-wide and nothing needs root.
#
#   install-libreoffice.sh [ROOT]      ROOT defaults to $ONEDIFF_HOME, else ~/.cache/onediff
#
# The release archive comes from download.documentfoundation.org and is checked against the SHA-256 the project publishes beside it.
# Linux x64 unpacks the .deb files with dpkg-deb into ROOT/oracles/libreoffice-VERSION; macOS arm64 copies the application out of the
# disk image. LibreOffice is run only as an external process by the comparison harness; it is never referenced by a package.
set -euo pipefail
VERSION=26.8.2
ROOT="${1:-${ONEDIFF_HOME:-$HOME/.cache/onediff}}"
DEST="$ROOT/oracles/libreoffice-$VERSION"
BASE=https://download.documentfoundation.org/libreoffice/stable/$VERSION
mkdir -p "$DEST" "$ROOT/downloads"
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)
    FILE=LibreOffice_${VERSION}_Linux_x86-64_deb.tar.gz
    URL=$BASE/deb/x86_64/$FILE
    ;;
  Darwin-arm64)
    FILE=LibreOffice_${VERSION}_MacOS_aarch64.dmg
    URL=$BASE/mac/aarch64/$FILE
    ;;
  *) echo "unsupported platform $(uname -sm)" >&2; exit 1 ;;
esac
if [ ! -f "$ROOT/downloads/$FILE" ]; then
  curl -fsSL -o "$ROOT/downloads/$FILE.part" "$URL"
  mv "$ROOT/downloads/$FILE.part" "$ROOT/downloads/$FILE"
fi
WANT=$(curl -fsSL "$URL.sha256" | awk '{print $1}')
if command -v sha256sum >/dev/null; then GOT=$(sha256sum "$ROOT/downloads/$FILE" | awk '{print $1}'); else GOT=$(shasum -a 256 "$ROOT/downloads/$FILE" | awk '{print $1}'); fi
if [ "$WANT" != "$GOT" ]; then echo "checksum mismatch for $FILE: published $WANT, got $GOT" >&2; exit 1; fi
echo "$FILE sha256 $GOT (matches the published value)"
if [ ! -e "$DEST/.installed" ]; then
  case "$(uname -s)" in
    Linux)
      TMPD=$(mktemp -d "$ROOT/downloads/unpack.XXXXXX")
      tar -xzf "$ROOT/downloads/$FILE" -C "$TMPD"
      for deb in "$TMPD"/*/DEBS/*.deb; do dpkg-deb -x "$deb" "$DEST"; done
      rm -rf "$TMPD"
      ;;
    Darwin)
      MNT=$(mktemp -d "$ROOT/downloads/mnt.XXXXXX")
      hdiutil attach -nobrowse -readonly -mountpoint "$MNT" "$ROOT/downloads/$FILE" >/dev/null
      cp -R "$MNT/LibreOffice.app" "$DEST/"
      hdiutil detach "$MNT" >/dev/null
      rmdir "$MNT" || true
      ;;
  esac
  touch "$DEST/.installed"
fi
SOFFICE=$(find "$DEST" -name soffice -type f -o -name soffice -type l | head -1)
echo "soffice: $SOFFICE"
"$SOFFICE" --headless --version
