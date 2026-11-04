#!/usr/bin/env bash
# Runs a command and records the machine it ran on: CPU model, cores, instruction-set flags, and the load average before,
# during (sampled every INTERVAL seconds) and after. Timings are only comparable with this record beside them.
#
# Usage: record-run.sh ENV_FILE -- COMMAND [ARGS...]
# Writes ENV_FILE and passes the command's exit code through. INTERVAL defaults to 30.
set -u
out="$1"; shift
[ "${1:-}" = "--" ] && shift
interval="${INTERVAL:-30}"

load() { if [ -r /proc/loadavg ]; then cut -d' ' -f1-3 /proc/loadavg; else sysctl -n vm.loadavg | tr -d '{}' | xargs; fi; }

if [ -r /proc/cpuinfo ]; then
  cpu=$(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2- | xargs)
  logical=$(grep -c '^processor' /proc/cpuinfo)
  isa=$(grep -m1 '^flags' /proc/cpuinfo | tr ' ' '\n' | grep -E '^(sse4_2|avx|avx2|avx512f|avx512bw|bmi2|popcnt|fma)$' | sort -u | xargs)
else
  cpu=$(sysctl -n machdep.cpu.brand_string 2>/dev/null || uname -m)
  logical=$(sysctl -n hw.logicalcpu 2>/dev/null || echo '?')
  isa=$(uname -m)
fi

{
  echo "machine: $(uname -sm)"
  echo "cpu: $cpu"
  echo "logical cores: $logical"
  echo "isa flags: $isa"
  echo "load before (1/5/15 min): $(load)"
} > "$out"

(
  while true; do
    sleep "$interval"
    echo "load during: $(load)" >> "$out"
  done
) &
sampler=$!

"$@"
code=$?

kill "$sampler" 2>/dev/null
wait "$sampler" 2>/dev/null
echo "load after (1/5/15 min): $(load)" >> "$out"
echo "exit code: $code" >> "$out"
exit $code
