"""Make a private, offline review ZIP from the API's wake_dataset volume."""

import argparse
import base64
import hashlib
import json
import uuid
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parents[2]
REVIEWER = ROOT / "apps/aegis-node/src-tauri/gen/android/app/src/main/assets/hands-free-reviewer"


def export(source, destination):
    source = source.resolve(strict=True)
    if not source.is_dir():
        raise ValueError("source must be a dataset directory")
    rows = []
    at = datetime.now(timezone.utc).isoformat()
    with destination.open("xb") as output:
        try:
            with ZipFile(output, "w", ZIP_DEFLATED) as archive:
                for path in sorted(source.glob("*/*.json")):
                    if path.with_suffix(".deleted").exists():
                        continue
                    node, event = str(uuid.UUID(path.parent.name)), str(uuid.UUID(path.stem))
                    row = json.loads(path.read_bytes())
                    sidecar = path.with_suffix(".meta")
                    labels = json.loads(sidecar.read_bytes()) if sidecar.is_file() else {}
                    reviewed_at = labels.get("reviewedAt")
                    # Node scope makes event identities unambiguous even after importing several devices.
                    row.update(id=f"{node}/{event}", originalEventId=event, nodeId=node, exportedAt=at,
                               source="Aegis central wake dataset", humanLabel=labels.get("humanLabel"),
                               commandLabel=labels.get("commandLabel"), labeledAt=reviewed_at, commandLabeledAt=reviewed_at)
                    for field, checksum, reference, name in [
                        ("wav", "sha256", "triggerContext", "trigger_context.wav"),
                        ("postWav", "postSha256", "postActivation", "post_activation.wav"),
                    ]:
                        encoded, sha = row.pop(field, None), row.pop(checksum, None)
                        if encoded is None:
                            row[reference] = None
                            continue
                        audio = base64.b64decode(encoded, validate=True)
                        digest = hashlib.sha256(audio).hexdigest()
                        if digest != (sha or "").lower():
                            raise ValueError(f"Invalid WAV digest: {event}")
                        member = f"events/{node}/{event}/{name}"
                        archive.writestr(member, audio)
                        row[reference] = dict(path=member, sha256=digest, samples=(len(audio) - 44) // 2,
                                              sampleRate=16000, channels=1, encoding="PCM16LE")
                    rows.append(row)
                rows.sort(key=lambda row: row["activatedAt"])
                raw = json.dumps(rows, ensure_ascii=False).encode()
                archive.writestr("events.js", "window.AEGIS_EVENTS_BASE64='" + base64.b64encode(raw).decode() + "';")
                archive.writestr("manifest.jsonl", "".join(json.dumps(row) + "\n" for row in rows))
                for name in ("index.html", "reviewer.css", "reviewer.js"):
                    archive.write(REVIEWER / name, name)
        except BaseException:
            output.close()
            destination.unlink(missing_ok=True)
            raise
    return len(rows)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    print(f"{export(args.source, args.destination)} amostras exportadas.")
