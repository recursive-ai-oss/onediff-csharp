#!/usr/bin/env python3
"""Shrinks a base/ours/theirs triple while OneDiff and `git merge-file` still disagree for one style.

usage: ddmin.py <gate.dll> <git> <triple-dir> <style: merge|diff3|zdiff3> <align: builtin|git> <out-dir>
Runs the gate (a separate .NET process) on each candidate; git only ever runs as a process.
"""
import os, shutil, subprocess, sys

gate, git, src, style, align, out = sys.argv[1:7]
work = os.path.join(out, "work")
cand = os.path.join(work, "c", "0")
dotnet = os.environ.get("DOTNET", "dotnet")

def read(name):
    with open(os.path.join(src, name), "rb") as f:
        return f.read().decode("latin-1").splitlines(keepends=True)

files = {n: read(n) for n in ("base", "ours", "theirs")}

def disagrees(f):
    shutil.rmtree(os.path.join(work, "c"), ignore_errors=True)
    os.makedirs(cand)
    for n, lines in f.items():
        with open(os.path.join(cand, n), "wb") as fh:
            fh.write("".join(lines).encode("latin-1"))
    res = os.path.join(work, "res")
    shutil.rmtree(res, ignore_errors=True)
    subprocess.run([dotnet, gate, git, os.path.join(work, "c"), res, f"--align={align}", "--jobs=1"] + (["--pairs"] if style == "pairs" else []),
                   stdout=subprocess.DEVNULL, check=True)
    if style == "pairs":
        with open(os.path.join(res, "pairs.tsv")) as fh:
            return len(fh.read().strip()) > 0
    with open(os.path.join(res, "results.tsv")) as fh:
        for line in fh:
            parts = line.rstrip("\n").split("\t")
            if parts[1] == style:
                return parts[2] != "identical"
    return False

assert disagrees(files), "the triple does not disagree"
changed = True
while changed:
    changed = False
    for name in ("base", "ours", "theirs"):
        n = len(files[name])
        chunk = max(1, n // 2)
        while chunk >= 1:
            i = 0
            while i < len(files[name]):
                trial = dict(files)
                trial[name] = files[name][:i] + files[name][i + chunk:]
                if disagrees(trial):
                    files = trial
                    changed = True
                else:
                    i += chunk
            chunk //= 2
os.makedirs(out, exist_ok=True)
for n, lines in files.items():
    with open(os.path.join(out, n), "wb") as fh:
        fh.write("".join(lines).encode("latin-1"))
print({n: len(l) for n, l in files.items()})
