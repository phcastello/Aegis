#!/usr/bin/env bash
# Public software/model downloads only. No recordings are read or uploaded.
set -euo pipefail
if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]] ||
   ! python3 -c 'import sys; assert sys.version_info[:2] == (3, 12)'; then
  echo 'Requires Linux x86_64, Python 3.12, gcc and the running Python pyconfig header.' >&2
  exit 1
fi
classic_root="${1:-${XDG_CACHE_HOME:-$HOME/.cache}/aegis-wakeword-poc/comparison/classic}"
classic_root="$(python3 - "$classic_root" <<'PY'
from pathlib import Path
import sys
p = Path(sys.argv[1]).expanduser().resolve()
if any((parent / '.git').exists() for parent in (p, *p.parents)):
    sys.exit('Keep installed tools and models outside Git checkouts')
print(p)
PY
)"
mkdir -p "$classic_root"
python3 -m venv "$classic_root/venv"
python="$classic_root/venv/bin/python"
# Raven's original scipy==1.6.0 and generated C predate Python 3.12. Keep the
# official .pyx/algorithm intact, use compatible pinned SciPy and regenerate C.
"$python" -m pip install --no-deps \
  numpy==1.26.4 scipy==1.14.1 python-speech-features==0.6 \
  webrtcvad-wheels==2.0.14 rhasspy-silence==0.4.0 \
  vosk==0.3.45 pocketsphinx==5.0.4 Cython==3.0.12 \
  setuptools==75.8.0 wheel==0.45.1 cffi==2.1.1 pycparser==3.12 \
  requests==2.34.2 certifi==2026.10.23 charset-normalizer==3.5.2 \
  idna==3.20 urllib3==2.8.0 srt==3.5.3 tqdm==4.70.1 \
  websockets==17.4 sounddevice==0.5.6

download_checked() {
  local url="$1" destination="$2" digest="$3" temporary
  if [[ ! -f "$destination" ]]; then
    temporary="$(mktemp "$classic_root/.download.XXXXXX")"
    if ! curl --fail --location --retry 2 "$url" --output "$temporary" ||
       ! printf '%s  %s\n' "$digest" "$temporary" | sha256sum --check --status; then
      rm -f "$temporary"
      return 1
    fi
    mv "$temporary" "$destination"
  fi
  printf '%s  %s\n' "$digest" "$destination" | sha256sum --check --status
}

download_checked \
  'https://codeload.github.com/rhasspy/rhasspy-wake-raven/tar.gz/9847924ff3ef92b3c497769e870354ddf8d6da1c' \
  "$classic_root/raven-source.tar.gz" \
  '05801cbe4d0dda822209c6d3ed279eee7209084ceb78f0774a7e45476acffa25'
download_checked \
  'https://www.python.org/ftp/python/3.12.15/Python-3.12.15.tar.xz' \
  "$classic_root/Python-3.12.15.tar.xz" \
  'c2c4321961fab0fb999d66e0cecf521c2ab3994c7992873ea99e306c1094fd5a'
download_checked \
  'https://alphacephei.com/vosk/models/vosk-model-small-pt-0.3.zip' \
  "$classic_root/vosk-model-small-pt-0.3.zip" \
  '6e1ce909032e1afa7a88e68a3d628ecafff302bdf195befab308826c395e93b7'

build_dir="$(mktemp -d "$classic_root/.raven-build.XXXXXX")"
trap 'rm -rf "$build_dir"' EXIT
mkdir "$build_dir/source" "$build_dir/headers"
tar -xzf "$classic_root/raven-source.tar.gz" -C "$build_dir/source" --strip-components=1
tar -xJf "$classic_root/Python-3.12.15.tar.xz" -C "$build_dir/headers" \
  --strip-components=2 Python-3.12.15/Include
"$python" - "$build_dir/headers/pyconfig.h" <<'PY'
import shutil, sys, sysconfig
shutil.copyfile(sysconfig.get_config_h_filename(), sys.argv[1])
PY
"$classic_root/venv/bin/cython" -3 "$build_dir/source/rhasspywake_raven/dtw.pyx"
CFLAGS="-I$build_dir/headers" "$python" -m pip install --no-build-isolation --no-deps "$build_dir/source"
"$python" - "$classic_root" <<'PY'
from pathlib import Path
import sys, zipfile
root = Path(sys.argv[1])
with zipfile.ZipFile(root / 'vosk-model-small-pt-0.3.zip') as archive:
    if archive.testzip():
        raise ValueError('Corrupt public model ZIP')
    archive.extractall(root)
PY
"$python" -m pip freeze > "$classic_root/installed-versions.txt"
printf 'CLASSIC_PYTHON=%s\n' "$python"
