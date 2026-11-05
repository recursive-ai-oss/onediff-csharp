#!/usr/bin/env python3
"""Proves that no evaluation or development document is in the training data, and measures how much text they share.

Document level (must be zero, the script exits 1 otherwise):
  * a training document's base hash (the generator's hash of the unedited document) is also an eval/dev base hash;
  * a training document's normalized full text (the unedited document's paragraphs, in order) equals an eval/dev document's;
  * the same document id (language, number) carries the training role in a set and the eval or dev role in the same
    seed's split (synth-v1 and every new seed are checked against their own split.json).
Paragraph level (measured, reported, never zero by design: the generator draws sentences from a small set of templates and
value lists, so the same paragraph can be written independently for two documents):
  * the share of eval/dev paragraphs, by normalized-text hash, that also occur in the training pairs;
  * the same for paragraphs of eight or more words, and for paragraphs that contain a number or a defined term (the ones
    that are not boilerplate).

Eval sets are read wherever they are given with --eval: synth-v1 eval pairs (docs.json), docx-v1, pdf-v1, combine-v1 (any
JSON; every "text" member is a paragraph), the real pairs (old.txt/new.txt split into lines) and scan sets once present.

usage: check_train_data.py --train DIR [--train DIR ...] --eval DIR [--eval DIR ...] [--dev DIR] --out FILE.json
"""
import argparse
import glob
import hashlib
import json
import os
import re
import sys
import unicodedata

DROP = dict.fromkeys(map(ord, "\u00ad\u200b\u200c\u200d\u200e\u200f\u061c\u2060\ufeff"), None)


def norm(t: str) -> str:
    t = unicodedata.normalize("NFKC", t).translate(DROP)
    t = re.sub(r"\s+", " ", t).strip().casefold()
    return t


def h(t: str) -> str:
    return hashlib.sha256(t.encode("utf-8")).hexdigest()


def collect_texts(obj, out):
    """Every "text" string anywhere in a JSON value."""
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k == "text" and isinstance(v, str):
                out.append(v)
            else:
                collect_texts(v, out)
    elif isinstance(obj, list):
        for v in obj:
            collect_texts(v, out)


def dir_paragraphs(root):
    """All paragraph texts of an eval/dev/train directory, from whatever files it holds."""
    paras = []
    files = 0
    for path in sorted(glob.glob(os.path.join(root, "**", "*"), recursive=True)):
        if os.path.isdir(path):
            continue
        base = os.path.basename(path)
        if base.endswith(".json") and base not in ("manifest.json", "split.json"):
            with open(path, encoding="utf-8") as f:
                try:
                    collect_texts(json.load(f), paras)
                except json.JSONDecodeError:
                    continue
            files += 1
        elif base in ("old.txt", "new.txt"):
            with open(path, encoding="utf-8", errors="replace") as f:
                paras.extend(line for line in f.read().split("\n") if line.strip())
            files += 1
    return paras, files


def base_docs(root):
    """(lang, doc, role, baseHash) from split.json, the document list of a generated set."""
    p = os.path.join(root, "split.json")
    if not os.path.exists(p):
        return []
    with open(p, encoding="utf-8") as f:
        return [(e["lang"], e["doc"], e["role"], e["baseHash"]) for e in json.load(f)]


def base_texts(root):
    """Normalized full text of every unedited document of a generated set: the old side of each -m pair, in order."""
    result = {}
    for p in sorted(glob.glob(os.path.join(root, "pairs", "*.docs.json"))):
        with open(p, encoding="utf-8") as f:
            d = json.load(f)
        lab = p.replace(".docs.json", ".labels.json")
        with open(lab, encoding="utf-8") as f:
            l = json.load(f)
        # The unedited document is the old side of every pair of the document.
        text = "\n".join(norm(n["text"]) for n in d["old"]["nodes"] if n["text"].strip())
        result[(l["lang"], l["baseDoc"], l["baseHash"])] = h(text)
    return result


def stats(eval_paras, train_set):
    def share(sel):
        sel = [p for p in sel]
        if not sel:
            return {"paragraphs": 0, "distinct": 0, "inTrain": 0, "share": None, "distinctShare": None}
        d = set(sel)
        inn = sum(1 for p in sel if p in train_set)
        dd = sum(1 for p in d if p in train_set)
        return {"paragraphs": len(sel), "distinct": len(d), "inTrain": inn, "share": round(inn / len(sel), 4),
                "distinctInTrain": dd, "distinctShare": round(dd / len(d), 4)}
    return share


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--train", action="append", required=True)
    ap.add_argument("--eval", action="append", required=True)
    ap.add_argument("--dev", action="append", default=[])
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    report = {"train": {}, "heldOut": {}, "documentLevel": {}, "paragraphLevel": {}}
    ok = True

    # Training side.
    train_hashes = {}  # normalized paragraph hash -> count
    train_base = {}
    train_fulltext = {}
    train_roles = []
    for t in a.train:
        paras, files = dir_paragraphs(t)
        for p in paras:
            n = norm(p)
            if n:
                train_hashes[h(n)] = train_hashes.get(h(n), 0) + 1
        docs = base_texts(t)
        for key, full in docs.items():
            train_base.setdefault(key[2], []).append((t, key[0], key[1]))
            train_fulltext.setdefault(full, []).append((t, key[0], key[1]))
        for lang, doc, role, bh in base_docs(t):
            train_roles.append((t, lang, doc, role, bh))
        report["train"][t] = {"files": files, "paragraphs": len(paras), "documents": len(docs)}
    train_set = set(train_hashes)

    # Held-out side (eval and dev).
    held_base = {}
    held_full = {}
    held = {}
    for role, dirs in (("eval", a.eval), ("dev", a.dev)):
        for d in dirs:
            paras, files = dir_paragraphs(d)
            held[(role, d)] = [h(norm(p)) for p in paras if norm(p)]
            report["heldOut"][d] = {"role": role, "files": files, "paragraphs": len(paras)}
            for key, full in base_texts(d).items():
                held_base.setdefault(key[2], []).append((d, key[0], key[1]))
                held_full.setdefault(full, []).append((d, key[0], key[1]))
            for lang, doc, r, bh in base_docs(d):
                if r in ("eval", "dev"):
                    held_base.setdefault(bh, []).append((d + "#split", lang, doc))

    # Document level.
    shared_base = sorted(set(train_base) & set(held_base))
    shared_full = sorted(set(train_fulltext) & set(held_full))
    # A training directory's own split must not give a training document an eval/dev role: documents the directory lists as
    # eval/dev are never written into it, so any training-role pair of a document with another role is a leak.
    role_conflicts = []
    by_dir = {}
    for t, lang, doc, role, bh in train_roles:
        by_dir.setdefault(t, {})[(lang, doc)] = role
    for t in a.train:
        # which documents have pairs in this directory
        have = {(k[1], k[2]) for k in base_texts(t)}
        for (lang, doc) in have:
            role = by_dir.get(t, {}).get((lang, doc))
            if role is not None and role not in ("train",):
                role_conflicts.append((t, lang, doc, role))
    report["documentLevel"] = {
        "sharedBaseHashes": len(shared_base),
        "sharedNormalizedDocuments": len(shared_full),
        "trainDocumentsWithHeldOutRole": len(role_conflicts),
        "trainDocuments": sum(len(v) for v in train_base.values()),
        "heldOutDocuments": sum(len(v) for v in held_base.values()),
    }
    if shared_base or shared_full or role_conflicts:
        ok = False

    # Paragraph level.
    def words(t):
        return len(re.findall(r"\w+", t))
    share = stats(None, train_set)
    # Recover texts for classifying; recompute from hashes is impossible, so classify during a second pass.
    for (role, d), hashes in held.items():
        paras, _ = dir_paragraphs(d)
        normed = [norm(p) for p in paras if norm(p)]
        allh = [h(n) for n in normed]
        long_ = [h(n) for n in normed if words(n) >= 8 or len(n) >= 40]
        valued = [h(n) for n in normed if re.search(r"\d", n) and (words(n) >= 8 or len(n) >= 40)]
        report["paragraphLevel"][d] = {
            "role": role,
            "all": share(allh),
            "long": share(long_),
            "longWithNumber": share(valued),
        }
    report["ok"] = ok
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=1, sort_keys=True)
    print(json.dumps({"ok": ok, **report["documentLevel"]}, sort_keys=True))
    for d, r in report["paragraphLevel"].items():
        print(os.path.basename(d.rstrip("/")), r["role"], "all", r["all"]["share"], "long", r["long"]["share"], "long+number", r["longWithNumber"]["share"])
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
