#!/usr/bin/env python3
"""Prints the paragraphs of a tracked-changes DOCX compactly: =same +inserted -deleted >moved-to <moved-from, ¶+ / ¶- for a
paragraph mark inserted or deleted, [fmt] for a run property change. For looking at what a writer produced.

    show-tracked.py file.docx [part=word/document.xml]
"""
import re, sys, zipfile
from xml.etree import ElementTree as ET

W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
q = lambda n: "{%s}%s" % (W, n)


def text(r):
    out = ""
    for c in r:
        t = c.tag
        if t in (q("t"), q("delText")):
            out += c.text or ""
        elif t == q("tab"):
            out += "\\t"
        elif t == q("br"):
            out += "\\n"
        elif t in (q("drawing"), q("pict")):
            out += "{obj}"
        elif t == q("footnoteReference"):
            out += "{fn}"
        elif t == q("fldChar"):
            out += "{f:" + c.get(q("fldCharType"))[0] + "}"
    return out


def walk(e, mode, out):
    for c in e:
        t = c.tag
        if t == q("r"):
            s = text(c)
            if c.find(q("rPr") + "/" + q("rPrChange")) is not None:
                s = "[fmt:" + s + "]"
            if s:
                out.append(mode + s)
        elif t in (q("ins"), q("del"), q("moveTo"), q("moveFrom")):
            m = {"ins": "+", "del": "-", "moveTo": ">", "moveFrom": "<"}[t.split("}")[1]]
            walk(c, m, out)
        elif t in (q("moveFromRangeStart"), q("moveToRangeStart")):
            out.append("{" + t.split("}")[1][:-5] + ":" + (c.get(q("name")) or "") + "}")
        elif t in (q("pPr"), q("rPr")):
            continue
        else:
            walk(c, mode, out)


def main():
    path = sys.argv[1]
    part = sys.argv[2] if len(sys.argv) > 2 else "word/document.xml"
    root = ET.fromstring(zipfile.ZipFile(path).read(part))
    n = 0
    for p in root.iter(q("p")):
        out = []
        walk(p, "=", out)
        mark = ""
        ppr = p.find(q("pPr"))
        if ppr is not None:
            rpr = ppr.find(q("rPr"))
            if rpr is not None:
                for k in ("ins", "del", "moveTo", "moveFrom"):
                    if rpr.find(q(k)) is not None:
                        mark += " ¶" + {"ins": "+", "del": "-", "moveTo": ">", "moveFrom": "<"}[k]
            if ppr.find(q("pPrChange")) is not None:
                mark += " ¶[chg]"
        # merge adjacent same-mode pieces
        merged = []
        for s in out:
            if merged and s[0] == merged[-1][0] and s[0] in "=+-<>":
                merged[-1] += s[1:]
            else:
                merged.append(s)
        print("%3d %s%s" % (n, " ".join(m if len(m) < 90 else m[:60] + "…" + m[-25:] for m in merged), mark))
        n += 1


main()
