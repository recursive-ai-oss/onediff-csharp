# Provenance: pair-model teacher `pair-teacher-v1`

What went into the teacher of the pair model and how it was checked. Kept current with every model change. Machine-readable twin:
`provenance.json`.

## Summary

| | |
|---|---|
| Model | `pair-teacher-v1`: `jhu-clsp/mmBERT-base` (307M parameters) fine-tuned with three linear heads. A **teacher** for later distillation; it is not a shipped model. |
| Weights licence of the base | MIT (model card, "License: mit"). Downloaded revision `c5955035435e2bf121cde7f3c8863ef52ff35d82` (`pytorch_model.bin`); the repository also carries `ca5b84ba39fb0531402e727a3d9a50626d9f522c` (`model.safetensors`). |
| Fine-tuned weights | `best.pt`, sha256 `5b339b743ec01a22e716ebfc62598d039f8de75c1f2fba25f4eec0d43de83ca7` (selected at step 3,000 of 10,014 on validation loss), outside git. Derived from MIT weights; nothing about it is released. |
| Training data | Our own synthetic pairs only, from the repository's generator (below). No real documents, no third-party text, no hosted model, no human labels. |
| Labels | The generator's exact labels (which blocks are one block, which edits form one unit). No teacher-made labels yet. |
| Evaluation data | Never read by training. synth-v1 `eval`, docx-v1, pdf-v1, combine-v1 and the real pairs are excluded; the exclusion is checked by `check_train_data.py` (below). |
| Code | repository commit recorded in `provenance.json` (`codeCommit`); training code in `tools/model/`. |
| Hosted-model use | none |
| Share-alike lineage | none |

## Datasets (all ours, generated)

The generator is `tools/OneDiff.GoldSets generate` (documents are written for it; nothing is copied from any source). Pairs, labels
and the split are a function of the seed.

| Name | Role | Seed | Documents per language | Train documents | Pairs | Set hash (sha256 of manifest) |
|---|---|---|---|---|---|---|
| synth-v1 train split | train | 20261104 | 60 (24 train) | 168 | 840 | `0ee528554144f8277e0a705a9e38d48e1f35544001d9962a80fac7799d817941` |
| synth-s20270111 | train | 20270111 | 250 (100 train) | 700 | 3,500 | `a7dad3a937403e12927c8af17b433e8918005483e8f375ca1b0a73b0257f848b` |
| synth-s20270112 | train | 20270112 | 800 (320 train) | 2,240 | 11,200 | `c3e98595ebfe93e10d1b591c4d5fbc7cda51bb1f6ab05f3972546b07ec8c670b` |
| synth-v1 dev split | dev (checks only) | 20261104 | 6 | 42 | 210 | `94026ec084850e98eb9ca06d9d299773f6ffa45b9583ef43e8315922a4bbe7e2` |

Seven languages (en, de, fr, es, zh, ja, ar). The new-seed sets were written with `--role train`, so they contain only the 40% of
documents the seed's shuffle assigns to `train`; every pair passed `Verify` at fallback level 0 (`--verify`: 3,500 and 11,200).

From these, `dump-model` records what the model is asked to decide (SHA-256 of each dump in `provenance.json`):

| Dump | Ambiguous block pairs | Multi-unit clauses |
|---|---|---|
| synth-v1 train | 3,152 | 79 |
| synth-s20270111 | 13,856 | 395 |
| synth-s20270112 | 44,750 | 1,118 |
| synth-v1 dev | 947 | 16 |

After deduplication by key, training used 53,396 examples (51,962 block pairs, 1,434 clauses) and validation 6,264 (a slice of the
training data cut by base document, 10%; the dev split is not used for selection). Block pairs are labelled 1 when the gold alignment
links the two blocks (a match or a move), else 0.

## Teacher

- **Base:** `jhu-clsp/mmBERT-base`, https://huggingface.co/jhu-clsp/mmBERT-base, MIT; paper arXiv:2509.06888; ModernBERT architecture, 22 layers, hidden size 768, Gemma 2 tokenizer (256,000 entries), trained on open data (FineWeb2, DCLM, MegaWika, StarCoder, ArXiv, StackExchange).
- **Fine-tune:** PyTorch 2.11.0+cu128, transformers 5.22.1, bf16 autocast, AdamW (lr 3e-5, weight decay 0.01, betas 0.9/0.98), linear warm-up 500 steps then linear decay, batch 16, 3 epochs (10,014 steps), maximum 320 tokens, gradient clipping 1.0, seed 1. Three binary cross-entropy heads: match, whole-clause, join-with-previous. Checkpoint and evaluation every 1,000 steps; best by validation loss.
- **Hardware:** one RTX 4070 (12 GB), driver 595.99.02, peak VRAM 6.75 GiB; training wall time 1,516 s (25.3 min), one chunk. Total GPU time used by the unit: about 0.5 GPU-hours (training plus two inference passes).
- **Code:** `tools/model/` (`train_teacher.py`, `predict.py`, `common.py`, `train-chunk.sh`); environment locked in `tools/model/uv.lock`.

## Evaluation data and its exclusion

`tools/model/check_train_data.py` reads the training sets and every evaluation set and requires, at **document** level: no shared
base-document hash, no shared normalized full document text, and no training document that the split lists with another role. Result
on the three training sets (3,108 documents) against synth-v1 eval, docx-v1 `62b43875…`, pdf-v1 `eb10c6b3…`, combine-v1 `5c55ebb5…`,
the real pairs and synth-v1 dev (1,008 documents): **0 shared base hashes, 0 shared documents, 0 training documents with a held-out
role.** (Scan sets do not exist yet; the script reads any directory handed to `--eval`.) `check-split` of the generator agrees.

### The limit: shared templates

The generator draws sentences from 12 templates per language with two phrasings each and fills values from short lists, by design for
all splits. Documents are disjoint but **paragraph text is not**. Measured as the share of an evaluation set's paragraphs (normalized
text: NFKC, case-folded, white space collapsed) that also occur in the training pairs:

| Evaluation set | Paragraphs | Verbatim in train | Distinct paragraphs verbatim in train | Paragraphs of ≥ 8 words | … with a number |
|---|---|---|---|---|---|
| synth-v1 eval (= docx-v1 text) | 105,585 | **74.1%** | 34.4% | 46.0% | 28.6% |
| combine-v1 | 125,943 | 74.5% | 29.3% | 46.6% | 29.0% |
| pdf-v1 (text extracted from rendered pages) | 126,140 | 62.0% | 13.5% | 36.9% | 24.1% |
| synth-v1 dev | 21,470 | 73.8% | 49.6% | 45.0% | 27.9% |
| real pairs | 20,555 | 0.16% | 0.09% | 0.0% | 0.0% |

So about three quarters of the synthetic evaluation text (and almost half of its long, non-boilerplate paragraphs) has been seen in
training word for word. A model trained here is therefore tested largely on text it has seen; scores on synth-v1 eval and dev measure
the editing and alignment behaviour on familiar wording, not generalization to new wording. Ambiguous block pairs are rarer in
overlap: 4.7% of the distinct dev block pairs (old and new text with diff marks) occur in training, 0 of the dev clauses.

**Remedy proposed (synth-v2, synth-v1 stays frozen):** give the generator a template split. Reserve for evaluation a subset of the
templates (say 4 of 12 per language, with one of the two phrasings of each) and a disjoint half of every value list (parties, terms,
jurisdictions, amounts), so evaluation paragraphs cannot occur in training by construction; train on the remaining templates. Re-measure
with this script; the target is below 5% of evaluation paragraphs of 8 or more words occurring in training. Real version pairs (0.16%
overlap) are the independent check.

## Seeds and reproduction

Data seeds: 20261104 (synth-v1), 20270111, 20270112. Training seed 1. Bootstrap seed 20261104, 10,000 resamples over documents.
Commands are in `tools/model/README.md`; the output hashes are in `provenance.json`.

## Not done / open

- Distillation, export to OneModel, heads in C# and the `.Model` package have not started (they follow a decision on the dev check).
- Labels from the teacher on real documents do not exist yet; the real pairs were only answered once, unlabelled, to count what changes.
