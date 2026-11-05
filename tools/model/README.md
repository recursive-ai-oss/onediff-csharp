# Pair model: teacher fine-tune and dev check

Tooling that fine-tunes the teacher of the pair model and measures what it would change in the engine. Nothing in this folder
ships in a package. Weights and data live outside the repository.

## What the model is asked to decide

The engine's rules make two kinds of judgement that a learned model could make instead. Both are exposed to measurement tools
through narrow seams, and every answer still goes through the unchanged pipeline and `Verify`, so a wrong answer can only make a
comparison read worse, never hide a change.

| Decision | Where the engine asks | The model reads | Gold answer |
|---|---|---|---|
| **Match score** of an ambiguous pair of blocks (rules similarity between 0.30 and 0.95) | the pair scorer (`IPairScorer`) | old ‖ new, each with the word diff marked `«…»`, and the paragraph style and list level | the two blocks belong to one gold alignment link |
| **Unit boundaries** in a clause that holds two or more change units | an internal clause hook in the rule grouping | the clause on each side, units marked `⟦…⟧`, atomic edits `«…»`, `¦` between neighbouring units | whether one gold unit is the clause (**whole**), and for each pair of neighbouring units whether they belong to the same gold unit (**join**) |

The teacher is `jhu-clsp/mmBERT-base` (MIT) with three linear heads on one encoder: match (from the first token), whole (first
token) and join (the hidden state at the `¦` marker of each side, averaged).

## Data

`make-data.sh` builds everything from the synthetic generator; no document comes from anywhere else.

- train: synth-v1's train split, and two sets from new seeds (`--role train`, train documents only);
- dev: synth-v1's dev split, used for the dev check and never for training; the validation slice for model selection is cut from
  the training data by base document;
- `dump-model` records, for each pair of a set, every ambiguous block pair and every multi-unit clause the pipeline meets, with
  the gold answer, in `$ROOT/train/pair-model/dump-*.jsonl`.

`check_train_data.py` proves that no evaluation or development document is in the training data (by base hash, by normalized
full text, and by document role) and measures how much paragraph text they share anyway. `PROVENANCE.md` records the results.

## Commands

```
# once per machine
UV_PROJECT_ENVIRONMENT=$HOME/onediff/gpu/venv-pair uv sync --project tools/model

# data, from the repository root
bash tools/model/make-data.sh
python3 tools/model/check_train_data.py --train ... --eval ... --dev ... --out overlap.json

# teacher, as many chunks as needed (each stops itself after MINUTES and continues from its checkpoint)
wq run --class gpu --max 2h --label "onediff pair-model teacher chunk" -- bash tools/model/train-chunk.sh

# answers for the dev dump, then the dev check
python3 tools/model/predict.py --ckpt $ROOT/models/RUN/best.pt --data $ROOT/train/pair-model/dump-synth-v1-dev.jsonl --out-dir $ROOT/models/RUN/answers
bash tools/model/dev-check.sh RUN
```

`dev-check.sh` first runs the engine through the answer tables with the rules' own decisions (every difference must be 0, which
checks the plumbing), then the teacher for both decisions, for each alone, and the gold answers (the room a perfect judge would
have). Each is scored with `GoldSets score --vs-rules`, which also runs the plain engine on the same pairs and takes a paired
bootstrap over documents (10,000 resamples, seed 20261104) of the difference.
