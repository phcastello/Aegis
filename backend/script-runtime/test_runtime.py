import json
import pathlib
import subprocess
import tempfile
import zipfile


RUNNER = "/opt/aegis-python/runner"


def run(code, data=None, directories=(), audit=False, timeout_seconds=5):
    return subprocess.run(
        [RUNNER, *directories],
        input=json.dumps({"code": code, "input": data, "workspace": bool(directories), "audit": audit}),
        encoding="utf-8",
        capture_output=True,
        timeout=timeout_seconds,
        env={"AEGIS_RUNTIME_TEST_SECRET": "host-only"},
    )


def expect_success(result):
    assert result.returncode == 0, (result.returncode, result.stderr)
    return result.stdout


number = "from secrets import randbelow; print(-9 + randbelow(1))"
assert expect_success(run(number)).strip() == "-9"
draws = expect_success(run("""
import json, random, secrets
print(json.dumps({
    'standard': [random.randint(-2, 1) for _ in range(100)],
    'secure': [-2 + secrets.randbelow(4) for _ in range(100)],
}))
"""))
for values in json.loads(draws).values():
    assert set(values) == {-2, -1, 0, 1}, values

data = '{"medidas": [1, 3, 17], "nome": "São Paulo"}'
analysis = expect_success(run("""
import json, statistics, sys
assert sys.stdin.read() == input_data
data = json.loads(input_data)
print(f'{data["nome"]}: {statistics.median(data["medidas"])}')
""", data))
assert analysis.strip() == "São Paulo: 3", analysis

expect_success(run("""
import os
assert not os.environ
for path, mode in [('/etc/passwd', 'r'), ('/usr/local/lib/python3.14/os.py', 'a')]:
    try:
        with open(path, mode):
            raise AssertionError('unexpected filesystem permission')
    except OSError:
        pass
try:
    import socket
    socket.socket().close()
except (ImportError, OSError):
    pass
else:
    raise AssertionError('unexpected network access')
"""))

failure = run("raise ValueError('script error')")
assert failure.returncode == 1 and "ValueError: script error" in failure.stderr, failure
overflow = run("print('a' * 20000)")
assert overflow.returncode == 3 and len(overflow.stdout) <= 16384, overflow.returncode
timeout = run("while True: pass")
assert timeout.returncode == 4, (timeout.returncode, timeout.stderr)
memory = run("bytearray(128 * 1024 * 1024)")
assert memory.returncode == 1 and 'MemoryError' in memory.stderr, memory
redacted = run("private_name = 993; print(f'private_email {private_name}') # private_comment", audit=True)
assert expect_success(redacted).strip() == "private_email 993"
assert "private_" not in redacted.stderr and "993" not in redacted.stderr
assert "<nome>" in redacted.stderr and "<texto>" in redacted.stderr

print("Python WASI runtime checks passed")

with tempfile.TemporaryDirectory() as temp:
    directories = [pathlib.Path(temp, name) for name in ("inputs", "work", "outputs")]
    for directory in directories:
        directory.mkdir()
    with zipfile.ZipFile(directories[0] / "vendas.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("dados.csv", "venda\n19\n23\n")
    expect_success(run("""
import csv, io, zipfile
from pathlib import Path
with zipfile.ZipFile('/inputs/vendas.zip') as z, z.open('dados.csv') as f:
    total = sum(int(row['venda']) for row in csv.DictReader(io.TextIOWrapper(f)))
Path('/work/total').write_text(str(total))
for path in ('/inputs/vendas.zip', '/work/../outside', '/outputs/../../etc/secret'):
    try:
        open(path, 'wb')
    except OSError:
        continue
    raise AssertionError('unexpected write access')
""", directories=directories))
    expect_success(run("""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
with ZipFile('/outputs/resultado.zip', 'w', ZIP_DEFLATED) as result:
    result.writestr('total.txt', Path('/work/total').read_bytes())
""", directories=directories))
    with zipfile.ZipFile(directories[2] / "resultado.zip") as result:
        assert result.read("total.txt") == b"42"

    expect_success(run("""
from aegis_docx import read_paragraphs, write_document
from openpyxl import Workbook, load_workbook
import png
from pypdf import PdfReader, PdfWriter

book = Workbook()
sheet = book.active
sheet.append(['Mês', 'Vendas'])
sheet.append(['Janeiro', 42])
book.save('/outputs/vendas.xlsx')
saved = load_workbook('/outputs/vendas.xlsx', read_only=True)
assert next(saved.active.values) == ('Mês', 'Vendas')
saved.close()

two_lines = 'Linha 1' + chr(10) + 'Linha 2'
write_document('/outputs/relatório.docx', ['Total: 42', two_lines],
               tables=[[['Mês', 'Vendas'], ['Janeiro', 42]]])
assert read_paragraphs('/outputs/relatório.docx') == ['Total: 42', two_lines, 'Mês', 'Vendas', 'Janeiro', '42']

with open('/outputs/gráfico.png', 'wb') as image:
    png.Writer(2, 1, greyscale=True, bitdepth=8).write(image, [[0, 255]])
with open('/outputs/gráfico.png', 'rb') as image:
    width, height, rows, _ = png.Reader(file=image).read()
    assert (width, height, list(next(rows))) == (2, 1, [0, 255])

pdf = PdfWriter()
pdf.add_blank_page(72, 72)
pdf.write('/outputs/resumo.pdf')
assert len(PdfReader('/outputs/resumo.pdf').pages) == 1
""", directories=directories, timeout_seconds=30))
    with zipfile.ZipFile(directories[2] / "vendas.xlsx") as result:
        assert b"<v>42</v>" in result.read("xl/worksheets/sheet1.xml")
    with zipfile.ZipFile(directories[2] / "relatório.docx") as result:
        assert b"Total: 42" in result.read("word/document.xml")
