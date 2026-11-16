#!/usr/bin/env python3
"""Collect only numeric isolated-API profiles and join them to chat-only samples.

Run on server 22 while benchmark-latency's disposable API is running. Unrelated
Docker log lines are discarded without persistence or printing. UUIDs remain in
the private filtered log/input only; exported analysis uses sample-NNN aliases.
No model, speech, microphone or STT calls are made by this analyzer.
"""
import argparse
from collections import Counter
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import subprocess
import threading

spec = importlib.util.spec_from_file_location("segments", Path(__file__).with_name("benchmark-chat-segments.py"))
segments = importlib.util.module_from_spec(spec)
spec.loader.exec_module(segments)

NUMBER = r"[0-9]+(?:[.,][0-9]+)?"
PROFILE = re.compile(
    r"LatencyProfile ([0-9a-fA-F-]{36}) ([a-zA-Z0-9_.]+): "
    r"elapsed=(" + NUMBER + r")ms duration=(" + NUMBER + r")ms count=(\(null\)|null|[0-9]+)?"
)
STAMP = re.compile(r"^(\d{4}-\d{2}-\d{2}T[0-9:.]+(?:Z|[+-][0-9:]+))\s")
SAMPLE = re.compile(r"sample-[0-9]{3,6}")
CASE = re.compile(r"[a-z][a-z0-9_]{0,40}")
TIERS = {"default", "auto", "priority", "fast", "flex", "scale"}
TOKEN_STAGES = {
    "input": "model.input_tokens", "cachedInput": "model.cached_input_tokens",
    "cacheWriteInput": "model.cache_write_tokens", "output": "model.output_tokens",
    "reasoning": "model.reasoning_tokens",
}


def parse_line(line):
    match = PROFILE.search(line)
    if not match:
        return None
    turn, stage, elapsed, duration, count = match.groups()
    segments.require_uuid(turn)
    elapsed, duration = float(elapsed.replace(",", ".")), float(duration.replace(",", "."))
    if not math.isfinite(elapsed) or not math.isfinite(duration):
        return None
    return {"turn": turn.lower(), "stage": stage, "elapsedMs": elapsed,
        "durationMs": duration, "count": None if count in (None, "null", "(null)") else int(count),
        "at": STAMP.match(line).group(1) if STAMP.match(line) else None}


def canonical_line(row):
    count = "" if row["count"] is None else str(row["count"])
    return (f'{row["at"] or "-"} LatencyProfile {row["turn"]} {row["stage"]}: '
        f'elapsed={row["elapsedMs"]:.3f}ms duration={row["durationMs"]:.3f}ms count={count}\n')


def load_inputs(directory, label):
    if not segments.LABEL.fullmatch(label):
        raise ValueError("invalid label")
    public = json.loads((directory / (label + "-chat-segments.json")).read_text())
    private = json.loads((directory / (label + "-chat-segments.private.json")).read_text())
    conditions = public.get("conditions", {})
    if (conditions.get("isolated") is not True or conditions.get("syntheticData") is not True or
            conditions.get("origin") != "hands_free" or conditions.get("voiceSegments") is not True or
            conditions.get("speechRequests") != 0):
        raise RuntimeError("require synthetic chat-only segmented samples")
    samples = public.get("samples", [])
    public_map = {}
    for sample in samples:
        key = sample.get("sample")
        if not isinstance(key, str) or not SAMPLE.fullmatch(key) or key in public_map:
            raise ValueError("invalid sample aliases")
        case = sample.get("case")
        if not isinstance(case, str) or not CASE.fullmatch(case):
            raise ValueError("invalid case alias")
        public_map[key] = sample
    turns = {}
    for detail in private:
        key = detail.get("sample")
        if key not in public_map:
            raise ValueError("unmatched private sample")
        turn = detail.get("turnId") or detail.get("request", {}).get("turnId")
        turn = segments.require_uuid(turn).lower()
        if turn in turns or key in turns.values():
            raise ValueError("duplicate private sample identity")
        turns[turn] = key
    if set(turns.values()) != set(public_map):
        raise ValueError("missing private sample identities")
    return public, public_map, turns


def selected_configuration(container):
    # Docker prints only these named non-secret settings, never the full Env list.
    names = ["AEGIS_CHAT_MODEL", "AEGIS_CHAT_REASONING_EFFORT", "AEGIS_MAX_OUTPUT_TOKENS",
        "AEGIS_OPENAI_SERVICE_TIER", "AEGIS_LATENCY_PROFILE", "AEGIS_OPENAI_HTTP2"]
    template = ""
    for name in names:
        prefix = name + "="
        size = str(len(prefix))
        template += ('{{range .Config.Env}}{{if ge (len .) ' + size + '}}{{if eq (slice . 0 ' +
            size + ') ' + json.dumps(prefix) + '}}{{println .}}{{end}}{{end}}{{end}}')
    selected = segments.bench.command("docker", "inspect", "--format", template, container).decode()
    values = dict(line.split("=", 1) for line in selected.splitlines() if "=" in line)
    model = values.get("AEGIS_CHAT_MODEL", "")
    effort = values.get("AEGIS_CHAT_REASONING_EFFORT")
    tokens = values.get("AEGIS_MAX_OUTPUT_TOKENS", "")
    tier = values.get("AEGIS_OPENAI_SERVICE_TIER")
    prefer_http2 = values.get("AEGIS_OPENAI_HTTP2", "").strip().lower()
    return {
        "model": model if re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:/-]{0,159}", model) else None,
        "reasoningEffort": effort if effort in {"none", "minimal", "low", "medium", "high", "xhigh"} else None,
        "maxOutputTokens": int(tokens) if re.fullmatch(r"[0-9]{1,7}", tokens) else None,
        "requestedServiceTier": tier if tier in TIERS else None,
        "profilingEnabled": values.get("AEGIS_LATENCY_PROFILE") == "1",
        "preferHttp2": True if prefer_http2 in {"true", "1"} else False if prefer_http2 in {"false", "0"} else None,
        "scope": "Explicit container environment; null settings may use image defaults.",
    }


def collect(state, turns, destination, expected_image=None):
    """Never write raw Docker logs: canonicalize only selected profile rows."""
    container = segments.bench.dc(state, "ps", "-q", "api").decode().strip()
    if not re.fullmatch(r"[0-9a-f]{12,64}", container):
        raise RuntimeError("require one disposable API container")
    labels = json.loads(segments.bench.command("docker", "inspect", "--format",
        "{{json .Config.Labels}}", container))
    if (labels.get("com.docker.compose.project") != state["project"] or
            labels.get("com.docker.compose.service") != "api"):
        raise RuntimeError("container is outside disposable project")
    image = segments.bench.command("docker", "inspect", "--format", "{{.Image}}", container).decode().strip()
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", image) or (expected_image is not None and image != expected_image):
        raise RuntimeError("measurement image differs from disposable API")
    configuration = selected_configuration(container)
    if not configuration["profilingEnabled"]:
        raise RuntimeError("disposable profiling is disabled")
    source = {"project": state["project"], "database": state.get("database"), "image": image,
        "configurationAtCollectionRecorded": True, "configuration": configuration}
    source_path = destination.with_suffix(".source.json")
    if source_path.exists():
        raise RuntimeError("profile source already exists")
    if destination.exists():
        raise RuntimeError("filtered profile already exists; pass --profile-log to reuse it")
    temporary = destination.with_name(destination.name + ".tmp")
    if temporary.exists():
        raise RuntimeError("temporary profile already exists")
    process = subprocess.Popen(["docker", "logs", "--timestamps", container],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    timeout = threading.Timer(45, process.kill)
    timeout.daemon = True
    timeout.start()
    count = 0
    try:
        with temporary.open("x", encoding="utf-8") as output:
            temporary.chmod(0o600)
            for line in process.stdout:
                row = parse_line(line)
                if row is not None and row["turn"] in turns:
                    output.write(canonical_line(row))
                    count += 1
        if process.wait() != 0:
            raise RuntimeError("filtered Docker collection failed")
        temporary.replace(destination)
        source_path.write_text(json.dumps(source, indent=2), encoding="utf-8")
        source_path.chmod(0o600)
    except BaseException:
        process.kill()
        process.wait()
        if temporary.exists():
            temporary.unlink()
        raise
    finally:
        timeout.cancel()
        process.stdout.close()
    return count


def read_profiles(path, turns):
    result = {turn: [] for turn in turns}
    with path.open(encoding="utf-8") as source:
        for line in source:
            if not line.strip():
                continue
            row = parse_line(line)
            if row is None or canonical_line(row).strip() != line.strip():
                raise ValueError("require canonical filtered profile log")
            if row["turn"] in result:
                result[row["turn"]].append(row)
    return result


def first(rows, stage):
    return next((row for row in rows if row["stage"] == stage), None)


def elapsed(row):
    return row["elapsedMs"] if row is not None else None


def count(row):
    return row["count"] if row is not None else None


def delta(end, start):
    return None if end is None or start is None else end - start


def measured_span(row):
    return {"stage": row["stage"], "durationMs": row["durationMs"],
        "startElapsedMs": row["elapsedMs"] - row["durationMs"],
        "endElapsedMs": row["elapsedMs"]}


def numeric(value):
    return value if type(value) in (int, float) and math.isfinite(value) else None


def analyze_sample(sample, rows):
    # Preserve input sequence for exact-time ties while making timing order explicit.
    rows = sorted(rows, key=lambda row: row["elapsedMs"])
    result = {"sample": sample["sample"], "case": sample["case"],
        "rep": numeric(sample.get("rep")), "ordinal": numeric(sample.get("ordinal")),
        "firstCallInHarness": sample.get("firstCallInHarness") is True,
        "continuesPreviousConversation": sample.get("continuesPreviousConversation") is True,
        "profileRows": len(rows), "profileStatus": "missing" if not rows else "partial",
        "client": {key: numeric(sample.get(key)) for key in (
            "firstFinalSegmentMs", "generationDoneMs", "transportEndMs",
            "segments", "firstSegmentCharacters", "responseCharacters", "toolStatusStarted")}}
    if sum(row["stage"] == "chat.enter" for row in rows) > 1:
        result["profileStatus"] = "ambiguous_multiple_chat_scopes"
        return result
    enter = first(rows, "chat.enter")
    flush = first(rows, "chat.first_final_segment_flushed")
    api_segment = first(rows, "chat.final_segment")
    model_segment = first(rows, "model.final_segment")
    starts = [row for row in rows if row["stage"] == "model.request_start"]
    request_start = starts[0] if starts else None
    prepare = first(rows, "chat.prepare")
    result["api"] = {
        "chatEnterElapsedMs": elapsed(enter),
        "firstModelRequestElapsedMs": elapsed(request_start),
        "firstModelSegmentElapsedMs": elapsed(model_segment),
        "firstApiSegmentElapsedMs": elapsed(api_segment),
        "firstFlushElapsedMs": elapsed(flush),
        "enterToFirstFlushMs": delta(elapsed(flush), elapsed(enter)),
        "beforeFirstModelMs": delta(elapsed(request_start), elapsed(enter)),
        "prepareMs": prepare["durationMs"] if prepare is not None else None,
        "firstModelSegmentToApiSegmentMs": delta(elapsed(api_segment), elapsed(model_segment)),
        "firstApiSegmentToFlushMs": delta(elapsed(flush), elapsed(api_segment)),
        # This intentionally subtracts local DURATIONS, never server/client timestamps.
        # The API origin begins inside the controller, after DI/auth/body binding.
        "outsideProfileResidualMs": delta(numeric(sample.get("firstFinalSegmentMs")), elapsed(flush)),
        "historyMessages": count(first(rows, "context.history_messages")),
        "memoryCharacters": count(first(rows, "context.memory_characters")),
        "modelCallsReported": count(first(rows, "chat.model_calls")),
        "executedToolsReported": count(first(rows, "chat.executed_tools")),
    }
    result["contextStages"] = [measured_span(row) for row in rows
        if row["stage"].startswith(("context.", "memory.")) and row["count"] is None]
    result["contextCounters"] = [{"stage": row["stage"], "count": row["count"]}
        for row in rows if row["stage"].startswith(("context.", "memory.")) and row["count"] is not None]
    result["preModelStages"] = [measured_span(row) for row in rows
        if request_start is not None and row["elapsedMs"] <= request_start["elapsedMs"] and
        row["stage"].startswith("chat.") and row["stage"] not in ("chat.enter", "chat.prepare") and
        row["durationMs"] > 0]
    result["rounds"] = []
    for index, start in enumerate(starts):
        end = starts[index+1]["elapsedMs"] if index+1 < len(starts) else math.inf
        group = [row for row in rows if start["elapsedMs"] <= row["elapsedMs"] < end]
        # request_characters is logged immediately BEFORE request_start.
        lower = starts[index-1]["elapsedMs"] if index else -math.inf
        preceding = [row for row in rows if row["stage"] == "model.request_characters" and
            lower < row["elapsedMs"] <= start["elapsedMs"]]
        characters = preceding[-1] if preceding else None
        header = first(group, "model.response_headers")
        sse = first(group, "model.first_sse")
        argument = first(group, "model.first_argument_delta")
        final = first(group, "model.final_segment")
        final_text = first(group, "model.first_final_text_delta")
        completed = first(group, "model.response_complete")
        round_completed = first(group, "model.round_completed")
        processing = first(group, "model.provider_processing")
        http_major = count(first(group, "model.http_version_major"))
        http_minor = count(first(group, "model.http_version_minor"))
        http_version = f"{http_major}.{http_minor}" if (http_major, http_minor) in {(1, 1), (2, 0), (3, 0)} else None
        tiers = [row["stage"].removeprefix("model.service_tier.") for row in group
            if row["stage"].startswith("model.service_tier.") and
            row["stage"].removeprefix("model.service_tier.") in TIERS]
        round_result = {
            "round": index+1, "reportedIteration": count(round_completed),
            "requestStartElapsedMs": start["elapsedMs"],
            "requestCharacters": count(characters),
            "httpStatus": count(header), "serviceTier": tiers[0] if tiers else None,
            "httpProtocol": {"major": http_major, "minor": http_minor, "version": http_version},
            "providerReportedProcessingMs": processing["durationMs"] if processing is not None else None,
            "milestonesFromRequestStartMs": {
                "responseHeaders": delta(elapsed(header), start["elapsedMs"]),
                "firstSse": delta(elapsed(sse), start["elapsedMs"]),
                "firstArgumentDelta": delta(elapsed(argument), start["elapsedMs"]),
                "firstFinalSegment": delta(elapsed(final), start["elapsedMs"]),
                "firstFinalTextDelta": delta(elapsed(final_text), start["elapsedMs"]),
                "responseComplete": delta(elapsed(completed), start["elapsedMs"]),
                "roundComplete": delta(elapsed(round_completed), start["elapsedMs"]),
            },
            "intervalsMs": {
                "requestToResponseHeaders": delta(elapsed(header), start["elapsedMs"]),
                "headersToFirstSse": delta(elapsed(sse), elapsed(header)),
                "firstSseToFirstArgumentDelta": delta(elapsed(argument), elapsed(sse)),
                "firstArgumentToFirstFinalSegment": delta(elapsed(final), elapsed(argument)),
                "requestToFirstFinalSegment": delta(elapsed(final), start["elapsedMs"]),
            },
            "tokens": {name: count(first(group, stage)) for name, stage in TOKEN_STAGES.items()},
            "toolsAfterRound": [measured_span(row) for row in group if row["stage"].startswith("tool.")],
        }
        result["rounds"].append(round_result)
    if enter is not None and flush is not None and starts:
        result["profileStatus"] = "complete_through_first_flush"
    return result


def public_source(source):
    result = {"configurationAtCollectionRecorded": source.get("configurationAtCollectionRecorded") is True}
    if not result["configurationAtCollectionRecorded"]:
        return result
    image = source.get("image", "")
    configuration = source.get("configuration", {})
    model = configuration.get("model", "")
    effort = configuration.get("reasoningEffort")
    tokens = configuration.get("maxOutputTokens")
    tier = configuration.get("requestedServiceTier")
    result.update({"image": image if isinstance(image, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", image) else None,
        "configuration": {
            "model": model if isinstance(model, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.:/-]{0,159}", model) else None,
            "reasoningEffort": effort if effort in {"none", "minimal", "low", "medium", "high", "xhigh"} else None,
            "maxOutputTokens": tokens if type(tokens) is int and 0 <= tokens <= 9_999_999 else None,
            "requestedServiceTier": tier if tier in TIERS else None,
            "profilingEnabled": configuration.get("profilingEnabled") is True,
            "preferHttp2": configuration.get("preferHttp2") if type(configuration.get("preferHttp2")) is bool else None,
        }})
    return result


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", required=True, type=Path)
    parser.add_argument("--label", required=True)
    parser.add_argument("--profile-log", type=Path,
        help="reuse a canonical filtered private profile log instead of collecting")
    args = parser.parse_args()
    public, samples, turns = load_inputs(args.directory, args.label)
    state = json.loads((args.directory / "state.json").read_text())
    identity = segments.verify_isolated(state)
    if public["conditions"].get("database") != identity["database"]:
        raise RuntimeError("measurement database differs from isolated API")
    path = args.profile_log or args.directory / (args.label + "-profile.private.log")
    if args.profile_log is None:
        collect(state, turns, path, expected_image=public.get("image"))
    source_path = path.with_suffix(".source.json")
    source = json.loads(source_path.read_text()) if source_path.exists() else {"configurationAtCollectionRecorded": False}
    if source.get("configurationAtCollectionRecorded") and (source.get("project") != state["project"] or
            source.get("database") != state["database"] or source.get("image") != public.get("image")):
        raise RuntimeError("profile source differs from measured experiment")
    profiles = read_profiles(path, turns)
    results = [analyze_sample(samples[alias], profiles[turn]) for turn, alias in turns.items()]
    output = {
        "schema": 1, "label": args.label, "source": public_source(source),
        "sourceCommit": public.get("sourceCommit") if re.fullmatch(r"[0-9a-f]{40}", str(public.get("sourceCommit", ""))) else None,
        "conditions": {"isolatedVerified": True, "syntheticData": True,
            "physicalDevice": False, "mobileNetworkMeasured": False,
            "speakerAcousticsMeasured": False, "speechRequests": 0},
        "definitions": {
            "apiOrigin": "Inside chat controller; DI, authentication and request binding precede this origin.",
            "outsideProfileResidual": "Client first final segment duration minus API first flush elapsed duration; includes transport and unprofiled client/server work, not pure network.",
            "providerWaits": "Include communication, queuing and provider processing; never interpreted as pure inference.",
            "providerReportedProcessing": "Optional provider header, retained as reported; not added to or subtracted from measured stages.",
            "overlap": "Context spans may be nested; model generation after first segment overlaps delivery. Do not sum full rounds, nested stages or full chat duration.",
            "missing": "Absent measurements are null; observed zero token counts remain zero.",
        },
        "samples": results,
    }
    destination = args.directory / (args.label + "-analysis.json")
    destination.write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")
    destination.chmod(0o600)
    print(json.dumps({"analysis": destination.name, "samples": len(results),
        "profiles": dict(Counter(row["profileStatus"] for row in results))}))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        raise SystemExit("profile analysis failed: " + type(error).__name__) from None
