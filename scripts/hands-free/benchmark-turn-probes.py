#!/usr/bin/env python3
"""Additional bounded live probes on benchmark-latency's disposable API.

stream: compare the first argument delta (not speakable) with complete final text.
full: synthetic WAV -> batch STT -> real hands-free chat -> first useful PCM.
quality: a real continuation and a stored synthetic memory, only in disposable DB.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import time
import urllib.request
import uuid
import wave

spec = importlib.util.spec_from_file_location("latency", Path(__file__).with_name("benchmark-latency.py"))
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


def transcription(base, recording_path, turn):
    audio = recording_path.read_bytes()
    with wave.open(str(recording_path), "rb") as recording:
        duration = round(recording.getnframes() * 1000 / recording.getframerate())
    boundary = "aegis-" + uuid.uuid4().hex
    request_id = str(uuid.uuid4())
    fields = [("transcriptionRequestId", request_id), ("clientDurationMilliseconds", str(duration))]
    form = b"".join(f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n'.encode() for k, v in fields)
    form += f'--{boundary}\r\nContent-Disposition: form-data; name="audio"; filename="synthetic.wav"\r\nContent-Type: audio/wav\r\n\r\n'.encode()
    form += audio + f'\r\n--{boundary}--\r\n'.encode()
    request = urllib.request.Request(base + "/api/voice/transcriptions", data=form,
        headers={"Content-Type": "multipart/form-data; boundary=" + boundary, "X-Aegis-Turn-Id": turn})
    with urllib.request.urlopen(request, timeout=60) as response:
        result = json.load(response)
    return result, request_id


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("probe", choices=["full", "stream", "quality"])
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--label", required=True)
    args = parser.parse_args()
    state = json.loads((args.directory / "state.json").read_text())
    base = state["base"]
    with bench.http(base, "/api/memory/diagnostics/environment") as response:
        identity = json.load(response)
    if not identity.get("isolated") or identity.get("database") != state["database"]:
        raise RuntimeError("require disposable benchmark API")
    output = []
    private = []
    experiment_started = time.perf_counter()
    def failed_probe(error_type, error, traceback):
        # Keep completed samples and a sanitized failure; never serialize raw
        # provider errors, request headers, or exception messages.
        output.append({"errorType": error_type.__name__, "elapsedMs": (time.perf_counter() - experiment_started) * 1000})
        (args.directory / f"{args.label}-{args.probe}.json").write_text(json.dumps(output, indent=2))
        (args.directory / f"{args.label}-{args.probe}.private.json").write_text(json.dumps(private, indent=2))
        print(json.dumps(output[-1]), file=sys.stderr)
    sys.excepthook = failed_probe
    def chat(text, conversation=None, session=None):
        turn = str(uuid.uuid4())
        payload = {"turnId": turn, "content": text, "origin": "hands_free", "voiceSessionId": session or str(uuid.uuid4())}
        if conversation:
            payload["conversationId"] = conversation
        with bench.http(base, "/api/chat/messages", payload) as response:
            result = json.load(response)
        return result, turn
    if args.probe == "full":
        for rep in range(2):
            turn = str(uuid.uuid4())
            started = time.perf_counter()
            result, request_id = transcription(base, args.directory.parent / "continuous.wav", turn)
            transcript = result
            stt_ms = (time.perf_counter() - started) * 1000
            with bench.http(base, "/api/chat/messages", {"turnId": turn, "content": result["text"],
                "origin": "hands_free", "voiceSessionId": str(uuid.uuid4())}) as response:
                result = json.load(response)
            chat_done_ms = (time.perf_counter() - started) * 1000
            with bench.http(base, "/api/voice/speech", {"turnId": turn, "speechRequestId": str(uuid.uuid4()),
                "assistantMessageId": result["message"]["id"]}) as response:
                audio = bench.pcm(response, started)
            sample = {"rep": rep, "turnId": turn, "transcriptionRequestId": request_id,
                "sttMs": stt_ms, "chatMs": chat_done_ms - stt_ms,
                "postEndpointToFirstSignalMs": audio["firstSignalMs"], "audio": audio}
            private.append({"chat": result, "transcription": transcript})
            output.append(sample)
            print(json.dumps(sample), flush=True)
    elif args.probe == "stream":
        for case, question in [bench.CORPUS[0], bench.CORPUS[3]]:
            turn = str(uuid.uuid4())
            started = time.perf_counter()
            events = []
            first_text_ms = done_ms = None
            with bench.http(base, "/api/chat/messages/stream", {"turnId": turn, "content": question,
                "origin": "hands_free", "voiceSessionId": str(uuid.uuid4())}) as response:
                for line in response:
                    event = json.loads(line)
                    elapsed = (time.perf_counter() - started) * 1000
                    if event.get("type") == "token" and first_text_ms is None:
                        first_text_ms = elapsed
                    if event.get("type") == "done":
                        done_ms = elapsed
                    events.append(event)
            output.append({"case": case, "turnId": turn, "firstFinalTextMs": first_text_ms,
                "doneMs": done_ms, "events": len(events)})
            private.append(events)
            print(json.dumps(output[-1]), flush=True)
    else:
        session = str(uuid.uuid4())
        first, _ = chat("Estou em dúvida entre chá e café. Me faça uma pergunta para ajudar a escolher.", session=session)
        continued, _ = chat("Prefiro algo sem cafeína.", first["conversationId"], session)
        stored, _ = chat("Neste teste sintético, lembre que o mascote fictício Aurora prefere a cor azul.")
        recalled, _ = chat("Qual é a cor favorita do mascote fictício Aurora?")
        private = [first, continued, stored, recalled]
        output = [{"continuedSameConversation": first["conversationId"] == continued["conversationId"],
            "initialContinuation": first.get("voiceContinuation"),
            "recallContainsBlue": "azul" in recalled["message"]["content"].lower(),
            "responseCharacters": [len(result["message"]["content"]) for result in private]}]
        print(json.dumps(output[0]), flush=True)
    (args.directory / f"{args.label}-{args.probe}.json").write_text(json.dumps(output, indent=2))
    (args.directory / f"{args.label}-{args.probe}.private.json").write_text(json.dumps(private, indent=2))


if __name__ == "__main__":
    main()
