"""Install the complete, pinned set of platform-independent wheels without running package code."""
import hashlib
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tempfile
from zipfile import ZipFile


runtime = Path(sys.argv[1])
stdlib = runtime / "stdlib"
source = Path(__file__).parent
with tempfile.TemporaryDirectory() as download_dir:
    for line in (source / "libraries.lock").read_text(encoding="ascii").splitlines():
        checksum, url = line.split()
        wheel = Path(download_dir) / url.rsplit("/", 1)[1]
        if not wheel.name.endswith("-none-any.whl"):
            raise ValueError(f"Expected a platform-independent wheel: {wheel.name}")
        subprocess.run(["curl", "--fail", "--silent", "--show-error", "--location", "--retry", "3",
                        url, "--output", wheel], check=True)
        if hashlib.sha256(wheel.read_bytes()).hexdigest() != checksum:
            raise ValueError(f"Wheel checksum mismatch: {wheel.name}")
        with ZipFile(wheel) as package:
            for member in package.infolist():
                entry = PurePosixPath(member.filename)
                if entry.is_absolute() or ".." in entry.parts:
                    raise ValueError(f"Invalid wheel member: {member.filename}")
                if entry.parts[0].endswith(".data") or member.is_dir():
                    continue
                destination = stdlib.joinpath(*entry.parts)
                destination.parent.mkdir(parents=True, exist_ok=True)
                with package.open(member) as content, destination.open("xb") as output:
                    shutil.copyfileobj(content, output)
for module in ("aegis_docx.py", "aegis_script_audit.py"):
    shutil.copyfile(source / module, stdlib / module)
