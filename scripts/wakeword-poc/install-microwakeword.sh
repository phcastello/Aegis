#!/usr/bin/env bash
# Isolated CPU training/runtime dependencies and immutable official source.
set -euo pipefail
microwake_root="${1:-${XDG_CACHE_HOME:-$HOME/.cache}/aegis-wakeword-poc/comparison/microwakeword}"
microwake_root="$(python3 - "$microwake_root" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1]).expanduser().resolve()
if any((x/'.git').exists() for x in (p,*p.parents)):
    raise SystemExit('microWakeWord tools/models must remain outside Git checkouts')
print(p)
PY
)"
mkdir -p "$microwake_root"
if [[ ! -d "$microwake_root/source/.git" ]]; then
  git clone https://github.com/OHF-Voice/micro-wake-word.git "$microwake_root/source"
fi
git -C "$microwake_root/source" checkout --detach 4665173cd35f1cff9a61e06fc427f124766c488e
python3 -m venv "$microwake_root/venv"
"$microwake_root/venv/bin/python" -m pip install \
  tensorflow-cpu==2.18.1 keras==3.15.1 numpy==2.0.2 pymicro-features==2.0.2 \
  pyyaml==6.0.3 ai-edge-litert==2.3.0 scipy==1.14.1 webrtcvad-wheels==2.0.14
"$microwake_root/venv/bin/python" -m pip freeze > "$microwake_root/environment.txt"
printf 'MICROWAKE_ROOT=%s\n' "$microwake_root"
