#!/usr/bin/env python3
"""Chat-only hands-free segment benchmark on benchmark-latency's isolated API.

No microphone, STT, synthesis or player requests are made. This measures the API
path on the server, not mobile end-to-end latency or physical audibility.
Use benchmark-latency.py prepare/start first. Credentials stay in its private
Compose file; this client never accepts or exports a provider credential.
"""
import argparse
from collections import Counter
import importlib.util
import json
import os
from pathlib import Path
import re
import time
import urllib.parse
import uuid

spec = importlib.util.spec_from_file_location("latency", Path(__file__).with_name("benchmark-latency.py"))
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)

CORPUS = [
    ("time", "Que horas são?"),
    ("math", "Quanto é dezessete mais vinte e cinco?"),
    ("connection", "Qual é a diferença entre uma conexão por Wi-Fi e uma conexão por cabo? Responda de forma prática."),
    ("download", "Como eu confiro se um arquivo terminou de baixar sem precisar abrir o conteúdo?"),
    ("memory", "Explique em duas frases a diferença entre memória RAM e armazenamento."),
    ("read_tool", "Liste os lugares cadastrados."),
    ("continuation", "Estou em dúvida entre chá e café para tomar à noite. Me faça uma pergunta para ajudar a escolher."),
    ("continuation_reply", "Prefiro algo sem cafeína e quero dormir cedo. Qual opção faz mais sentido?"),
]
ALLOWED_EVENTS = {"conversation", "token", "tool_status", "memory_activity", "final_segment", "done"}
LABEL = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,63}")


class ProtocolError(RuntimeError):
    """Deliberately has no provider data or response text."""


def require_uuid(value):
    if not isinstance(value, str):
        raise ProtocolError("invalid identity")
    try:
        uuid.UUID(value)
    except (ValueError, AttributeError):
        raise ProtocolError("invalid identity") from None
    return value


def read_stream(response, turn, started, *, conversation=None, clock=time.perf_counter):
    """Consume and validate the whole NDJSON stream, timing only finalized units."""
    require_uuid(turn)
    if conversation is not None:
        require_uuid(conversation)
    counts = Counter()
    events = []
    segment_ids = set()
    segment_characters = []
    assistant = None
    first_segment = done_at = None
    continuation = None
    tool_starts = 0
    for line in response:
        if not line.strip():
            continue
        if len(line) > 1_048_576:
            raise ProtocolError("oversized event")
        try:
            event = json.loads(line)
        except (ValueError, UnicodeError):
            raise ProtocolError("invalid event") from None
        elapsed = (clock() - started) * 1000
        if not isinstance(event, dict) or event.get("turnId") != turn:
            raise ProtocolError("invalid turn")
        kind = event.get("type")
        if kind == "error":
            raise ProtocolError("chat stream failed")
        if done_at is not None or kind not in ALLOWED_EVENTS:
            raise ProtocolError("invalid stream ordering")
        counts[kind] += 1
        events.append(event)
        if kind == "conversation":
            next_conversation = require_uuid(event.get("conversationId"))
            if conversation is not None and next_conversation != conversation:
                raise ProtocolError("conversation changed")
            conversation = next_conversation
        elif kind == "tool_status":
            if event.get("state") == "started":
                tool_starts += 1
        elif kind == "final_segment":
            next_conversation = require_uuid(event.get("conversationId"))
            next_assistant = require_uuid(event.get("assistantMessageId"))
            segment_id = require_uuid(event.get("segmentId"))
            sequence = event.get("sequence")
            content = event.get("content")
            if ((conversation is not None and conversation != next_conversation) or
                    (assistant is not None and assistant != next_assistant) or
                    type(sequence) is not int or sequence != len(segment_characters) or
                    segment_id in segment_ids or not isinstance(content, str) or
                    not content.strip() or len(content) > 1500):
                raise ProtocolError("invalid finalized unit")
            conversation, assistant = next_conversation, next_assistant
            segment_ids.add(segment_id)
            segment_characters.append(len(content))
            if first_segment is None:
                first_segment = elapsed
        elif kind == "done":
            if (not segment_characters or event.get("conversationId") != conversation or
                    event.get("assistantMessageId") != assistant):
                raise ProtocolError("invalid completion")
            continuation = event.get("voiceContinuation")
            if continuation not in (None, "continue", "close"):
                raise ProtocolError("invalid continuation")
            done_at = elapsed
    ended = (clock() - started) * 1000
    if done_at is None:
        raise ProtocolError("incomplete stream")
    return {
        "firstFinalSegmentMs": first_segment,
        "generationDoneMs": done_at,
        "transportEndMs": ended,
        "segments": len(segment_characters),
        "firstSegmentCharacters": segment_characters[0],
        "responseCharacters": sum(segment_characters),
        "eventCounts": dict(counts),
        "toolStatusStarted": tool_starts,
        "continuation": continuation,
    }, {
        "turnId": turn,
        "conversationId": conversation,
        "assistantMessageId": assistant,
        "events": events,
    }


def verify_default_configuration(state, directory):
    """Cost guard for NEW inference only; historical analysis must not call it."""
    match = re.fullmatch(r"aegis_latency_([0-9a-f]{10})", str(state.get("project", "")))
    if match is None or state.get("database") != "aegis_memory_v060_" + match.group(1):
        raise RuntimeError("invalid disposable experiment identity")
    root = directory.resolve(strict=True)
    path = Path(state.get("compose", ""))
    if not path.is_absolute() or path.is_symlink() or path.name != "compose.private.json":
        raise RuntimeError("require this experiment's private Compose file")
    resolved = path.resolve(strict=True)
    if resolved.parent != root:
        raise RuntimeError("private Compose is outside experiment directory")
    configuration = json.loads(resolved.read_text())
    environment = configuration.get("services", {}).get("api", {}).get("environment")
    if (not isinstance(environment, dict) or environment.get("AEGIS_MEMORY_EVAL_ISOLATED") != "true" or
            environment.get("AEGIS_MEMORY_QDRANT_COLLECTION") != state["database"]):
        raise RuntimeError("require isolated experiment configuration")
    if environment.get("AEGIS_OPENAI_SERVICE_TIER") != "default":
        raise RuntimeError("new chat benchmarks require explicitly default service tier")


def verify_isolated(state):
    """Fail closed before any model call, even if a state file points elsewhere."""
    if (not str(state.get("project", "")).startswith("aegis_latency_") or
            not str(state.get("database", "")).startswith("aegis_memory_v060_")):
        raise RuntimeError("invalid disposable identity")
    url = urllib.parse.urlsplit(state["base"])
    if (url.scheme != "http" or url.hostname != "127.0.0.1" or
            url.username is not None or url.password is not None or
            url.path not in ("", "/") or url.query or url.fragment):
        raise RuntimeError("require loopback disposable API")
    with bench.http(state["base"], "/api/memory/diagnostics/environment") as response:
        identity = json.load(response)
    if (identity.get("isolated") is not True or identity.get("database") != state["database"] or
            identity.get("qdrantCollection") != state["database"]):
        raise RuntimeError("refuse non-disposable API")
    return identity


def save(directory, label, public, private):
    for suffix, value in [(".json", public), (".private.json", private)]:
        path = directory / (label + "-chat-segments" + suffix)
        path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
        path.chmod(0o600)


def run(state, directory, label, repetitions, selected):
    if not LABEL.fullmatch(label):
        raise ValueError("invalid label")
    if not 1 <= repetitions <= 3:
        raise ValueError("require 1-3 repetitions")
    cases = [(case, text) for case, text in CORPUS if case in selected]
    if not cases or len(cases) != len(selected) or len(selected) != len(set(selected)):
        raise ValueError("invalid cases")
    if "continuation_reply" in selected and "continuation" not in selected:
        raise ValueError("continuation_reply requires continuation")
    for suffix in (".json", ".private.json"):
        if (directory / (label + "-chat-segments" + suffix)).exists():
            raise RuntimeError("existing measurement label; use a new label")
    verify_default_configuration(state, directory)
    identity = verify_isolated(state)
    public = {
        "schema": 1, "label": label, "sourceCommit": state.get("sourceCommit"),
        "image": state.get("measurementImage", state.get("sourceImage")),
        "conditions": {
            "isolated": True, "syntheticData": True, "physicalDevice": False,
            "mobileNetworkMeasured": False, "speakerAcousticsMeasured": False,
            "origin": "hands_free", "voiceSegments": True,
            "sttRequests": 0, "speechRequests": 0,
            "database": identity["database"], "memoryCountBefore": identity.get("memoryCount"),
            "extractionJobCountBefore": identity.get("extractionJobCount"),
            "firstCallIsNotProofOfColdProvider": True,
        },
        "repetitions": repetitions, "samples": [],
    }
    private = []
    save(directory, label, public, private)
    for rep in range(repetitions):
        previous_conversation = None
        session = str(uuid.uuid4())
        for case, question in cases:
            number = len(public["samples"]) + 1
            sample_id = f"sample-{number:03d}"
            is_reply = case == "continuation_reply"
            turn = str(uuid.uuid4())
            voice_session = session if case in ("continuation", "continuation_reply") else str(uuid.uuid4())
            payload = {"turnId": turn, "content": question, "origin": "hands_free",
                "voiceSessionId": voice_session, "voiceSegments": True}
            if is_reply:
                if previous_conversation is None:
                    raise RuntimeError("continuation predecessor failed")
                payload["conversationId"] = previous_conversation
            sample = {"sample": sample_id, "case": case, "rep": rep,
                "ordinal": number, "firstCallInHarness": number == 1,
                "continuesPreviousConversation": is_reply}
            detail = {"sample": sample_id, "request": payload}
            started = time.perf_counter()
            try:
                headers = {"Content-Type": "application/json", "X-Aegis-Turn-Id": turn}
                with bench.http(state["base"], "/api/chat/messages/stream", payload, headers=headers) as response:
                    sample["responseHeadersMs"] = (time.perf_counter() - started) * 1000
                    metrics, data = read_stream(response, turn, started,
                        conversation=payload.get("conversationId"))
                sample.update(metrics)
                detail.update(data)
                if case == "continuation":
                    previous_conversation = data["conversationId"]
                if case == "read_tool":
                    # Stream categories do not prove which tool ran. Correlate
                    # the private turn ID with profiler tool.place_list afterward.
                    sample["expectedTool"] = "place_list"
                    sample["toolExecutionRequiresProfileConfirmation"] = True
            except Exception as error:
                sample["errorType"] = type(error).__name__
                sample["elapsedMs"] = (time.perf_counter() - started) * 1000
            finally:
                # No synthesis was claimed. Cancel only after transport EOF/failure,
                # outside the measured interval, to release the synthetic turn.
                try:
                    with bench.http(state["base"], "/api/chat/turns/" + turn, method="DELETE") as response:
                        json.load(response)
                    sample["cleanup"] = "cancelled_without_synthesis"
                except Exception as error:
                    sample["cleanupErrorType"] = type(error).__name__
            public["samples"].append(sample)
            private.append(detail)
            save(directory, label, public, private)
            print(json.dumps(sample, ensure_ascii=False), flush=True)
            if "errorType" in sample or "cleanupErrorType" in sample:
                raise RuntimeError("bounded probe stopped after failure")
    return public, private


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True,
        help="existing private benchmark-latency prepare/start directory")
    parser.add_argument("--label", required=True)
    parser.add_argument("--repetitions", type=int, default=1)
    parser.add_argument("--cases", nargs="+", choices=[case for case, _ in CORPUS],
        default=[case for case, _ in CORPUS])
    args = parser.parse_args()
    state = json.loads((args.directory / "state.json").read_text())
    run(state, args.directory, args.label, args.repetitions, args.cases)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Never expose raw HTTP/provider errors or request/response bodies.
        raise SystemExit("chat-only benchmark failed: " + type(error).__name__) from None
