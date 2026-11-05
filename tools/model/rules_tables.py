#!/usr/bin/env python3
"""Writes the answer tables the scorer tool reads from a dump, using what the rules decided: the equivalence check of the
lookup path (scoring with these tables must give the engine's own rows, with every difference 0).

usage: rules_tables.py --data DUMP --out-dir DIR
"""
import argparse
import json
import os



def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", action="append", required=True)
    ap.add_argument("--out-dir", required=True)
    a = ap.parse_args()
    os.makedirs(a.out_dir, exist_ok=True)
    seen = set()
    with open(os.path.join(a.out_dir, "cand-scores.jsonl"), "w") as c, open(os.path.join(a.out_dir, "clause-answers.jsonl"), "w") as g:
        for p in a.data:
            for r in (json.loads(line) for line in open(p, encoding="utf-8") if line.strip()):
                k = (r["t"], r["key"])
                if k in seen:
                    continue
                seen.add(k)
                if r["t"] == "cand":
                    c.write(json.dumps({"key": r["key"], "score": r["rules"]}) + "\n")
                else:
                    g.write(json.dumps({"key": r["key"], "whole": r["rulesWhole"], "join": r["rulesJoin"]}) + "\n")


if __name__ == "__main__":
    main()
