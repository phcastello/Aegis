#!/usr/bin/env bash
# Isolated CPU whisper.cpp binding, multilingual Tiny Q5_1 and Silero VAD.
set -euo pipefail
whisper_root="${1:-${XDG_CACHE_HOME:-$HOME/.cache}/aegis-wakeword-poc/comparison}"
whisper_root="$(python3 - "$whisper_root" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1]).expanduser().resolve()
if any((q/'.git').exists() for q in (p,*p.parents)):
    sys.exit('Keep models and environments outside Git')
print(p)
PY
)"
mkdir -p "$whisper_root"
python3 -m venv "$whisper_root/whisper-venv"
"$whisper_root/whisper-venv/bin/python" -m pip install \
  pywhispercpp==1.4.1 onnxruntime==1.23.2 numpy==2.2.6
download_model() {
  local target="$1" url="$2" digest="$3"
  if [[ ! -f "$target" ]]; then
    curl --fail --location --retry 2 "$url" --output "$target.download"
    printf '%s  %s\n' "$digest" "$target.download" | sha256sum --check --status
    mv "$target.download" "$target"
  fi
  printf '%s  %s\n' "$digest" "$target" | sha256sum --check --status
}
download_model "$whisper_root/ggml-tiny-q5_1.bin" \
  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny-q5_1.bin \
  818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7
download_model "$whisper_root/silero_vad.onnx" \
  https://raw.githubusercontent.com/snakers4/silero-vad/v6.2/src/silero_vad/data/silero_vad.onnx \
  1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3
"$whisper_root/whisper-venv/bin/python" -m pip freeze > "$whisper_root/whisper-requirements.txt"
