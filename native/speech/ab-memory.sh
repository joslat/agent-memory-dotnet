#!/usr/bin/env bash
# Memory on against memory off, on the same server code, alternating arms so drift hits both alike.
# Usage: native/speech/ab-memory.sh <report-dir> [rounds]   (S2S_SOURCE, MEMORY_ENV and MEMORY_BIN as for run-baseline.sh)
set -euo pipefail
OUT="${1:?report dir}"
ROUNDS="${2:-3}"
HERE="$(cd "$(dirname "$0")" && pwd)"
: "${S2S_SOURCE:?}" "${MEMORY_ENV:?}" "${MEMORY_BIN:?}"
for round in $(seq 1 "$ROUNDS"); do
  for arm in off on; do
    echo "== round $round, memory $arm"
    if [ "$arm" = on ]; then
      bash "$HERE/run-baseline.sh" "$OUT/$arm-$round" | tail -2
    else
      MEMORY_ENV="" bash "$HERE/run-baseline.sh" "$OUT/$arm-$round" | tail -1
    fi
  done
done
python3 -I "$HERE/ab_summary.py" "$OUT" | tee "$OUT/ab-summary.txt"
