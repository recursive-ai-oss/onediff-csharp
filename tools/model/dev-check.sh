#!/usr/bin/env bash
# The dev check: scores the engine with another decision procedure's answers on synth-v1 dev, beside the rules, with paired
# bootstrap intervals; and the same with perfect answers, to show where there is room at all.
#
#   rules-table      the rules' own decisions read back through the tables: must equal the engine (every difference 0)
#   teacher          fine-tuned model for both decisions
#   teacher-judge    model for block pairs only
#   teacher-clauses  model for clause grouping only
#   oracle-*         gold answers for the same decisions (head-room)
#
# usage: tools/model/dev-check.sh RUN_NAME      (answers in $ROOT/models/RUN_NAME/answers; run from the repository root)
set -euo pipefail
ROOT=${ONEDIFF_HOME:-$HOME/onediff}
RUN=${1:?run name}
A=$ROOT/models/$RUN/answers
args=""
OUT=$ROOT/models/$RUN/dev-check
DEV=$ROOT/dev/synth-v1-dev
PY=$ROOT/gpu/venv-pair/bin/python
JOBS=${JOBS:-8}
mkdir -p "$OUT"
export PATH=$HOME/dotnet10:$PATH DOTNET_ROOT=$HOME/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export ONEDIFF_ARTIFACTS=${ONEDIFF_ARTIFACTS:-$ROOT/artifacts/pair-model}
dotnet build tools/OneDiff.GoldSets -c Release -v quiet -clp:NoSummary -m:4 -p:UseSharedCompilation=false
gs() { dotnet run --no-build -c Release --project tools/OneDiff.GoldSets -- "$@"; }

$PY tools/model/rules_tables.py --data "$ROOT/train/pair-model/dump-synth-v1-dev.jsonl" --out-dir "$OUT/rules"
run() { name=$1; shift; gs score --data "$DEV" --out "$OUT/$name.json" --pipeline "$name" --jobs "$JOBS" --vs-rules "$@" | tail -4 | tee "$OUT/$name.log"; }

run rules-table --cand-scores "$OUT/rules/cand-scores.jsonl" --clause-answers "$OUT/rules/clause-answers.jsonl"
NAMES="rules-table"
if [ -f "$A/cand-scores.jsonl" ] && [ -z "${ORACLE_ONLY:-}" ]; then
  run teacher --cand-scores "$A/cand-scores.jsonl" --clause-answers "$A/clause-answers.jsonl"
  run teacher-judge --cand-scores "$A/cand-scores.jsonl"
  run teacher-clauses --clause-answers "$A/clause-answers.jsonl"
  NAMES="$NAMES teacher teacher-judge teacher-clauses"
fi
run oracle-judge --oracle-judge
run oracle-clauses --oracle-clauses
run oracle-both --oracle-judge --oracle-clauses

for n in $NAMES oracle-judge oracle-clauses oracle-both; do
  args="$args --name $n=$OUT/$n.json.paired.json"
done
$PY tools/model/dev_table.py $args > "$OUT/dev-table.md"
echo "wrote $OUT/dev-table.md"
