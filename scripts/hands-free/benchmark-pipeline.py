#!/usr/bin/env python3
"""Bounded, opt-in end-to-end voice measurements on disposable infrastructure.

Requires benchmark-latency.py prepare/start and a private JSON corpus (never
checked in): [{"id":"...", "wav":"absolute PCM16 mono 16k WAV path"}].
Optional websocket-client is needed only for realtime experiments. No provider
key is accepted by this client. Never use this helper against the owner's DB.
"""
import argparse
import array
import concurrent.futures
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import time
import urllib.error
import urllib.request
import uuid
import wave

HERE = Path(__file__).parent
spec = importlib.util.spec_from_file_location("latency", HERE / "benchmark-latency.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)
spec = importlib.util.spec_from_file_location("probes", HERE / "benchmark-turn-probes.py")
probes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probes)


def sql(state, query):
    return bench.command("docker", "compose", "-p", state["project"], "-f", state["compose"],
        "exec", "-T", "postgres", "psql", "-qAt", "-v", "ON_ERROR_STOP=1",
        "-U", "aegis_eval", "-d", state["database"], input=query.encode()).decode()


def fixture_identity(state):
    node = uuid.uuid4()
    credential = "aegis-node-v1." + node.hex + "." + secrets.token_urlsafe(32)
    digest = hashlib.sha256(credential.encode()).hexdigest()
    sql(state, f'''BEGIN; INSERT INTO "aegis_nodes" ("Id","Name","Platform","AppVersion","ProtocolVersion",
        "Enabled","ReceiveReminders","TargetPriority","PairedAt","CreatedAt","UpdatedAt") VALUES
        ('{node}','Pipeline disposable fixture','Android','0.8.0',1,true,false,0,now(),now(),now());
        INSERT INTO "node_credentials" ("NodeId","SecretHash","CreatedAt") VALUES
        ('{node}',decode('{digest}','hex'),now()); COMMIT;''')
    return str(node), credential


def tts_identity():
    # Inherited URL only; direct readiness never synthesizes/warms speech.
    live = json.loads(bench.command("docker", "inspect", "aegis-api"))[0]
    env = dict(x.split("=", 1) for x in live["Config"]["Env"] if "=" in x)
    base = env.get("AEGIS_TTS_BASE_URL", "").rstrip("/")
    if not base:
        return {"exposed": False}
    headers = {}
    if env.get("AEGIS_TTS_API_TOKEN"):
        headers["Authorization"] = "Bearer " + env["AEGIS_TTS_API_TOKEN"]
    try:
        with urllib.request.urlopen(urllib.request.Request(base + "/ready", headers=headers), timeout=15) as response:
            return {"httpStatus": response.status, **{k: v for k, v in response.headers.items()
                    if k.lower().startswith(("x-aegis-tts-", "x-aegis-voice-"))}}
    except urllib.error.HTTPError as error:
        return {"httpStatus": error.code, **{k: v for k, v in error.headers.items()
                    if k.lower().startswith(("x-aegis-tts-", "x-aegis-voice-"))}}
    except Exception as error:
        return {"errorType": type(error).__name__}


def realtime(base, recording, credential, turn, endpoint_ms):
    import websocket
    request = str(uuid.uuid4())
    with wave.open(str(recording), "rb") as source:
        if (source.getnchannels(), source.getsampwidth(), source.getframerate()) != (1, 2, 16000):
            raise ValueError("require mono PCM16/16000 WAV")
        audio = source.readframes(source.getnframes())
    # Reference boundary is the end of the synthetic waveform. The 800ms local
    # endpoint delay is simulated, with no invented PCM/padding sent to provider.
    started = time.perf_counter()
    ws = websocket.create_connection(base.replace("http", "ws", 1) +
        "/api/voice/transcriptions/realtime?transcriptionRequestId=" + request,
        header=["Authorization: AegisNode " + credential, "X-Aegis-Turn-Id: " + turn], timeout=45)
    try:
        ready = json.loads(ws.recv())
        if ready.get("type") != "ready" or ready.get("transcriptionRequestId") != request:
            raise RuntimeError("invalid ready identity")
        first_send = time.perf_counter()
        for offset in range(0, len(audio), 6400):
            ws.send_binary(audio[offset:offset + 6400])
            # Reproduce capture pacing, not an inference warm-up.
            target = first_send + min(offset + 6400, len(audio)) / 32000
            time.sleep(max(0, target - time.perf_counter()))
        speech_end = time.perf_counter()
        time.sleep(endpoint_ms / 1000)
        committed = time.perf_counter()
        ws.send(json.dumps({"type": "commit"}))
        while True:
            event = json.loads(ws.recv())
            if event.get("type") == "error":
                raise RuntimeError("relay rejected realtime")
            if event.get("type") == "final":
                if event.get("transcriptionRequestId") != request:
                    raise RuntimeError("invalid final identity")
                return event, {"requestId": request, "connectReadyMs": (first_send-started)*1000,
                    "capturedMs": len(audio)/32, "endpointMs": (committed-speech_end)*1000,
                    "residualCommitMs": (time.perf_counter()-committed)*1000}, speech_end
    finally:
        ws.close()


def segmented(base, payload, started):
    payload = dict(payload, voiceSegments=True)
    first_segment = None
    segments = []
    done = None
    transcript = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as worker:
        pending = []
        with bench.http(base, "/api/chat/messages/stream", payload) as response:
            for line in response:
                event = json.loads(line)
                transcript.append(event)
                if event.get("type") == "error":
                    raise RuntimeError("chat stream failed")
                if event.get("type") == "final_segment":
                    # Do not derive speech from token/argument/intermediate events.
                    if first_segment is None:
                        first_segment = (time.perf_counter()-started)*1000
                    if len(pending) >= 2:
                        pending.pop(0).result()
                    segment = event.get("segment", event)
                    body = {k: segment[k] for k in ("turnId", "conversationId", "assistantMessageId", "segmentId", "sequence")}
                    body["speechRequestId"] = str(uuid.uuid4())
                    queued = time.perf_counter()
                    def fetch(body=body, queued=queued):
                        with bench.http(base, "/api/voice/speech/segments", body) as audio:
                            validate_audio(audio)
                            return {"sequence": body["sequence"], "requestAtMs": (queued-started)*1000,
                                "audio": bench.pcm(audio, started)}
                    future = worker.submit(fetch)
                    pending.append(future)
                    segments.append(future)
                if event.get("type") == "done":
                    done = (time.perf_counter()-started)*1000
        audio = [future.result(timeout=180) for future in segments]
    if done is None or not audio:
        raise RuntimeError("missing finalized output")
    return {"firstFinalSegmentMs": first_segment, "generationDoneMs": done,
            "firstSignalMs": audio[0]["audio"]["firstSignalMs"], "segments": audio}, transcript


def validate_audio(response):
    for key, expected in {"X-Aegis-Audio-Format": "pcm_s16le", "X-Aegis-Sample-Rate": "24000", "X-Aegis-Channels": "1"}.items():
        if response.headers.get(key) != expected:
            raise RuntimeError("incompatible speech format")


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--label", required=True)
    parser.add_argument("--realtime", action="store_true")
    parser.add_argument("--segments", action="store_true")
    parser.add_argument("--endpoint-ms", type=int, default=800)
    args = parser.parse_args()
    if not 0 <= args.endpoint_ms <= 2000:
        raise ValueError("bounded endpoint simulation")
    state = json.loads((args.directory / "state.json").read_text())
    with bench.http(state["base"], "/api/memory/diagnostics/environment") as response:
        identity = json.load(response)
    if not identity.get("isolated") or identity.get("database") != state["database"]:
        raise RuntimeError("require verified disposable infrastructure")
    corpus = json.loads(args.corpus.read_text())
    if not 1 <= len(corpus) <= 8:
        raise ValueError("require 1-8 reserved cases")
    output = {"schema": 1, "label": args.label, "image": state.get("measurementImage"),
        "sourceCommit": state["sourceCommit"], "ttsBefore": tts_identity(),
        "conditions": {"physicalDevice": False, "speakerAcousticsMeasured": False,
            "endpointSimulatedMs": args.endpoint_ms, "waveformEndReference": True,
            "realtime": args.realtime, "segments": args.segments}, "samples": []}
    private = []
    if output["ttsBefore"].get("httpStatus") != 200:
        (args.directory / (args.label + ".json")).write_text(json.dumps(output, indent=2))
        raise SystemExit("TTS is not ready; no transcription/model/speech calls started")
    node, credential = fixture_identity(state) if args.realtime else (None, None)
    try:
        for case in corpus:
            turn = str(uuid.uuid4())
            sample = {"case": case["id"], "turnId": turn,
                "fixtureSha256": hashlib.sha256(Path(case["wav"]).read_bytes()).hexdigest()}
            with wave.open(str(case["wav"]), "rb") as fixture:
                samples = array.array("h", fixture.readframes(fixture.getnframes()))
                last_signal = next((i for i in range(len(samples)-1, -1, -1) if abs(samples[i]) >= 64), None)
                sample["trailingDigitalQuietMs"] = None if last_signal is None else (len(samples)-last_signal-1)/16
            try:
                if args.realtime:
                    final, sample["stt"], started = realtime(state["base"], Path(case["wav"]), credential,
                                                            turn, args.endpoint_ms)
                else:
                    started = time.perf_counter()
                    time.sleep(args.endpoint_ms/1000)
                    commit = time.perf_counter()
                    final, request = probes.transcription(state["base"], Path(case["wav"]), turn)
                    sample["stt"] = {"requestId": request, "endpointMs": (commit-started)*1000,
                        "residualCommitMs": (time.perf_counter()-commit)*1000}
                sample["sttFinalAtMs"] = (time.perf_counter()-started)*1000
                payload = {"turnId": turn, "content": final["text"], "origin": "hands_free",
                    "voiceSessionId": str(uuid.uuid4())}
                if args.segments:
                    metrics, events = segmented(state["base"], payload, started)
                    sample.update(metrics)
                    private.append({"case": case["id"], "transcription": final, "events": events})
                else:
                    with bench.http(state["base"], "/api/chat/messages", payload) as response:
                        chat = json.load(response)
                    sample["generationDoneMs"] = (time.perf_counter()-started)*1000
                    tts_start = time.perf_counter()
                    with bench.http(state["base"], "/api/voice/speech", {"turnId": turn,
                        "speechRequestId": str(uuid.uuid4()), "assistantMessageId": chat["message"]["id"]}) as response:
                        validate_audio(response)
                        sample["audio"] = bench.pcm(response, started)
                    sample["ttsRequestAtMs"] = (tts_start-started)*1000
                    sample["firstSignalMs"] = sample["audio"]["firstSignalMs"]
                    private.append({"case": case["id"], "transcription": final, "chat": chat})
                sample["totalMs"] = (time.perf_counter()-started)*1000
                if sample.get("firstSignalMs") is not None and sample["trailingDigitalQuietMs"] is not None:
                    sample["lastInputSignalToFirstOutputSignalMs"] = sample["firstSignalMs"] + sample["trailingDigitalQuietMs"]
            except Exception as error:
                sample["errorType"] = type(error).__name__
            output["samples"].append(sample)
            (args.directory / (args.label + ".json")).write_text(json.dumps(output, indent=2))
            (args.directory / (args.label + ".private.json")).write_text(json.dumps(private, indent=2))
            print(json.dumps(sample), flush=True)
    finally:
        if node:
            sql(state, f'''DELETE FROM "node_credentials" WHERE "NodeId"='{node}';
                          DELETE FROM "aegis_nodes" WHERE "Id"='{node}';''')
        output["ttsAfter"] = tts_identity()
        (args.directory / (args.label + ".json")).write_text(json.dumps(output, indent=2))


if __name__ == "__main__":
    main()
