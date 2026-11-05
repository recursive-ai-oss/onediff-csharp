#!/usr/bin/env python3
"""Runs the fine-tuned teacher over a dump and writes the answers the scorer tool reads.

  cand-scores.jsonl      {"key", "score"}              probability that the two blocks are one block edited
  clause-answers.jsonl   {"key", "whole", "join"}      one clause-wide unit, or per unit after the first: joins the one before

A gap whose marker was cut off by the length limit takes the rules' answer. Identical keys are predicted once.

usage: predict.py --ckpt best.pt --data DUMP [--data DUMP ...] --out-dir DIR
"""
import argparse
import json
import os

import torch

import common


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", required=True)
    ap.add_argument("--data", action="append", required=True)
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--model", default=common.MODEL)
    ap.add_argument("--batch", type=int, default=64)
    a = ap.parse_args()
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    from transformers import AutoTokenizer
    tok = AutoTokenizer.from_pretrained(a.model)
    model = common.PairModel(a.model).to(dev)
    model.load_state_dict(torch.load(a.ckpt, map_location=dev, weights_only=False)["model"])
    model.eval()

    seen = {}
    for path in a.data:
        for r in common.read_jsonl(path):
            seen.setdefault((r["t"], r["key"]), r)
    exs = []
    for (t, key), r in seen.items():
        if t == "cand":
            exs.append({"t": "cand", "key": key, "a": f"<{r['oldStruct']}> {r['old']}", "b": f"<{r['newStruct']}> {r['new']}"})
        else:
            exs.append({"t": "clause", "key": key, "a": r["old"], "b": r["new"], "join": r["rulesJoin"], "rulesJoin": r["rulesJoin"], "rulesWhole": r["rulesWhole"]})
    exs.sort(key=lambda e: (len(e["a"]) + len(e["b"]), e["key"]))
    os.makedirs(a.out_dir, exist_ok=True)
    cand_out = open(os.path.join(a.out_dir, "cand-scores.jsonl"), "w", encoding="utf-8")
    cl_out = open(os.path.join(a.out_dir, "clause-answers.jsonl"), "w", encoding="utf-8")
    cut = 0
    with torch.no_grad():
        for i in range(0, len(exs), a.batch):
            chunk = exs[i : i + a.batch]
            items = [common.encode(tok, e) for e in chunk]
            batch = common.collate(items, chunk, tok.pad_token_id, dev)
            with torch.autocast(dev, dtype=torch.bfloat16, enabled=dev == "cuda"):
                m, w, g, gmask = model(batch)
            pm = torch.sigmoid(m.float()).cpu().tolist()
            pw = (w.float() > 0).cpu().tolist()
            pg = (g.float() > 0).cpu().tolist()
            gk = gmask.cpu().tolist()
            for b, e in enumerate(chunk):
                if e["t"] == "cand":
                    cand_out.write(json.dumps({"key": e["key"], "score": round(pm[b], 6)}) + "\n")
                else:
                    join = []
                    for j, rj in enumerate(e["rulesJoin"]):
                        if j < common.MAX_GAPS and gk[b][j]:
                            join.append(bool(pg[b][j]))
                        else:
                            join.append(bool(rj))
                            cut += 1
                    cl_out.write(json.dumps({"key": e["key"], "whole": bool(pw[b]), "join": join}) + "\n")
    print(f"{sum(e['t']=='cand' for e in exs)} pairs, {sum(e['t']=='clause' for e in exs)} clauses; gaps answered by the rules after truncation: {cut}")


if __name__ == "__main__":
    main()
