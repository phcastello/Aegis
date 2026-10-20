#!/usr/bin/env python3
"""Upload existing private signing material through gh's stdin, never command arguments/logs."""
import base64
import json
import subprocess
from pathlib import Path

key_dir = Path.home() / ".local/share/aegis/release-keys"
credentials = json.loads((key_dir / "android-signing.json").read_text())
values = {
    "ANDROID_KEYSTORE_BASE64": base64.b64encode((key_dir / "android-release.p12").read_bytes()).decode(),
    "ANDROID_KEYSTORE_PASSWORD": credentials["ANDROID_KEYSTORE_PASSWORD"],
    "ANDROID_KEY_ALIAS": credentials["ANDROID_KEY_ALIAS"],
    "ANDROID_KEY_PASSWORD": credentials["ANDROID_KEY_PASSWORD"],
    "TAURI_SIGNING_PRIVATE_KEY": (key_dir / "updater.key").read_text(),
}
for name, value in values.items():
    subprocess.run(["gh", "secret", "set", name, "--repo", "phcastello/Aegis"],
                   input=value, text=True, check=True, stdout=subprocess.DEVNULL)
    print(f"Configured {name}")
