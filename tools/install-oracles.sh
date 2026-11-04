#!/usr/bin/env bash
# Installs the reference tools the oracle harness runs as black boxes, at pinned versions, from released binary
# packages only (conda-forge binaries through a user-local micromamba; the diff-match-patch wheel from PyPI).
# Nothing is installed system-wide and no source is fetched.
#
# Usage: install-oracles.sh [ROOT]
#   ROOT defaults to $ONEDIFF_HOME, else ~/.cache/onediff. The tools land in ROOT/oracles:
#     env/bin/{git,diff,diff3,patch}   dmp-venv/bin/python (with diff-match-patch)
# GPL tools (git, GNU diffutils, GNU patch) are only ever executed by the harness and benchmarks; nothing ships them.
set -euo pipefail

ROOT="${1:-${ONEDIFF_HOME:-$HOME/.cache/onediff}}"
O="$ROOT/oracles"
GIT_V=2.51.2
DIFFUTILS_V=3.12
PATCH_V=2.8
DMP_V=20241021
mkdir -p "$O/bin"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) plat=linux-64 ;;
  Darwin-arm64) plat=osx-arm64 ;;
  *) echo "unsupported platform $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

if [ ! -x "$O/bin/micromamba" ]; then
  curl -fsSL "https://micro.mamba.pm/api/micromamba/$plat/latest" | tar -xj -C "$O" bin/micromamba
fi
export MAMBA_ROOT_PREFIX="$O/mamba"
"$O/bin/micromamba" create -y -q -p "$O/env" -c conda-forge --override-channels \
  "git=$GIT_V" "diffutils=$DIFFUTILS_V" "patch=$PATCH_V" "python=3.13" uv >/dev/null
E="$O/env/bin"

# diff-match-patch: the released wheel only (no sdist, so no source is unpacked).
"$E/uv" venv -q --python "$E/python" "$O/dmp-venv"
VIRTUAL_ENV="$O/dmp-venv" "$E/uv" pip install -q --only-binary :all: "diff-match-patch==$DMP_V"

echo "== installed under $O ($plat)"
fail=0
check() { # name, expected substring, actual line
  printf '%-18s %s\n' "$1" "$3"
  case "$3" in *"$2"*) ;; *) echo "   expected $2" >&2; fail=1 ;; esac
}
check git "$GIT_V" "$("$E/git" --version)"
check diff "$DIFFUTILS_V" "$("$E/diff" --version | head -1)"
check diff3 "$DIFFUTILS_V" "$("$E/diff3" --version | head -1)"
check patch "$PATCH_V" "$("$E/patch" --version | head -1)"
check diff-match-patch "$DMP_V" "diff-match-patch $("$O/dmp-venv/bin/python" -c 'import importlib.metadata as m; print(m.version("diff-match-patch"))')"
exit $fail
