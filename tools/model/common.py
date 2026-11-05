"""Shared pieces of the pair-model tools: reading the dumps, the cross-encoder with its three heads, batching.

The dumps are written by `OneDiff.GoldSets dump-model`. Two kinds of record:

  cand    an ambiguous pair of blocks the scorer is asked about: texts with the word diff marked inline, label 1 when the gold
          alignment links the two blocks.
  clause  a clause with two or more change units: both sides with units in ⟦ ⟧, atomic edits in « » and ¦ between neighbouring
          units; `whole` says one gold unit is the clause, `join[i]` that unit i+1 belongs to the same gold unit as unit i.
"""
import hashlib
import json
import random
from dataclasses import dataclass, field

import torch
import torch.nn as nn

GAP = "\u00a6"
MODEL = "jhu-clsp/mmBERT-base"
MAX_LEN = 320
MAX_GAPS = 48


def read_jsonl(path):
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.strip():
                yield json.loads(line)


def doc_of(rec):
    """The base document a record came from: pair ids are lang-doc-(m|sN)."""
    p = rec["pair"].rsplit("-", 1)[0]
    return p


def load_train(paths, val_fraction_per_mille=100):
    """Reads the dumps; returns (train, val) lists of examples, split by base document, deduplicated by key within a split.

    A record that appears with the same key and a different label (the same two texts are a match in one pair and not in
    another) is kept once with the majority label (ties: 0).
    """
    by_key = {}
    for src_i, path in enumerate(paths):
        tag = f"s{src_i}"
        for r in read_jsonl(path):
            d = tag + ":" + doc_of(r)
            val = int(hashlib.sha256(d.encode()).hexdigest()[:6], 16) % 1000 < val_fraction_per_mille
            k = (val, r["t"], r["key"])
            by_key.setdefault(k, []).append(r)
    train, val = [], []
    for (is_val, t, key), recs in sorted(by_key.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2])):
        r = recs[0]
        if t == "cand":
            lab = sum(x["label"] for x in recs) * 2 > len(recs)
            ex = {"t": "cand", "key": key, "a": f"<{r['oldStruct']}> {r['old']}", "b": f"<{r['newStruct']}> {r['new']}",
                  "label": float(lab), "n": len(recs), "lang": r["lang"], "gold": r["gold"], "rules": r["rules"]}
        else:
            # All records with this key agree: the key contains the texts, the extents and the units.
            ex = {"t": "clause", "key": key, "a": r["old"], "b": r["new"], "whole": float(r["whole"]),
                  "join": [float(x) for x in r["join"]], "n": len(recs), "lang": r["lang"],
                  "rulesWhole": r["rulesWhole"], "rulesJoin": r["rulesJoin"]}
        (val if is_val else train).append(ex)
    return train, val


def encode(tok, ex):
    """Token ids for one example plus the token index of each gap marker (per side) for clause examples."""
    enc = tok(ex["a"], ex["b"], truncation="longest_first", max_length=MAX_LEN, return_offsets_mapping=True, return_token_type_ids=False)
    item = {"ids": enc["input_ids"]}
    if ex["t"] == "clause":
        seq = enc.sequence_ids()
        offs = enc["offset_mapping"]
        pos = []
        for side, text in ((0, ex["a"]), (1, ex["b"])):
            chars = [i for i, c in enumerate(text) if c == GAP]
            toks = []
            for c in chars:
                t = -1
                for ti, (s, e) in enumerate(offs):
                    if seq[ti] == side and s <= c < e:
                        t = ti
                        break
                toks.append(t)
            pos.append(toks)
        n = len(ex["join"])
        gp = []
        for g in range(min(n, MAX_GAPS)):
            gp.append((pos[0][g] if g < len(pos[0]) else -1, pos[1][g] if g < len(pos[1]) else -1))
        item["gaps"] = gp
    return item


def collate(items, exs, pad_id, device):
    B = len(items)
    L = max(len(i["ids"]) for i in items)
    ids = torch.full((B, L), pad_id, dtype=torch.long)
    att = torch.zeros((B, L), dtype=torch.long)
    for b, it in enumerate(items):
        ids[b, : len(it["ids"])] = torch.tensor(it["ids"])
        att[b, : len(it["ids"])] = 1
    G = max([len(i.get("gaps", [])) for i in items] + [1])
    gpos = torch.full((B, G, 2), -1, dtype=torch.long)
    glab = torch.zeros((B, G))
    for b, (it, ex) in enumerate(zip(items, exs)):
        for g, (p0, p1) in enumerate(it.get("gaps", [])):
            gpos[b, g, 0], gpos[b, g, 1] = p0, p1
            if "join" in ex:
                glab[b, g] = ex["join"][g]
    kind = torch.tensor([0 if e["t"] == "cand" else 1 for e in exs])
    y_match = torch.tensor([e.get("label", 0.0) for e in exs])
    y_whole = torch.tensor([e.get("whole", 0.0) for e in exs])
    return {k: v.to(device) for k, v in dict(ids=ids, att=att, gpos=gpos, glab=glab, kind=kind, y_match=y_match, y_whole=y_whole).items()}


class PairModel(nn.Module):
    """mmBERT with three linear heads: match (CLS), whole-clause (CLS), join-with-previous (at the ¦ marker of each side)."""

    def __init__(self, name=MODEL):
        super().__init__()
        from transformers import AutoModel
        self.enc = AutoModel.from_pretrained(name, attn_implementation="sdpa")
        h = self.enc.config.hidden_size
        self.match = nn.Linear(h, 1)
        self.whole = nn.Linear(h, 1)
        self.gap = nn.Linear(h, 1)

    def forward(self, batch):
        hid = self.enc(input_ids=batch["ids"], attention_mask=batch["att"]).last_hidden_state
        cls = hid[:, 0]
        m = self.match(cls).squeeze(-1)
        w = self.whole(cls).squeeze(-1)
        gpos = batch["gpos"]  # B,G,2
        B, G, _ = gpos.shape
        idx = gpos.clamp(min=0)
        take = torch.gather(hid, 1, idx.view(B, G * 2, 1).expand(-1, -1, hid.shape[-1])).view(B, G, 2, -1)
        ok = (gpos >= 0).unsqueeze(-1).to(take.dtype)
        pooled = (take * ok).sum(2) / ok.sum(2).clamp(min=1)
        gmask = (gpos >= 0).any(-1)
        g = self.gap(pooled).squeeze(-1)
        return m, w, g, gmask


def losses(out, batch):
    m, w, g, gmask = out
    bce = nn.functional.binary_cross_entropy_with_logits
    cand = batch["kind"] == 0
    clause = ~cand
    zero = m.sum() * 0
    l_match = bce(m[cand].float(), batch["y_match"][cand]) if cand.any() else zero
    l_whole = bce(w[clause].float(), batch["y_whole"][clause]) if clause.any() else zero
    gm = gmask & clause.unsqueeze(-1)
    l_gap = bce(g[gm].float(), batch["glab"][gm]) if gm.any() else zero
    return l_match, l_whole, l_gap


def auc(scores, labels):
    """Area under the ROC curve by ranks (ties get average rank)."""
    pairs = sorted(zip(scores, labels))
    n_pos = sum(1 for _, l in pairs if l > 0.5)
    n_neg = len(pairs) - n_pos
    if n_pos == 0 or n_neg == 0:
        return float("nan")
    rank_sum = 0.0
    i = 0
    while i < len(pairs):
        j = i
        while j + 1 < len(pairs) and pairs[j + 1][0] == pairs[i][0]:
            j += 1
        avg = (i + j) / 2 + 1
        rank_sum += avg * sum(1 for k in range(i, j + 1) if pairs[k][1] > 0.5)
        i = j + 1
    return (rank_sum - n_pos * (n_pos + 1) / 2) / (n_pos * n_neg)


def set_seed(seed):
    random.seed(seed)
    torch.manual_seed(seed)
    torch.cuda.manual_seed_all(seed)
