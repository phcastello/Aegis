#!/usr/bin/env bash
# Fixed five-candidate CPU protocol. Stop other experiment jobs beforehand.
set -euo pipefail
comparison_cache="${AEGIS_WAKEWORD_POC_CACHE:-$HOME/.cache/aegis-wakeword-poc}/comparison"
measurement_label="${1:-efficiency}"
measurement_cpu="${AEGIS_BENCH_CPU:-2}"
export OPENBLAS_NUM_THREADS=1 OMP_NUM_THREADS=1 CUDA_VISIBLE_DEVICES=''
export TF_NUM_INTRAOP_THREADS=1 TF_NUM_INTEROP_THREADS=1
for entry in 'raven:default:classic' 'pocketsphinx:default:classic' \
             'whisper:prompt:whisper-venv' 'openwakeword:strict:openwakeword' \
             'microwakeword-realneg:default:microwakeword'; do
  IFS=: read -r candidate configuration environment <<< "$entry"
  python="$comparison_cache/$environment/venv/bin/python"
  if [[ "$environment" == whisper-venv ]]; then
    python="$comparison_cache/whisper-venv/bin/python"
  fi
  output="$comparison_cache/results/$candidate-$measurement_label"
  printf 'Measuring %s (%s), CPU %s, 120 s offline + 120 s paced\n' \
    "$candidate" "$configuration" "$measurement_cpu"
  taskset -c "$measurement_cpu" "$python" "$(dirname "$0")/comparison.py" \
    --candidate "$candidate" --config "$configuration" --mode efficiency \
    --seconds 120 --output "$output"
done
