#!/usr/bin/env python3
"""Fine-tunes the teacher (mmBERT-base) as a pair cross-encoder on the two tasks the heads need, in resumable chunks.

One run directory holds `state.pt` (model, optimizer, schedule position, data order position), `best.pt` (the weights with the
lowest validation loss) and `curve.jsonl` (the loss curve). Each invocation trains until `--max-minutes` have passed (or the
schedule ends), saves, and exits; running it again continues from the saved step. The data order is a function of the seed and
the epoch, so a resumed run sees the same batches as an uninterrupted one.

Validation is a slice of the training data cut by base document; the synth-v1 dev split is used only by the dev check.

usage: train_teacher.py --run DIR --data DUMP [--data DUMP ...] [--epochs 3] [--max-minutes 100] [--batch 16] [--lr 3e-5] [--seed 1]
"""
import argparse
import json
import math
import os
import random
import time

import torch

import common


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--data", action="append", required=True)
    ap.add_argument("--epochs", type=float, default=3)
    ap.add_argument("--max-minutes", type=float, default=100)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--lr", type=float, default=3e-5)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--eval-every", type=int, default=1000)
    ap.add_argument("--limit", type=int, default=0, help="use only this many training examples (smoke runs)")
    ap.add_argument("--model", default=common.MODEL)
    a = ap.parse_args()

    t_start = time.time()
    os.makedirs(a.run, exist_ok=True)
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    common.set_seed(a.seed)
    from transformers import AutoTokenizer
    tok = AutoTokenizer.from_pretrained(a.model)

    train, val = common.load_train(a.data)
    if a.limit:
        random.Random(a.seed).shuffle(train)
        train, val = train[: a.limit], val[: max(64, a.limit // 8)]
    train_keys = {(e["t"], e["key"]) for e in train}
    val = [e for e in val if (e["t"], e["key"]) not in train_keys]
    print(f"train {len(train)} examples ({sum(e['t']=='cand' for e in train)} pairs, {sum(e['t']=='clause' for e in train)} clauses), "
          f"validation {len(val)}", flush=True)
    tr_items = [common.encode(tok, e) for e in train]
    va_items = [common.encode(tok, e) for e in val]
    print(f"encoded; max tokens {max(len(i['ids']) for i in tr_items)}", flush=True)

    model = common.PairModel(a.model).to(dev)
    decay = [p for n, p in model.named_parameters() if p.ndim >= 2]
    no_decay = [p for n, p in model.named_parameters() if p.ndim < 2]
    opt = torch.optim.AdamW([{"params": decay, "weight_decay": 0.01}, {"params": no_decay, "weight_decay": 0.0}], lr=a.lr, betas=(0.9, 0.98), eps=1e-6)

    steps_per_epoch = math.ceil(len(train) / a.batch)
    total = int(steps_per_epoch * a.epochs)
    warm = max(50, total // 20)

    def lr_at(s):
        if s < warm:
            return a.lr * (s + 1) / warm
        return a.lr * max(0.0, (total - s) / max(1, total - warm))

    state_path = os.path.join(a.run, "state.pt")
    step = 0
    best = float("inf")
    if os.path.exists(state_path):
        st = torch.load(state_path, map_location=dev, weights_only=False)
        model.load_state_dict(st["model"])
        opt.load_state_dict(st["opt"])
        step, best = st["step"], st["best"]
        print(f"resumed at step {step}/{total}", flush=True)

    def order(epoch):
        idx = list(range(len(train)))
        random.Random(a.seed * 1000 + epoch).shuffle(idx)
        return idx

    def save():
        tmp = state_path + ".tmp"
        torch.save({"model": model.state_dict(), "opt": opt.state_dict(), "step": step, "best": best}, tmp)
        os.replace(tmp, state_path)

    @torch.no_grad()
    def evaluate():
        model.eval()
        tot = [0.0, 0.0, 0.0]
        cnt = [0, 0, 0]
        ms, ml = [], []
        gp_ok = gp_n = 0
        wh_ok = wh_n = 0
        for i in range(0, len(val), 32):
            ex = val[i : i + 32]
            batch = common.collate(va_items[i : i + 32], ex, tok.pad_token_id, dev)
            with torch.autocast(dev, dtype=torch.bfloat16, enabled=dev == "cuda"):
                out = model(batch)
            lm, lw, lg = common.losses(out, batch)
            nm = int((batch["kind"] == 0).sum())
            nc = len(ex) - nm
            ng = int((out[3] & (batch["kind"] == 1).unsqueeze(-1)).sum())
            for j, (l, n) in enumerate(((lm, nm), (lw, nc), (lg, ng))):
                tot[j] += float(l) * n
                cnt[j] += n
            for b, e in enumerate(ex):
                if e["t"] == "cand":
                    ms.append(float(torch.sigmoid(out[0][b].float())))
                    ml.append(e["label"])
                else:
                    wh_n += 1
                    wh_ok += int((out[1][b] > 0).item() == (e["whole"] > 0.5))
                    for g in range(min(len(e["join"]), common.MAX_GAPS)):
                        if out[3][b, g]:
                            gp_n += 1
                            gp_ok += int((out[2][b, g] > 0).item() == (e["join"][g] > 0.5))
        model.train()
        r = {"val_match_loss": tot[0] / max(1, cnt[0]), "val_whole_loss": tot[1] / max(1, cnt[1]), "val_gap_loss": tot[2] / max(1, cnt[2]),
             "val_match_auc": common.auc(ms, ml), "val_whole_acc": wh_ok / max(1, wh_n), "val_gap_acc": gp_ok / max(1, gp_n)}
        r["val_loss"] = r["val_match_loss"] + r["val_whole_loss"] + r["val_gap_loss"]
        return r

    model.train()
    curve = open(os.path.join(a.run, "curve.jsonl"), "a", encoding="utf-8")
    run_loss = [0.0, 0.0, 0.0]
    run_n = 0
    peak = 0.0
    while step < total:
        epoch, within = divmod(step, steps_per_epoch)
        idx = order(epoch)
        lo = within * a.batch
        sel = idx[lo : lo + a.batch]
        exs = [train[i] for i in sel]
        batch = common.collate([tr_items[i] for i in sel], exs, tok.pad_token_id, dev)
        for g in opt.param_groups:
            g["lr"] = lr_at(step)
        with torch.autocast(dev, dtype=torch.bfloat16, enabled=dev == "cuda"):
            out = model(batch)
        lm, lw, lg = common.losses(out, batch)
        loss = lm + lw + lg
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        opt.step()
        opt.zero_grad(set_to_none=True)
        step += 1
        for j, l in enumerate((lm, lw, lg)):
            run_loss[j] += float(l)
        run_n += 1
        if dev == "cuda":
            peak = max(peak, torch.cuda.max_memory_allocated() / 2**30)
        if step % 50 == 0:
            rec = {"step": step, "epoch": round(step / steps_per_epoch, 3), "lr": lr_at(step), "match": run_loss[0] / run_n, "whole": run_loss[1] / run_n,
                   "gap": run_loss[2] / run_n, "peak_vram_gib": round(peak, 2), "elapsed_s": round(time.time() - t_start)}
            run_loss, run_n = [0.0, 0.0, 0.0], 0
            curve.write(json.dumps(rec) + "\n")
            curve.flush()
            if step % 500 == 0:
                print(json.dumps(rec), flush=True)
        done = step >= total
        out_of_time = (time.time() - t_start) / 60 > a.max_minutes
        if step % a.eval_every == 0 or done or out_of_time:
            r = evaluate()
            r.update({"step": step, "eval": True})
            curve.write(json.dumps(r) + "\n")
            curve.flush()
            print(json.dumps(r), flush=True)
            if r["val_loss"] < best:
                best = r["val_loss"]
                torch.save({"model": model.state_dict(), "step": step, "val": r}, os.path.join(a.run, "best.pt"))
            save()
        if out_of_time and not done:
            print(f"time budget reached at step {step}/{total}; run again to continue", flush=True)
            break
    print(f"peak VRAM {peak:.2f} GiB; step {step}/{total}; wall {time.time()-t_start:.0f}s", flush=True)


if __name__ == "__main__":
    main()
