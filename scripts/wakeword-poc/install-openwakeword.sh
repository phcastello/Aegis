#!/usr/bin/env bash
# Isolated CPU training/runtime plus public, pinned feature/TTS models.
set -euo pipefail
comparison_root="${1:-${XDG_CACHE_HOME:-$HOME/.cache}/aegis-wakeword-poc/comparison}"
comparison_root="$(python3 - "$comparison_root" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1]).expanduser().resolve()
if any((q/'.git').exists() for q in (p,*p.parents)):
    sys.exit('Keep training tools, voices and models outside Git checkouts')
print(p)
PY
)"
mkdir -p "$comparison_root/openwakeword/models" "$comparison_root/voices"
comparison_root="$(cd "$comparison_root" && pwd)"
oww_root="$comparison_root/openwakeword"
python3 -m venv "$oww_root/venv"
python="$oww_root/venv/bin/python"
"$python" -m pip install --no-cache-dir numpy==1.26.4 scipy==1.13.1 \
  onnxruntime==1.20.1 onnx==1.17.0 scikit-learn==1.5.2 piper-tts==1.3.0 \
  requests==2.32.3 tqdm==4.67.1 coloredlogs==15.0.1 flatbuffers==25.12.19 \
  packaging==26.3 protobuf==7.36.2 sympy==1.14.0 joblib==1.6.0 \
  threadpoolctl==3.7.0 cloudpickle==3.1.2 humanfriendly==10.0 mpmath==1.3.0 \
  charset-normalizer==3.5.2 idna==3.20 urllib3==2.8.0 certifi==2026.10.23
if [[ ! -d "$oww_root/source/.git" ]]; then
  git clone https://github.com/dscripka/openWakeWord.git "$oww_root/source"
fi
git -C "$oww_root/source" checkout --detach 368c03716d1e92591906a84949bc477f3a834455
# ONNX-only inference does not use LiteRT, Speex, Torch or the legacy full extras.
"$python" -m pip install --no-cache-dir --no-deps "$oww_root/source"
"$python" - "$comparison_root" <<'PY'
import hashlib
from pathlib import Path
import sys
from urllib.request import urlretrieve

root = Path(sys.argv[1])
assets = {
    "openwakeword/models/melspectrogram.onnx": ("https://github.com/dscripka/openWakeWord/releases/download/v0.5.1/melspectrogram.onnx", "ba2b0e0f8b7b875369a2c89cb13360ff53bac436f2895cced9f479fa65eb176f"),
    "openwakeword/models/embedding_model.onnx": ("https://github.com/dscripka/openWakeWord/releases/download/v0.5.1/embedding_model.onnx", "70d164290c1d095d1d4ee149bc5e00543250a7316b59f31d056cff7bd3075c1f"),
}
pins = {
    "cadu": ["765f0809a6ea9035d4a6d0d008dbf8876e68b2dd32029312672fa8f405bdb535", "5fe03aa3d4901880554905b12075713cd552598c8a350455a1ec73f8b4e6be19", "b867e819f48365b592c7789f71bba5ca79e1cb3b8a499853485db417d8031427"],
    "faber": ["858555e3a064209c57088fe6bd70c4c3dc54d03eaa00c45d5ecaf43a33f95aa7", "7e694de195ae3fc36dd732c445eb04fb49b649854893cb5506b978f0d50a1d6f", "01f1a5bcfd0538782726059ae407eae9c2dd1b5b35f7298fd53a68de91afe563"],
    "jeff": ["3a6f4c46355813c2b7bbc4d16b6d13d60ed72074b952a393baace82a7d0c94b5", "7bf8145b572b36806f5ce0f1d3322b6711975bc7d0473e8d36fced4a9ec0030d", "acdea1b233a478a8ce3bac1f4886a14c8bb6a6287cefb41a26a657a3a7418b08"],
}
revision = "b4e358ffcb232591bf73a540e70f7de528e4174a"
for voice, digests in pins.items():
    names = [f"pt_BR-{voice}-medium.onnx", f"pt_BR-{voice}-medium.onnx.json", "MODEL_CARD"]
    for name, digest in zip(names, digests):
        assets[f"voices/{voice}/{name}"] = (f"https://huggingface.co/rhasspy/piper-voices/resolve/{revision}/pt/pt_BR/{voice}/medium/{name}", digest)
for relative, (url, digest) in assets.items():
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        temporary = path.with_suffix(path.suffix + ".download")
        urlretrieve(url, temporary)
        if hashlib.file_digest(temporary.open("rb"), "sha256").hexdigest() != digest:
            raise ValueError(f"Asset checksum mismatch: {relative}")
        temporary.rename(path)
    if hashlib.file_digest(path.open("rb"), "sha256").hexdigest() != digest:
        raise ValueError(f"Existing asset checksum mismatch: {relative}")
    print(digest, relative)
PY
"$python" -m pip freeze > "$oww_root/installed-requirements.txt"
printf 'OPENWAKEWORD_PYTHON=%s\n' "$python"
