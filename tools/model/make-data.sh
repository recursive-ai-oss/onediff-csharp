#!/usr/bin/env bash
# Builds the data the pair model is trained and checked on, from the synthetic generator.
#
#   train   synth-v1's train split (already generated, seed 20261104) and a set from a new seed (train documents only)
#   dev     synth-v1's dev split, used for model checks and never for training
#   dumps   every ambiguous block pair and every multi-unit clause the pipeline meets on those sets, with the gold answer
#
# Evaluation data is never read here; tools/model/check_train_data.py proves the overlap afterwards.
# usage: tools/model/make-data.sh            (run from the repository root, on the machine that trains)
set -euo pipefail

ROOT=${ONEDIFF_HOME:-$HOME/onediff}
JOBS=${JOBS:-8}
# New seeds for the training sets: "seed:documents per language" (the generator keeps 40% of the documents as train documents).
SETS=${SETS:-"20270111:250 20270112:800"}
OUT=$ROOT/train/pair-model
mkdir -p "$OUT"
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$ROOT/artifacts/pair-model}

dotnet build tools/OneDiff.GoldSets -c Release -v quiet -clp:NoSummary -m:4 -p:UseSharedCompilation=false
gs() { dotnet run --no-build -c Release --project tools/OneDiff.GoldSets -- "$@"; }

for spec in $SETS; do
  seed=${spec%%:*}; docs=${spec##*:}
  NEW=$ROOT/train/synth-s$seed-train
  if [ ! -d "$NEW/pairs" ]; then
    gs generate --out "$NEW" --role train --seed "$seed" --docs-per-lang "$docs" --verify
  fi
  echo "seed $seed set hash: $(cat "$NEW/manifest.sha256")"
  gs dump-model --data "$NEW" --out "$OUT/dump-synth-s$seed-train.jsonl" --jobs "$JOBS"
done
echo "synth-v1 train hash: $(cat "$ROOT/train/synth-v1-train/manifest.sha256")"
echo "synth-v1 dev hash: $(cat "$ROOT/dev/synth-v1-dev/manifest.sha256")"
gs dump-model --data "$ROOT/train/synth-v1-train" --out "$OUT/dump-synth-v1-train.jsonl" --jobs "$JOBS"
gs dump-model --data "$ROOT/dev/synth-v1-dev" --out "$OUT/dump-synth-v1-dev.jsonl" --jobs "$JOBS"
# The real pairs are evaluation material: dumped only to be answered once and compared, never trained on.
gs dump-real --real "$ROOT/eval/real-pairs-v1" --out "$OUT/dump-real-eval-only.jsonl"
sha256sum "$OUT"/dump-*.jsonl
