#!/usr/bin/env python3
"""Runs the Open-XML-PowerTools comparison engine (WmlComparer) on a directory of document pairs through python-redlines.

    wmlcomparer.py --docx DIR --out DIR [--jobs N] [--limit N] [--ids FILE] [--timeout SECONDS]
    wmlcomparer.py --versions

The engine is a compiled binary shipped in the python-redlines-ooxmlpowertools wheel; this script only calls the package's public
`XmlPowerToolsEngine.run_redline` and never looks at the installed files. For each pair <id>.old.docx / <id>.new.docx it writes
OUT/wmlcomparer/<id>.docx and appends one line per pair to OUT/wmlcomparer/runs.jsonl in the same shape the .NET runner uses.
A pair that is already recorded is skipped, so an interrupted run resumes.
"""

import argparse
import json
import multiprocessing
import os
import sys
import time
from importlib import metadata

TOOL = "wmlcomparer"
AUTHOR = "comparator"


def versions():
    for name in ("python-redlines", "python-redlines-ooxmlpowertools"):
        print(name, metadata.version(name))


def compare(job):
    id_, docx, out = job
    from python_redlines import XmlPowerToolsEngine

    start = time.monotonic()
    try:
        with open(os.path.join(docx, id_ + ".old.docx"), "rb") as f:
            original = f.read()
        with open(os.path.join(docx, id_ + ".new.docx"), "rb") as f:
            modified = f.read()
        result, _stdout, _stderr = XmlPowerToolsEngine().run_redline(AUTHOR, original, modified)
        with open(os.path.join(out, id_ + ".docx"), "wb") as f:
            f.write(result)
        return dict(Id=id_, Tool=TOOL, Ok=True, Milliseconds=(time.monotonic() - start) * 1000, OutputBytes=len(result), Error=None)
    except Exception as e:  # a failed pair is a result, not a reason to stop
        return dict(Id=id_, Tool=TOOL, Ok=False, Milliseconds=(time.monotonic() - start) * 1000, OutputBytes=0, Error=(type(e).__name__ + ": " + str(e)).replace("\n", " ")[:400])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--versions", action="store_true")
    ap.add_argument("--docx")
    ap.add_argument("--out")
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--ids")
    ap.add_argument("--timeout", type=float, default=180)
    a = ap.parse_args()
    if a.versions:
        versions()
        return 0
    out = os.path.join(os.path.abspath(a.out), TOOL)
    os.makedirs(out, exist_ok=True)
    ids = sorted(f[: -len(".old.docx")] for f in os.listdir(a.docx) if f.endswith(".old.docx"))
    if a.ids:
        want = set(open(a.ids).read().split())
        ids = [i for i in ids if i in want]
    if a.limit:
        ids = ids[: a.limit]
    log = os.path.join(out, "runs.jsonl")
    done = set()
    if os.path.exists(log):
        done = {json.loads(l)["Id"] for l in open(log) if l.strip()}
    todo = [(i, a.docx, out) for i in ids if i not in done]
    results = []
    with multiprocessing.Pool(a.jobs) as pool:
        pending = [(j, pool.apply_async(compare, (j,))) for j in todo]
        for j, r in pending:
            try:
                results.append(r.get(timeout=a.timeout))
            except multiprocessing.TimeoutError:
                results.append(dict(Id=j[0], Tool=TOOL, Ok=False, Milliseconds=a.timeout * 1000, OutputBytes=0, Error="timeout"))
    with open(log, "a") as f:
        for r in sorted(results, key=lambda r: r["Id"]):
            f.write(json.dumps(r) + "\n")
    allr = [json.loads(l) for l in open(log) if l.strip()]
    print(f"{TOOL}: {len(allr)} pairs recorded, {sum(r['Ok'] for r in allr)} ok, {sum(not r['Ok'] for r in allr)} failed; this run {len(results)} pairs")
    for r in [r for r in results if not r["Ok"]][:5]:
        print(r["Id"], r["Error"], file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
