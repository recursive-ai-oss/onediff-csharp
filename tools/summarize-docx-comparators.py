#!/usr/bin/env python3
"""Turns the scorer's per-tool output and the runners' logs into one markdown results page.

    summarize-docx-comparators.py CMP_DIR READ_DIR SCORE_DIR tool...

CMP_DIR holds <tool>/runs.jsonl from the runners, READ_DIR <tool>/roundtrip.jsonl from `OneDiff.GoldSets.Docx read`, SCORE_DIR
<tool>/{all,completed,look0..look5}.txt from score-docx-comparators.sh.
"""
import json, os, re, statistics, sys

cmp_dir, read_dir, score_dir = sys.argv[1:4]
tools = sys.argv[4:]
ROWS = ["align.F1.same", "align.F1.edited", "align.F1.moved", "align.F1.split", "align.F1.merged", "align.F1.unrelated",
        "boundary.F1", "boundary.P", "boundary.R", "frag.sentence", "frag.unit", "missed.sentence"]
LINE = re.compile(r"^all\s+(\S+)\s+([-\d.]+)\s+\[\s*([-\d.]+),\s*([-\d.]+)\]")


def rows(path):
    out = {}
    for l in open(path):
        m = LINE.match(l)
        if m:
            out[m.group(1)] = (float(m.group(2)), float(m.group(3)), float(m.group(4)))
    return out


def cell(r, k):
    return "%.3f [%.3f, %.3f]" % r[k] if k in r else "-"


print("## Run and round-trip\n")
print("| Tool | Pairs | Ran | Failed | Median ms (ok) | Opened | Accept-all = new | Reject-all = old | Revisions (median) |")
print("|---|---|---|---|---|---|---|---|---|")
for t in tools:
    runs = [json.loads(l) for l in open(os.path.join(cmp_dir, t, "runs.jsonl")) if l.strip()]
    rt = [json.loads(l) for l in open(os.path.join(read_dir, t, "roundtrip.jsonl")) if l.strip()]
    ok = [r for r in runs if r["Ok"]]
    n = len(runs)
    print("| %s | %d | %d (%.1f%%) | %d | %.0f | %d | %d (%.1f%%) | %d (%.1f%%) | %.0f |" % (
        t, n, len(ok), 100 * len(ok) / n, n - len(ok), statistics.median(r["Milliseconds"] for r in ok), sum(r["opened"] for r in rt),
        sum(r["acceptParagraphs"] for r in rt), 100 * sum(r["acceptParagraphs"] for r in rt) / n,
        sum(r["rejectParagraphs"] for r in rt), 100 * sum(r["rejectParagraphs"] for r in rt) / n,
        statistics.median(r["revisions"] for r in rt)))
print("\nAccept and reject rates are over all pairs (a pair with no output counts as a miss); paragraph sequences compared through OneOffice's accept-all and reject-all.")
for scope, title in (("all", "All pairs (a pair without output scores as an empty result)"), ("completed", "Completed pairs only")):
    print("\n## %s\n" % title)
    print("| Row | " + " | ".join(tools) + " |")
    print("|---|" + "---|" * len(tools))
    data = {t: rows(os.path.join(score_dir, t, scope + ".txt")) for t in tools}
    for k in ROWS:
        print("| %s | %s |" % (k, " | ".join(cell(data[t], k) for t in tools)))
print("\n## Per rendering look (1,000 resamples)\n")
for k in ("align.F1.edited", "boundary.F1", "frag.sentence", "missed.sentence"):
    print("\n**%s**\n" % k)
    print("| Look | " + " | ".join(tools) + " |")
    print("|---|" + "---|" * len(tools))
    for l in range(6):
        vals = []
        for t in tools:
            p = os.path.join(score_dir, t, "look%d.txt" % l)
            vals.append(cell(rows(p), k) if os.path.exists(p) else "-")
        print("| look %d | %s |" % (l, " | ".join(vals)))
