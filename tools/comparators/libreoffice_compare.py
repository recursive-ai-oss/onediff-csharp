#!/usr/bin/env python3
"""Runs LibreOffice's Compare Document on a directory of document pairs, headless, through its UNO interface.

    program/python libreoffice_compare.py --soffice PATH --docx DIR --out DIR [--limit N] [--ids FILE] [--timeout SECONDS]

Run it with the Python that ships in LibreOffice's program directory (it carries the `uno` module). The script starts one
`soffice --headless` with its own profile, and for each pair <id>.old.docx / <id>.new.docx opens the NEW document, runs the
dispatch command `.uno:CompareDocuments` against the OLD one (LibreOffice's documented way: the open document is the revised
one, the chosen file is the original) and stores the result as DOCX in OUT/libreoffice/<id>.docx. One line per pair goes to
OUT/libreoffice/runs.jsonl, in the shape the other runners use. A pair that is already recorded is skipped. LibreOffice is only
executed: nothing of its installation is read.
"""

import argparse
import json
import os
import subprocess
import sys
import time

import uno
from com.sun.star.beans import PropertyValue

TOOL = "libreoffice"
PORT = 2002 + os.getpid() % 500


def prop(name, value):
    p = PropertyValue()
    p.Name = name
    p.Value = value
    return p


def start(soffice, profile):
    proc = subprocess.Popen(
        [soffice, "--headless", "--invisible", "--norestore", "--nologo", "--nodefault", "-env:UserInstallation=file://" + profile,
         "--accept=socket,host=127.0.0.1,port=%d;urp;" % PORT],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    local = uno.getComponentContext()
    resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
    for _ in range(120):
        try:
            ctx = resolver.resolve("uno:socket,host=127.0.0.1,port=%d;urp;StarOffice.ComponentContext" % PORT)
            return proc, ctx
        except Exception:
            time.sleep(0.5)
    proc.kill()
    raise RuntimeError("soffice did not start")


def compare(ctx, old, new, out):
    smgr = ctx.ServiceManager
    desktop = smgr.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)
    dispatcher = smgr.createInstanceWithContext("com.sun.star.frame.DispatchHelper", ctx)
    doc = desktop.loadComponentFromURL(uno.systemPathToFileUrl(new), "_blank", 0, (prop("Hidden", True),))
    try:
        frame = doc.getCurrentController().getFrame()
        dispatcher.executeDispatch(frame, ".uno:CompareDocuments", "", 0,
                                   (prop("URL", uno.systemPathToFileUrl(old)), prop("NoAcceptDialog", True)))
        doc.storeToURL(uno.systemPathToFileUrl(out), (prop("FilterName", "MS Word 2007 XML"),))
    finally:
        doc.close(True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--soffice", required=True)
    ap.add_argument("--docx", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--ids")
    ap.add_argument("--timeout", type=float, default=120)
    a = ap.parse_args()
    out = os.path.join(os.path.abspath(a.out), TOOL)
    os.makedirs(out, exist_ok=True)
    profile = os.path.join(out, ".profile")
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
    todo = [i for i in ids if i not in done]
    proc, ctx = start(a.soffice, profile)
    n = 0
    with open(log, "a") as f:
        for id_ in todo:
            target = os.path.join(out, id_ + ".docx")
            start_t = time.monotonic()
            try:
                compare(ctx, os.path.join(os.path.abspath(a.docx), id_ + ".old.docx"), os.path.join(os.path.abspath(a.docx), id_ + ".new.docx"), target)
                rec = dict(Id=id_, Tool=TOOL, Ok=True, Milliseconds=(time.monotonic() - start_t) * 1000, OutputBytes=os.path.getsize(target), Error=None)
            except Exception as e:
                rec = dict(Id=id_, Tool=TOOL, Ok=False, Milliseconds=(time.monotonic() - start_t) * 1000, OutputBytes=0, Error=(type(e).__name__ + ": " + str(e)).replace("\n", " ")[:400])
                if proc.poll() is not None:
                    proc, ctx = start(a.soffice, profile)
            f.write(json.dumps(rec) + "\n")
            f.flush()
            n += 1
    try:
        desktop = ctx.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)
        desktop.terminate()
    except Exception:
        pass
    try:
        proc.wait(30)
    except subprocess.TimeoutExpired:
        proc.kill()
    allr = [json.loads(l) for l in open(log) if l.strip()]
    print(f"{TOOL}: {len(allr)} pairs recorded, {sum(r['Ok'] for r in allr)} ok, {sum(not r['Ok'] for r in allr)} failed; this run {n} pairs")
    return 0


if __name__ == "__main__":
    sys.exit(main())
