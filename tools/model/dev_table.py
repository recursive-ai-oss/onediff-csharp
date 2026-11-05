#!/usr/bin/env python3
"""Turns the paired score files (`score --vs-rules`) into the dev-check table: for each row, the rules' value, the variant's, the
difference with its paired-bootstrap 95% interval, and a verdict.

A row "beats" when the interval of (variant - rules) excludes 0 on the good side, "loses" when it excludes 0 on the bad side, and
"ties" when it contains 0. Good is higher for F1, precision, recall and accuracy; lower for fragmentation, regions per sentence
or unit, missed sentences and fallbacks. "same" marks a row where the two values are equal to six places.

usage: dev_table.py --name LABEL=FILE.paired.json [--name ...] [--scope all] > table.md
"""
import argparse
import json

LOWER = ("frag.", "regions.", "missed.", "verify.fallback")
ROWS = ["align.F1.same", "align.F1.edited", "align.F1.moved", "align.F1.split", "align.F1.merged", "align.F1.unrelated",
        "boundary.F1", "boundary.P", "boundary.R", "frag.unit", "frag.sentence", "missed.sentence", "regions.unit", "verify.pass", "verify.fallback"]


def verdict(metric, r):
    if r["lo"] is None or r["diff"] is None:
        return "n/a"
    if abs(r["diff"]) < 5e-7 and abs(r["lo"]) < 5e-7 and abs(r["hi"]) < 5e-7:
        return "same"
    lower = metric.startswith(LOWER)
    if r["lo"] > 0:
        return "loses" if lower else "beats"
    if r["hi"] < 0:
        return "beats" if lower else "loses"
    return "ties"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", action="append", required=True)
    ap.add_argument("--scope", default="all")
    a = ap.parse_args()
    for spec in a.name:
        label, path = spec.split("=", 1)
        with open(path, encoding="utf-8") as f:
            d = json.load(f)
        print(f"### {label} (scope {a.scope}; {d['resamples']} paired resamples over documents, seed {d['seed']})\n")
        print("| row | rules | variant | variant − rules | 95% CI | verdict |")
        print("|---|---|---|---|---|---|")
        by = {(r["scope"], r["metric"]): r for r in d["rows"]}
        for m in ROWS:
            r = by.get((a.scope, m))
            if r is None or r["baseline"] is None or r["value"] is None:
                continue
            lo = "—" if r["lo"] is None else f"{r['lo']:+.4f}"
            hi = "—" if r["hi"] is None else f"{r['hi']:+.4f}"
            print(f"| {m} | {r['baseline']:.4f} | {r['value']:.4f} | {r['diff']:+.4f} | [{lo}, {hi}] | {verdict(m, r)} |")
        print()


if __name__ == "__main__":
    main()
