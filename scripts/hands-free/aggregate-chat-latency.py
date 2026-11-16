#!/usr/bin/env python3
"""Build a sanitized JSON report from synthetic chat benchmarks and analyses.

Reads only normal harness/analysis JSON, never private IDs, raw logs, prompts,
audio or provider payloads. Missing future labels are reported as not available.
No HTTP, Docker, inference or microphone operations are performed.
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import statistics

spec = importlib.util.spec_from_file_location("profile", Path(__file__).with_name("analyze-chat-profile.py"))
profile = importlib.util.module_from_spec(spec)
spec.loader.exec_module(profile)

DEFAULT_LABELS = ["current-profile", "current-recheck", "priority-profile", "priority-recheck",
    "http2-control", "http1-control"]
API_FIELDS = {
    "enterToFirstFlushMs": "enterToFirstFlushMs",
    "preModelMs": "beforeFirstModelMs",
    "firstModelSegmentToAuthorizedPersistedSegmentMs": "firstModelSegmentToApiSegmentMs",
    "authorizedSegmentToFlushMs": "firstApiSegmentToFlushMs",
    "outsideProfileResidualMs": "outsideProfileResidualMs",
}
BASELINE_REVISION = "350f892442acba5e1826ee60d376935c75a4fd49"


def number(value):
    return value if type(value) in (int, float) and math.isfinite(value) else None


def stats(values):
    values = [value for value in values if number(value) is not None]
    rounded = lambda value: round(value, 3) if value is not None else None
    return {"n": len(values),
        "avg": rounded(statistics.mean(values)) if values else None,
        "median": rounded(statistics.median(values)) if values else None,
        "range": {"min": rounded(min(values)) if values else None,
            "max": rounded(max(values)) if values else None}}


def token_stats(values):
    observed = [value for value in values if type(value) is int and value >= 0]
    return dict(stats(observed), observedTotal=sum(observed) if observed else None)


def safe_software(public):
    revision, image = public.get("sourceCommit", ""), public.get("image", "")
    return {
        "apiRevision": revision if isinstance(revision, str) and re.fullmatch(r"[0-9a-f]{40}", revision) else None,
        "apiImage": image if isinstance(image, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", image) else None,
    }


def summarize_profiles(samples):
    result = {"samples": len(samples),
        "completeThroughFirstFlush": sum(s.get("profileStatus") == "complete_through_first_flush" for s in samples)}
    result["api"] = {name: stats(s.get("api", {}).get(field) for s in samples)
        for name, field in API_FIELDS.items()}
    context = defaultdict(list)
    for sample in samples:
        for row in sample.get("contextStages", []):
            stage = row.get("stage", "")
            if isinstance(stage, str) and re.fullmatch(r"(context|memory)\.[a-zA-Z0-9_.]+", stage):
                context[stage].append(row.get("durationMs"))
    result["context"] = {
        "prepareMs": stats(s.get("api", {}).get("prepareMs") for s in samples),
        "subStagesMs": {name: stats(values) for name, values in sorted(context.items())},
        "historyMessages": stats(s.get("api", {}).get("historyMessages") for s in samples),
        "memoryCharacters": stats(s.get("api", {}).get("memoryCharacters") for s in samples),
    }
    rounds = [r for sample in samples for r in sample.get("rounds", [])]
    result["provider"] = {
        "rounds": len(rounds),
        "requestCharactersPerRound": stats(r.get("requestCharacters") for r in rounds),
        "hostedToolSearchCallsPerRound": token_stats(r.get("hostedToolSearchCalls") for r in rounds),
        "isolatedNoPromptCachePerRound": token_stats(r.get("isolatedNoPromptCache") for r in rounds),
        "requestToHeadersMs": stats(r.get("intervalsMs", {}).get("requestToResponseHeaders") for r in rounds),
        "requestToFirstSseMs": stats(r.get("milestonesFromRequestStartMs", {}).get("firstSse") for r in rounds),
        "requestToFirstArgumentMs": stats(r.get("milestonesFromRequestStartMs", {}).get("firstArgumentDelta") for r in rounds),
        "requestToFirstFinalSegmentMs": stats(r.get("intervalsMs", {}).get("requestToFirstFinalSegment") for r in rounds),
        "firstArgumentToFirstFinalSegmentMs": stats(r.get("intervalsMs", {}).get("firstArgumentToFirstFinalSegment") for r in rounds),
        "reportedProcessingMs": stats(r.get("providerReportedProcessingMs") for r in rounds),
        "serviceTiersObserved": dict(Counter(r.get("serviceTier") if r.get("serviceTier") in profile.TIERS else "unknown" for r in rounds)),
        "httpProtocolsObserved": dict(Counter(r.get("httpProtocol", {}).get("version")
            if r.get("httpProtocol", {}).get("version") in {"1.1", "2.0", "3.0"} else "unknown" for r in rounds)),
    }
    tools = defaultdict(list)
    for current in rounds:
        for tool in current.get("toolsAfterRound", []):
            stage = tool.get("stage", "")
            if isinstance(stage, str) and re.fullmatch(r"tool\.[a-zA-Z0-9_.]+", stage):
                tools[stage].append(tool.get("durationMs"))
    result["toolsMs"] = {stage: stats(values) for stage, values in sorted(tools.items())}
    result["tokensPerRound"] = {name: token_stats(r.get("tokens", {}).get(name) for r in rounds)
        for name in profile.TOKEN_STAGES}
    result["cachedInputFractionPerRound"] = stats(
        r["tokens"]["cachedInput"] / r["tokens"]["input"] for r in rounds
        if number(r.get("tokens", {}).get("input")) is not None and r["tokens"]["input"] > 0
        and number(r.get("tokens", {}).get("cachedInput")) is not None)
    return result


def dataset(directory, label, expected_database):
    if not profile.segments.LABEL.fullmatch(label):
        raise ValueError("invalid dataset label")
    harness_path = directory / (label + "-chat-segments.json")
    if not harness_path.exists():
        return {"label": label, "available": False, "status": "not_available"}, [], []
    public = json.loads(harness_path.read_text())
    conditions = public.get("conditions", {})
    if (conditions.get("isolated") is not True or conditions.get("syntheticData") is not True or
            conditions.get("origin") != "hands_free" or conditions.get("voiceSegments") is not True or
            conditions.get("speechRequests") != 0 or conditions.get("database") != expected_database):
        raise ValueError("dataset is outside synthetic chat-only experiment")
    samples = public.get("samples", [])
    aliases = {}
    for sample in samples:
        key, case = sample.get("sample"), sample.get("case")
        if (not isinstance(key, str) or not profile.SAMPLE.fullmatch(key) or key in aliases or
                not isinstance(case, str) or not profile.CASE.fullmatch(case)):
            raise ValueError("invalid sample aliases")
        aliases[key] = sample
    analysis_path = directory / (label + "-analysis.json")
    analysis = json.loads(analysis_path.read_text()) if analysis_path.exists() else {}
    profiled = analysis.get("samples", [])
    for sample in profiled:
        original = aliases.get(sample.get("sample"))
        if original is None or original.get("case") != sample.get("case"):
            raise ValueError("analysis and harness samples differ")
    completed = [sample for sample in samples if number(sample.get("firstFinalSegmentMs")) is not None
        and number(sample.get("generationDoneMs")) is not None and "errorType" not in sample]
    subsequent = [sample for sample in completed if sample.get("firstCallInHarness") is False]
    without_tools = [sample for sample in subsequent if number(sample.get("toolStatusStarted")) == 0]
    metric = lambda rows, field: stats(row.get(field) for row in rows)
    by_case = defaultdict(list)
    for sample in completed:
        by_case[sample["case"]].append(sample.get("firstFinalSegmentMs"))
    result = {
        "label": label, "available": True, "status": "complete" if len(completed) == len(samples) else "has_incomplete_requests",
        "chatRequests": len(samples), "completedRequests": len(completed),
        "incompleteRequests": len(samples)-len(completed),
        "software": safe_software(public),
        "configuration": profile.public_source(analysis.get("source", {})),
        "firstFinalSegmentMs": {
            "all": metric(completed, "firstFinalSegmentMs"),
            "firstInHarness": metric([s for s in completed if s.get("firstCallInHarness") is True], "firstFinalSegmentMs"),
            "subsequent": metric(subsequent, "firstFinalSegmentMs"),
            "subsequentWithoutToolEvents": metric(without_tools, "firstFinalSegmentMs"),
            "bySyntheticCase": {case: stats(values) for case, values in sorted(by_case.items())},
        },
        "effectChecks": {"createReminder": {
            "cases": sum(sample.get("case") == "create_reminder" for sample in samples),
            "verified": sum(sample.get("case") == "create_reminder" and all(
                sample.get("reminderVerification", {}).get(key) is True for key in
                ("exactlyOneCreated", "dueAtMatches", "timeZoneMatches", "scheduled")) for sample in samples),
            "verificationMs": stats(sample.get("reminderVerificationMs") for sample in samples
                if sample.get("case") == "create_reminder"),
            "verificationTiming": "Before/after authenticated reads and postcondition checks are outside first-final-segment timing.",
        }},
        "generationDoneMs": metric(completed, "generationDoneMs"),
        "transportEndMs": metric(completed, "transportEndMs"),
        "profile": summarize_profiles(profiled),
        "profileByCohort": {
            "firstInHarness": summarize_profiles([p for p in profiled if aliases[p["sample"]].get("firstCallInHarness") is True]),
            "subsequent": summarize_profiles([p for p in profiled if aliases[p["sample"]].get("firstCallInHarness") is False]),
            "subsequentWithoutToolEvents": summarize_profiles([p for p in profiled
                if aliases[p["sample"]].get("firstCallInHarness") is False and
                number(aliases[p["sample"]].get("toolStatusStarted")) == 0]),
        },
        "artifacts": {"harness": harness_path.name, "analysis": analysis_path.name if analysis_path.exists() else None},
    }
    return result, samples, profiled



def int_value(value):
    if type(value) is int:
        return value
    if type(value) is float and math.isfinite(value) and value.is_integer():
        return int(value)
    if isinstance(value, str) and re.fullmatch(r"[0-9]+", value):
        return int(value)
    return None


def true_value(value):
    return value is True or value in ("t", "true")



def historical_numeric_features(directory, expected_count):
    path = directory / "historical-numeric-features.json"
    if not path.exists():
        return {"available": False}
    rows = json.loads(path.read_text())
    if (not isinstance(rows, list) or len(rows) != expected_count or
            any(int_value(row.get("audit_matches")) != 1 for row in rows)):
        raise ValueError("historical numeric-feature cohort is incomplete")
    samples = []
    for index, row in enumerate(rows, 1):
        samples.append({
            "observation": index,
            "modelRequests": int_value(row.get("model_request_count")),
            "executedTools": int_value(row.get("executed_tool_count")),
            "firstSegmentCharacters": int_value(row.get("first_segment_character_count")),
            "lastRoundTokens": {
                "input": int_value(row.get("last_input_tokens")),
                "cachedInput": int_value(row.get("last_cached_input_tokens")),
                "output": int_value(row.get("last_output_tokens")),
                "reasoning": int_value(row.get("last_reasoning_tokens")),
            },
            "usedPlaceList": row.get("used_place_list") if type(row.get("used_place_list")) is bool else None,
            "usedCalendarRead": row.get("used_calendar_read") if type(row.get("used_calendar_read")) is bool else None,
            "usedMemorySearch": row.get("used_memory_search") if type(row.get("used_memory_search")) is bool else None,
            "otherToolCount": int_value(row.get("other_tool_count")),
        })
    fractions = []
    for sample in samples:
        tokens = sample["lastRoundTokens"]
        fractions.append(tokens["cachedInput"]/tokens["input"]
            if tokens["input"] is not None and tokens["input"] > 0 and tokens["cachedInput"] is not None else None)
    return {
        "available": True, "matchedObservations": len(rows),
        "allRequestsLowEffortConfirmed": all(row.get("all_requests_effort_low") is True for row in rows),
        "lastRoundCachedInputFraction": stats(fractions),
        "observations": samples,
        "limitations": [
            "Token values describe the last model request of each historical turn, not necessarily earlier tool rounds.",
            "The first two actual turns had no cached input and had reasoning tokens despite low effort.",
            "Warm-cache rates from the synthetic controls cannot be generalized to these four actual turns.",
            "Historical tools were not place_list, calendar_read or memory_search in these numeric flags; the synthetic place_list case is not the same workload.",
        ],
    }


def historical_attribution(directory):
    path = directory / "historical-attribution.json"
    base = {"date": "2026-11-15", "phoneObservations": 4,
        "phoneChatToFirstFinalSegmentAggregateMeanMs": 4286,
        "detailedServerStageProfilingAvailable": False,
        "individualPhoneDurationsAvailable": False,
        "individualFirstFlushAvailable": False}
    if not path.exists():
        return dict(base, attributionStatus="not_available", partialServerAttributionAvailable=False,
            reason="No local numeric historical attribution result was supplied.")
    data = json.loads(path.read_text())
    rows = data.get("rows", [])
    if not rows or any(int_value(row.get("selected_turn_count")) != len(rows) or
            int_value(row.get("successful_audit_matches")) != 1 or
            int_value(row.get("first_segment_sequence")) != 0 or
            not true_value(row.get("voice_segments_complete")) or
            not true_value(row.get("clock_order_consistent")) for row in rows):
        raise ValueError("historical cohort matching evidence is incomplete")
    fields = {
        "userMessageCreatedToRuntimeContext": "user_to_runtime_context_ms",
        "runtimeContextToFirstSegmentCreated": "runtime_to_first_segment_created_ms",
        "userMessageCreatedToFirstSegmentCreated": "user_to_first_segment_created_ms",
        "fullModelToolLoop": "full_model_tool_loop_ms",
    }
    means = {}
    for name, field in fields.items():
        values = [number(row.get(field)) for row in rows]
        if any(value is None for value in values):
            raise ValueError("historical numeric interval is missing")
        means[name] = round(statistics.mean(values), 6)
    phone = number(data.get("phoneChatToFirstFinalSegmentAggregateMeanMs"))
    if phone is None:
        raise ValueError("historical phone cohort mean is missing")
    remaining = round(phone - means["userMessageCreatedToFirstSegmentCreated"], 6)
    source = data.get("sourceApiCommit", "")
    return dict(base, attributionStatus="partial_recovered", partialServerAttributionAvailable=True,
        matchedObservations=len(rows), successfulAuditMatches=len(rows), cohortMatched=True,
        sourceApiRevision=source if isinstance(source, str) and re.fullmatch(r"[0-9a-f]{40}", source) else None,
        phoneChatToFirstFinalSegmentAggregateMeanMs=phone, meansMs=means,
        remainingUnattributedAggregateMeanMs=remaining,
        numericFeatures=historical_numeric_features(directory, len(rows)),
        modelCalls=[int_value(row.get("model_calls")) for row in rows],
        toolCalls=[int_value(row.get("tool_calls")) for row in rows],
        evidenceSource="Existing numeric read-only SQL result joined to the four voice sessions; no identifiers or timestamps are exported.",
        limitations=[
            "User-message creation and runtime-context timestamps recover only part of the server timeline.",
            "Runtime context to first-segment creation includes remaining context work, provider communication, model generation and tools; it is not pure inference.",
            "First-segment creation precedes persistence, authorization, HTTP flush and client receipt.",
            "The remaining interval is a difference of matched cohort means, not per-turn network latency.",
            "Provider network versus processing and individual first-flush timings remain unavailable.",
            "Full model/tool-loop duration continues beyond the first segment and must not be added to first-segment intervals.",
        ])


def network_control(path):
    if not path.exists():
        return {"available": False}
    data = json.loads(path.read_text())
    rows = data.get("Samples", [])
    if (data.get("InferenceRequests") != 0 or data.get("AuthenticationUsed") is not False or
            data.get("ExpectedStatus") != 401 or any(row.get("Status") != 401 for row in rows)):
        raise ValueError("require the existing unauthenticated no-inference network control")
    cases = []
    for row in rows:
        policy, phase = row.get("Policy"), row.get("Phase")
        if policy not in {"baseline", "retained"} or phase not in {"initial", "warm", "after70s", "after135s"}:
            raise ValueError("unknown network control cohort")
        durations = {}
        for name in ("dns", "tcp", "tls"):
            values = row.get("DurationsMs", {}).get(name, [])
            if any(number(value) is None or value < 0 for value in values):
                raise ValueError("invalid numeric network interval")
            durations[name] = round(sum(values), 6) if values else None
        observed = [value for value in durations.values() if value is not None]
        version = row.get("Version")
        cases.append({
            "sequence": int_value(row.get("Sequence")), "policy": policy, "phase": phase,
            "startOffsetMs": number(row.get("StartOffsetMs")),
            "headersMs": number(row.get("HeadersElapsedMs")), "totalMs": number(row.get("TotalElapsedMs")),
            "status": 401, "httpProtocol": version if version in {"1.1", "2.0", "3.0"} else None,
            "newConnections": int_value(row.get("NewConnectionCount")),
            "reusedConnection": row.get("ReusedConnection") if type(row.get("ReusedConnection")) is bool else None,
            "reportedDurationsMs": durations,
            "sumOfReportedDnsTcpTlsMs": round(sum(observed), 6) if observed else None,
        })
    baseline = [case for case in cases if case["policy"] == "baseline" and case["phase"] == "after135s"]
    retained = [case for case in cases if case["policy"] == "retained" and case["phase"] == "after135s"]
    return {
        "available": True, "requests": len(cases), "inferenceRequests": 0,
        "authenticated": False, "expectedStatus": 401, "samples": cases,
        "after135s": {
            "observedPairs": min(len(baseline), len(retained)),
            "baselineReportedSetupMs": baseline[0]["sumOfReportedDnsTcpTlsMs"] if len(baseline) == 1 else None,
            "retainedConnectionReused": retained[0]["reusedConnection"] if len(retained) == 1 else None,
        },
        "quantitativePerformanceGainEstablished": False,
        "limitations": [
            "Only one baseline/retained pair was observed after the long idle interval.",
            "Requests returned unauthenticated 401 responses and did not execute an inference.",
            "Reported DNS/TCP/TLS intervals are observations, not a proven causal response-time saving.",
            "This control does not measure authenticated chat, mobile transport or speech playback.",
        ],
    }



def default_labels(directory):
    if directory.name.endswith(("-namespaces", "-cold-control")):
        return ["namespace-control", "eager-control"]
    return ["deferred-control", "eager-control"] if directory.name.endswith("-deferred") else DEFAULT_LABELS


def experiment_context(directory, labels, datasets, recorded_requests):
    original = directory.name == "latency-20261116-isolated"
    forced_uncached = directory.name.endswith("-cold-control")
    namespaces = directory.name.endswith("-namespaces") or "namespace-control" in labels
    deferred = not namespaces and set(labels).issubset({"deferred-control", "eager-control"})
    if original and not set(labels).issubset(DEFAULT_LABELS):
        raise ValueError("the archived 48-request report must not include a new experiment")
    if original:
        return {
            "reportType": "synthetic_server_chat_latency_control",
            "runClosure": {"status": "closed", "referenceExperimentRequests": 48,
                "recordedRequests": recorded_requests,
                "temporaryResources": {"cleanupCompleted": True, "containersRemaining": 0,
                    "privateComposeRemoved": True,
                    "verificationSource": "Coordinator's completed original-project cleanup; aggregator performs no Docker/API operations."}},
            "qualitativeReview": {
                "reviewedSamples": sum(d.get("chatRequests", 0) for d in datasets if d["label"] in
                    {"current-profile", "current-recheck", "priority-profile", "priority-recheck"}),
                "blockedSamples": sum(d.get("chatRequests", 0) for d in datasets if d["label"] in
                    {"http2-control", "http1-control"}),
                "allSamplesApproved": False, "status": "partial_review_only",
                "evidenceSource": "Coordinator review of the original 48-request cohort only; blocked means content review unavailable."},
            "productionRestoration": {"baselineApiRevision": BASELINE_REVISION,
                "profilerEnabled": False, "http2OptInEnabled": False,
                "f5Variant": "C", "f5Profile": "20261113-h-stream-pronunciation",
                "synthesisRequestsFromExperiment": 0,
                "verificationSource": "Coordinator's original restoration checks; not a new production-state check."},
        }
    return {
        "reportType": "forced_uncached_control" if forced_uncached else "namespaced_tool_schema_default_tier_control" if namespaces else "deferred_tool_schema_default_tier_control" if deferred else "synthetic_server_chat_latency_control",
        "runClosure": {"status": "not_recorded", "referenceExperimentRequests": 8 if forced_uncached else 18 if namespaces else 16 if deferred else None,
            "recordedRequests": recorded_requests,
            "temporaryResources": {"cleanupCompleted": None, "containersRemaining": None,
                "privateComposeRemoved": None,
                "verificationSource": "No cleanup-completion statement is supplied for this new experiment."}},
        "qualitativeReview": {"reviewedSamples": None, "blockedSamples": None,
            "allSamplesApproved": False, "status": "not_recorded",
            "evidenceSource": "The original 32-reviewed/16-blocked counts do not apply to this new cohort."},
        "productionRestoration": {"status": "not_claimed_for_this_experiment",
            "verificationSource": "This file-only report makes no new production-state or promotion claim."},
    }


def build_report(directory, labels):
    state = json.loads((directory / "state.json").read_text())
    if (not str(state.get("project", "")).startswith("aegis_latency_") or
            not str(state.get("database", "")).startswith("aegis_memory_v060_")):
        raise ValueError("require disposable experiment state")
    if len(labels) != len(set(labels)):
        raise ValueError("duplicate dataset labels")
    datasets, all_samples, all_profiles = [], [], []
    for label in labels:
        result, samples, profiles = dataset(directory, label, state["database"])
        datasets.append(result)
        all_samples.extend(samples)
        all_profiles.extend(profiles)
    context = experiment_context(directory, labels, datasets, len(all_samples))
    return {
        "schema": 1, "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "reportType": context["reportType"],
        "software": {
            "productionApiBaselineRevision": BASELINE_REVISION,
            "historicalPhoneAppVersion": "0.8.0-unstable.15",
            "historicalPhoneBuild": 8000015,
            "measurementSource": "Disposable API on server 22; source revision and image are recorded per dataset.",
        },
        "totals": {
            "syntheticChatRequests": len(all_samples),
            "completedRequests": sum(number(s.get("firstFinalSegmentMs")) is not None
                and number(s.get("generationDoneMs")) is not None and "errorType" not in s for s in all_samples),
            "samplesWithDetailedProfiles": len(all_profiles),
            "datasetsAvailable": sum(dataset["available"] for dataset in datasets),
            "datasetsNotAvailable": sum(not dataset["available"] for dataset in datasets),
            "sttRequests": 0, "speechRequests": 0, "physicalPhoneMeasurements": 0,
        },
        "historicalPhoneAggregate": historical_attribution(directory),
        "networkControl": network_control(directory.with_name("latency-20261116-network-control") / "network-control.json"),
        "userConstraints": {"normalOpenAIRequestsAllowed": True,
            "premiumFastPriorityProhibited": True, "premiumFastPriorityAllowed": False,
            "newBenchmarkCallsPerformedByAggregation": 0, "premiumModeSuggested": False},
        "runClosure": context["runClosure"],
        "qualitativeReview": context["qualitativeReview"],
        "productionRestoration": context["productionRestoration"],
        "phoneValidation": {
            "status": "pending", "samplesIncluded": 0,
            "metricRequested": "Last actual spoken speech to actual audible response.",
            "speakerAcousticsMeasured": False,
        },
        "interpretation": {
            "firstInHarness": "First observed call of this run, recorded separately to expose process/connection startup; not proof of hardware or provider coldness.",
            "forcedUncachedControl": "Isolated Development-only explicit-no-breakpoint cache control. It is not hardware coldness or a production cache recommendation; inspect per-round effective markers and observed cache tokens.",
            "subsequent": "All completed calls after the first, including the read-only tool case.",
            "subsequentWithoutToolEvents": "Completed subsequent calls with zero observed started tool-status events.",
            "providerWaits": "Communication, provider queueing and processing are combined; these are not pure inference times.",
            "authorizedPersistedSegment": "Model finalized segment to backend persistence/authorization marker; validation, scheduling and database work are included.",
            "flush": "Authorized segment marker to successful HTTP flush; distinct from preceding persistence/authorization.",
            "residual": "Client first segment duration minus API first-flush elapsed; includes early DI/auth/binding and client/transport work.",
            "httpPreference": "PreferHttp2 is a request preference with fallback; compare it with the protocol actually observed per round.",
            "hostedToolSearch": "Counts hosted tool_search_call output items per provider round; separate from executed application tools.",
            "requestCharacters": "Serialized request characters are not network bytes or prompt tokens; deferred schemas can change prompt loading without shrinking serialized JSON.",
            "context": "Nested context stages and chat.prepare overlap and must not be added together.",
            "missing": "Missing measurements remain null; n reports observed values.",
        },
        "datasetLimitations": [
            "Synthetic requests use the real hands-free segmented backend/context/tool path with an isolated dataset, not the owner's production conversation history.",
            "Sequential, small runs are affected by provider load, cache state, lazy initialization, case mix and evolving continuation history.",
            "Server-local transport is not the phone/VPN/mobile network path.",
            "Streaming generation after the first finalized segment overlaps delivery; full rounds and full chat durations must not be summed as serial stages.",
            "The control sends no audio and performs no STT, TTS or physical playback; it does not establish end-to-end audibility or voice quality.",
            "Historical attribution is partially recovered from persisted numeric timestamps; provider network/processing and individual first-flush timings remain unavailable.",
            "Token counts are observations; this report does not verify provider charges or compute dollar costs.",
        ],
        "decision": {"productionPromotionPerformedByReport": False, "status": "measurement_only",
            "reason": "No automatic promotion decision is made from these variable small samples."},
        "datasets": datasets,
        "profiledAggregate": summarize_profiles(all_profiles),
        "profiledByCohort": {
            "firstInHarness": summarize_profiles([p for p in all_profiles if p.get("firstCallInHarness") is True]),
            "subsequent": summarize_profiles([p for p in all_profiles if p.get("firstCallInHarness") is False]),
            "subsequentWithoutToolEvents": summarize_profiles([p for p in all_profiles
                if p.get("firstCallInHarness") is False and number(p.get("client", {}).get("toolStatusStarted")) == 0]),
        },
    }


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--labels", nargs="+", help="dataset labels; deferred/namespaces directories select their respective two controls")
    args = parser.parse_args()
    report = build_report(args.directory, args.labels or default_labels(args.directory))
    destination = args.directory / "latency-report.json"
    destination.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    destination.chmod(0o600)
    print(json.dumps({"report": destination.name, "totals": report["totals"]}))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        raise SystemExit("latency aggregation failed: " + type(error).__name__) from None
