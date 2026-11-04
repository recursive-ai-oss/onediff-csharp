#!/usr/bin/env python3
"""Prints a base/ours/theirs triple as strings of one letter per distinct line ('_' for an empty line)."""
import sys, os
d = sys.argv[1]
ids = {}
def tok(l):
    if l == b"\n": return "_"
    if l not in ids:
        ids[l] = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"[len(ids) % 62]
    return ids[l]
for n in ("base", "ours", "theirs"):
    data = open(os.path.join(d, n), "rb").read()
    lines = data.splitlines(keepends=True)
    print(n.ljust(7), "".join(tok(l) for l in lines), "(%d lines)" % len(lines))
