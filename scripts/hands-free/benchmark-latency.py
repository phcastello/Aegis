#!/usr/bin/env python3
"""Small live latency experiment on disposable resources; no personal data.

Credentials are inherited in memory from the existing API container. The generated
Compose configuration is private and must never be checked in or attached to reports.
No production service, volume, configuration or data is modified by this harness.
"""
import argparse
import array
import json
import os
from pathlib import Path
import secrets
import socket
import statistics
import subprocess
import time
import urllib.request
import uuid
import wave

CORPUS = [
    ("time", "Que horas são?"),
    ("math", "Quanto é dezessete mais vinte e cinco?"),
    ("fact", "Qual é a capital do Japão?"),
    ("long", "Explique em três frases por que o céu parece azul."),
    ("continuation", "Estou em dúvida entre chá e café. Me faça uma pergunta para ajudar a escolher."),
    ("read_tool", "Liste os lugares cadastrados."),
]
TTS_TEXTS = {
    "time": "São nove horas e quinze minutos.",
    "math": "Dezessete mais vinte e cinco são quarenta e dois.",
    "long": "A água ferve a cem graus Celsius ao nível do mar. Em lugares mais altos, ela ferve a uma temperatura menor, por causa da pressão atmosférica.",
}


def command(*args, input=None):
    return subprocess.check_output(args, input=input, stderr=subprocess.PIPE)


def dc(state, *args):
    return command("docker", "compose", "-p", state["project"], "-f", state["compose"], *args)


def http(base, path, body=None, method=None, headers=None):
    req = urllib.request.Request(base + path, method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers=headers or {"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=180)


def prepare(directory):
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    if (directory / "state.json").exists():
        raise RuntimeError("existing experiment: use a new private directory")
    live = json.loads(command("docker", "inspect", "aegis-api"))[0]
    env = dict(item.split("=", 1) for item in live["Config"]["Env"] if "=" in item)
    # Provider and inference settings match the running API. No Google OAuth,
    # notification credentials, owner database or data-protection mounts are copied.
    env = {k: v for k, v in env.items() if k == "OPENAI_API_KEY" or
        k.startswith(("AEGIS_CHAT_", "AEGIS_MAX_OUTPUT_", "AEGIS_OPENAI_", "AEGIS_MEMORY_", "AEGIS_TTS_", "AEGIS_STT_", "AEGIS_TITLE_"))}
    run = secrets.token_hex(5)
    project = "aegis_latency_" + run
    db_name = "aegis_memory_v060_" + run
    db_password, graph_password = secrets.token_hex(24), secrets.token_hex(24)
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    env.update({
        "ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": "http://+:8090",
        "ConnectionStrings__AegisDatabase": f"Host=postgres;Database={db_name};Username=aegis_eval;Password={db_password}",
        "AEGIS_MEMORY_QDRANT_URL": "http://qdrant:6333",
        "AEGIS_MEMORY_QDRANT_COLLECTION": db_name,
        "AEGIS_MEMORY_NEO4J_URI": "bolt://neo4j:7687",
        "AEGIS_MEMORY_NEO4J_DATABASE": "neo4j",
        "AEGIS_MEMORY_NEO4J_USERNAME": "neo4j",
        "AEGIS_MEMORY_NEO4J_PASSWORD": graph_password,
        "AEGIS_MEMORY_DIAGNOSTICS_ENABLED": "true", "AEGIS_MEMORY_EVAL_ISOLATED": "true",
        "AEGIS_LATENCY_PROFILE": "1",
    })
    composition = {"services": {
        "postgres": {"image": "postgres:16-alpine", "environment": {
            "POSTGRES_DB": db_name, "POSTGRES_USER": "aegis_eval", "POSTGRES_PASSWORD": db_password}},
        "qdrant": {"image": "qdrant/qdrant:v1.12.6"},
        "neo4j": {"image": "neo4j:5.26.30-community", "environment": {
            "NEO4J_AUTH": "neo4j/" + graph_password, "NEO4J_server_memory_heap_initial__size": "256m",
            "NEO4J_server_memory_heap_max__size": "512m", "NEO4J_server_memory_pagecache_size": "128m"}},
        "api": {"image": live["Image"], "environment": env,
            "ports": [f"127.0.0.1:{port}:8090"], "depends_on": ["postgres", "qdrant", "neo4j"]},
    }}
    private_compose = directory / "compose.private.json"
    private_compose.write_text(json.dumps(composition))
    private_compose.chmod(0o600)
    state = {"project": project, "database": db_name, "compose": str(private_compose),
        "base": f"http://127.0.0.1:{port}", "sourceImage": live["Image"],
        "sourceCommit": live["Config"]["Labels"].get("org.opencontainers.image.revision"),
        "created": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    (directory / "state.json").write_text(json.dumps(state, indent=2))
    print(json.dumps({"prepared": True, "database": db_name, "sourceCommit": state["sourceCommit"]}))


def start(state, image, source_commit, directory):
    if image:
        path = Path(state["compose"])
        config = json.loads(path.read_text())
        config["services"]["api"]["image"] = image
        path.write_text(json.dumps(config))
    if source_commit:
        state["sourceCommit"] = source_commit
    dc(state, "up", "-d", "--no-build")
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        try:
            with http(state["base"], "/api/memory/diagnostics/environment") as response:
                identity = json.load(response)
            if identity.get("isolated") and identity.get("database") == state["database"]:
                container = dc(state, "ps", "-q", "api").decode().strip()
                running = json.loads(command("docker", "inspect", container))[0]
                state["measurementImage"] = running["Image"]
                (directory / "state.json").write_text(json.dumps(state, indent=2))
                print(json.dumps({"ready": True, "base": state["base"], "identity": identity}))
                return
        except Exception:
            pass
        time.sleep(1)  # bounded startup readiness, never an inference keep-alive
    raise RuntimeError("disposable API did not become ready")


def pcm(response, start_time):
    first_byte = first_pcm = first_signal = None
    size = 0
    tail = b""
    peak = 0
    chunks = 0
    while True:
        data = response.read1(4096)
        if not data:
            break
        elapsed = (time.perf_counter() - start_time) * 1000
        chunks += 1
        size += len(data)
        if first_byte is None:
            first_byte = elapsed
        samples_data = tail + data
        tail = samples_data[len(samples_data) // 2 * 2:]
        samples = array.array("h", samples_data[:len(samples_data) // 2 * 2])
        if samples:
            if first_pcm is None:
                first_pcm = elapsed
            local_peak = max(map(abs, samples))
            peak = max(peak, local_peak)
            # Explicit digital threshold; this is not acoustic audibility.
            if first_signal is None and local_peak >= 64:
                first_signal = elapsed
    return {"firstByteMs": first_byte, "firstPcmMs": first_pcm, "firstSignalMs": first_signal,
        "totalMs": (time.perf_counter() - start_time) * 1000, "bytes": size, "chunks": chunks,
        "peakPcm16": peak, "partialSampleAtEof": bool(tail)}


def measure(state, directory, label, repetitions, with_stt):
    if not 1 <= repetitions <= 3:
        raise ValueError("require 1–3 repetitions")
    with http(state["base"], "/api/memory/diagnostics/environment") as response:
        identity = json.load(response)
    if not identity.get("isolated") or identity.get("database") != state["database"]:
        raise RuntimeError("refuse non-disposable API")
    results = {"schema": 1, "label": label, "conditions": identity, "repetitions": repetitions,
        "sourceCommit": state["sourceCommit"], "image": state.get("measurementImage", state["sourceImage"]), "samples": [], "stt": []}
    private_responses = []
    for rep in range(repetitions):
        for case, question in CORPUS:
            turn = str(uuid.uuid4())
            payload = {"turnId": turn, "origin": "hands_free", "voiceSessionId": str(uuid.uuid4()), "content": question}
            sample = {"case": case, "rep": rep, "turnId": turn}
            started = time.perf_counter()
            try:
                with http(state["base"], "/api/chat/messages", payload) as response:
                    chat = json.load(response)
                sample["chatMs"] = (time.perf_counter() - started) * 1000
                message = chat["message"]
                sample.update({"characters": len(message["content"]), "continuation": chat.get("voiceContinuation")})
                private_responses.append({"sample": sample.copy(), "chat": chat})
                speech_start = time.perf_counter()
                with http(state["base"], "/api/voice/speech", {"turnId": turn,
                    "speechRequestId": str(uuid.uuid4()), "assistantMessageId": message["id"]}) as response:
                    sample["tts"] = pcm(response, speech_start)
                sample["chatToFirstSignalMs"] = sample["chatMs"] + sample["tts"]["firstSignalMs"]
            except Exception as error:
                sample["errorType"] = type(error).__name__
                sample["elapsedMs"] = (time.perf_counter() - started) * 1000
            results["samples"].append(sample)
            (directory / (label + "-pipeline.json")).write_text(json.dumps(results, indent=2))
            print(json.dumps(sample), flush=True)
    if with_stt:
        audio_path = directory.parent / "continuous.wav"
        audio = audio_path.read_bytes()
        with wave.open(str(audio_path), "rb") as recording:
            duration = round(recording.getnframes() / recording.getframerate() * 1000)
        for rep in range(3):
            boundary = "aegis-latency-" + secrets.token_hex(12)
            request_id = str(uuid.uuid4())
            form = bytearray()
            for key, value in [("transcriptionRequestId", request_id), ("clientDurationMilliseconds", str(duration))]:
                form.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{value}\r\n'.encode())
            form.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="audio"; filename="synthetic.wav"\r\nContent-Type: audio/wav\r\n\r\n'.encode())
            form.extend(audio)
            form.extend(f'\r\n--{boundary}--\r\n'.encode())
            req = urllib.request.Request(state["base"] + "/api/voice/transcriptions", data=form,
                headers={"Content-Type": "multipart/form-data; boundary=" + boundary})
            started = time.perf_counter()
            with urllib.request.urlopen(req, timeout=60) as response:
                transcription = json.load(response)
            sample = {"rep": rep, "requestId": request_id, "totalMs": (time.perf_counter() - started) * 1000,
                "audioMs": duration, "bytes": len(audio), "transcriptCharacters": len(transcription.get("text", ""))}
            results["stt"].append(sample)
            private_responses.append({"stt": transcription})
            print(json.dumps({"stt": sample}), flush=True)
    (directory / (label + "-pipeline.json")).write_text(json.dumps(results, indent=2))
    (directory / (label + "-responses.private.json")).write_text(json.dumps(private_responses, indent=2))
    values = [x["chatToFirstSignalMs"] for x in results["samples"] if "chatToFirstSignalMs" in x]
    if values:
        print(json.dumps({"n": len(values), "meanMs": statistics.mean(values), "medianMs": statistics.median(values), "maxMs": max(values)}))


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["prepare", "start", "measure", "stop", "cleanup"])
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--image")
    parser.add_argument("--source-commit", help="exact source of a locally built experiment image")
    parser.add_argument("--label", default="baseline")
    parser.add_argument("--repetitions", type=int, default=2)
    parser.add_argument("--stt", action="store_true")
    args = parser.parse_args()
    if args.action == "prepare":
        prepare(args.directory)
        return
    state = json.loads((args.directory / "state.json").read_text())
    if not state["project"].startswith("aegis_latency_") or not state["database"].startswith("aegis_memory_v060_"):
        raise RuntimeError("invalid disposable experiment")
    if args.action == "start":
        start(state, args.image, args.source_commit, args.directory)
    elif args.action == "measure":
        measure(state, args.directory, args.label, args.repetitions, args.stt)
    elif args.action == "stop":
        dc(state, "stop", "api")
    elif args.action == "cleanup":
        dc(state, "down", "--volumes", "--remove-orphans")
        # Credentials were needed only for temporary runtime configuration.
        # Keep measured identities/results, not another credential-bearing file.
        Path(state["compose"]).unlink()
        print("disposable experiment removed")


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        # Docker stderr may contain credentials from a rejected configuration.
        raise SystemExit("subprocess failed (exit " + str(error.returncode) + "); inspect private state locally") from None
