import json
import subprocess


RUNNER = "/opt/aegis-python/runner"


def run(code, data=None):
    return subprocess.run(
        [RUNNER],
        input=json.dumps({"code": code, "input": data}),
        encoding="utf-8",
        capture_output=True,
        timeout=5,
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

print("Python WASI runtime checks passed")
