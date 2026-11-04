#!/usr/bin/env python3
"""Drives the released diff-match-patch package as a black box for the oracle harness.

  dmp_oracle.py version
  dmp_oracle.py make  OLD_FILE NEW_FILE OUT_FILE    writes patch_toText(patch_make(old, new))
  dmp_oracle.py apply PATCH_FILE TEXT_FILE OUT_FILE writes the patched text; exits 3 when any hunk did not apply

All files are UTF-8. The package is only imported and called through its public functions.
"""
import importlib.metadata
import sys

from diff_match_patch import diff_match_patch


def read(path):
    with open(path, "rb") as f:
        return f.read().decode("utf-8")


def write(path, text):
    with open(path, "wb") as f:
        f.write(text.encode("utf-8"))


def main(argv):
    cmd = argv[1]
    if cmd == "version":
        print(importlib.metadata.version("diff-match-patch"))
        return 0
    dmp = diff_match_patch()
    if cmd == "make":
        old, new = read(argv[2]), read(argv[3])
        write(argv[4], dmp.patch_toText(dmp.patch_make(old, new)))
        return 0
    if cmd == "apply":
        patches = dmp.patch_fromText(read(argv[2]))
        result, applied = dmp.patch_apply(patches, read(argv[3]))
        write(argv[4], result)
        return 0 if all(applied) else 3
    print("unknown command", cmd, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
