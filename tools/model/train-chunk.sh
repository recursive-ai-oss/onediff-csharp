#!/usr/bin/env bash
# One resumable chunk of the teacher fine-tune. Run it under the GPU queue, for example:
#
#   wq run --class gpu --max 2h --label "onediff pair-model teacher chunk" -- bash tools/model/train-chunk.sh
#
# The run directory ($ROOT/models/$RUN) keeps the last checkpoint; each call continues from it and stops by itself after
# MINUTES (default 105) so that the final evaluation and save finish inside the queue's two-hour limit.
set -euo pipefail
ROOT=${ONEDIFF_HOME:-$HOME/onediff}
RUN=${RUN:-pair-teacher-v1}
MINUTES=${MINUTES:-105}
EPOCHS=${EPOCHS:-3}
D=$ROOT/train/pair-model
export HF_HOME=$ROOT/hf TOKENIZERS_PARALLELISM=false
cd "$(dirname "$0")"
mkdir -p "$ROOT/models/$RUN"
nvidia-smi --query-gpu=name,driver_version,memory.used,memory.total --format=csv | tee -a "$ROOT/models/$RUN/chunks.log"
date -u +"chunk start %FT%TZ" | tee -a "$ROOT/models/$RUN/chunks.log"
"$ROOT/gpu/venv-pair/bin/python" train_teacher.py --run "$ROOT/models/$RUN" \
  --data "$D/dump-synth-v1-train.jsonl" $(for f in "$D"/dump-synth-s*-train.jsonl; do echo --data "$f"; done) \
  --epochs "$EPOCHS" --max-minutes "$MINUTES" --batch "${BATCH:-16}" --lr "${LR:-3e-5}" --seed "${SEED:-1}" --eval-every "${EVAL_EVERY:-1000}" 2>&1 \
  | grep -v -i 'warn' | tee -a "$ROOT/models/$RUN/chunks.log"
date -u +"chunk end %FT%TZ" | tee -a "$ROOT/models/$RUN/chunks.log"
