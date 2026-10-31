#!/usr/bin/env bash
# Public packages/model only. Owner recordings are never read by this installer.
set -euo pipefail

if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]] ||
   ! python3 -c 'import sys; assert sys.version_info[:2] == (3, 12)'; then
  echo 'This pinned installer requires Linux x86_64 and Python 3.12.' >&2
  exit 1
fi

sherpa_root="${1:-${XDG_CACHE_HOME:-$HOME/.cache}/aegis-wakeword-poc/sherpa}"
sherpa_root="$(python3 - "$sherpa_root" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1]).expanduser().resolve()
if any((parent / '.git').exists() for parent in (path, *path.parents)):
    sys.exit('Keep downloaded models and dependencies outside Git checkouts')
print(path)
PY
)"
mkdir -p "$sherpa_root/wheels"
sherpa_root="$(cd "$sherpa_root" && pwd)"
python3 -m venv "$sherpa_root/venv"
python="$sherpa_root/venv/bin/python"
requirements="$sherpa_root/requirements.lock"
cat > "$requirements" <<'REQUIREMENTS'
sherpa-onnx==2.0.1 --hash=sha256:c7780d8af6921c18e8e920376b32d7707294e5cd3838f4762cf746ab919bcc40
sherpa-onnx-core==2.0.1 --hash=sha256:f4ee0ab91c4ae596e0e49d7de32522a946295e038a652f1c1884d89014ee4ec2
numpy==2.2.6 --hash=sha256:fd83c01228a688733f1ded5201c678f0c53ecc1006ffbc404db9f7a899ac6249
REQUIREMENTS
"$python" -m pip download --only-binary=:all: --require-hashes \
  --dest "$sherpa_root/wheels" -r "$requirements"
"$python" -m pip install --no-index --find-links "$sherpa_root/wheels" \
  --require-hashes --only-binary=:all: -r "$requirements"

model='sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20'
archive="$sherpa_root/model.tar.bz2"
expected_sha='68447f4fbc67e70eee3a93961f36e81e98f47aef73ce7e7ca00885c6cd3616a6'
if [[ ! -f "$archive" ]]; then
  download="$(mktemp "$sherpa_root/.model.XXXXXX")"
  trap 'rm -f "$download"' EXIT
  curl --fail --location --retry 2 \
    "https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/$model.tar.bz2" \
    --output "$download"
  printf '%s  %s\n' "$expected_sha" "$download" | sha256sum --check --status
  mv "$download" "$archive"
fi
printf '%s  %s\n' "$expected_sha" "$archive" | sha256sum --check --status
tar --extract --bzip2 --file "$archive" --directory "$sherpa_root" --no-same-owner
"$python" -m pip freeze
printf 'SHERPA_PYTHON=%s\nSHERPA_MODEL=%s\n' "$python" "$sherpa_root/$model"
