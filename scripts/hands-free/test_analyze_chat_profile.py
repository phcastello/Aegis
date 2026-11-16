#!/usr/bin/env python3
"""Offline numeric/privacy/round-boundary checks; no server/provider calls."""
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

spec = importlib.util.spec_from_file_location("profile", Path(__file__).with_name("analyze-chat-profile.py"))
profile = importlib.util.module_from_spec(spec)
spec.loader.exec_module(profile)


class ProfileTests(unittest.TestCase):
    def setUp(self):
        self.turn = str(uuid.uuid4())
        self.sample = {"sample": "sample-001", "case": "math", "rep": 0, "ordinal": 1,
            "firstFinalSegmentMs": 215, "generationDoneMs": 310, "transportEndMs": 312,
            "content": "PRIVATE RESPONSE MUST NEVER BE COPIED"}
    def row(self, stage, elapsed, duration=0, count=None):
        return {"turn": self.turn, "stage": stage, "elapsedMs": elapsed,
            "durationMs": duration, "count": count, "at": None}
    def test_numeric_filter_discards_other_text_and_preserves_null_and_zero(self):
        line = ("2026-11-16T19:22:00.123456789Z PRIVATE PREFIX LatencyProfile " + self.turn +
            " model.reasoning_tokens: elapsed=1,125ms duration=0.000ms count=0 PRIVATE SUFFIX")
        row = profile.parse_line(line)
        self.assertEqual(row["elapsedMs"], 1.125)
        self.assertEqual(row["count"], 0)
        canonical = profile.canonical_line(row)
        self.assertNotIn("PRIVATE", canonical)
        self.assertEqual(profile.parse_line(canonical), row)
        self.assertIsNone(profile.parse_line("private unrelated API log"))
        null = profile.parse_line("LatencyProfile " + self.turn +
            " chat.enter: elapsed=0.100ms duration=0.000ms count=(null)")
        self.assertIsNone(null["count"])
    def test_chain_nested_context_tokens_and_residual(self):
        rows = [self.row("chat.enter", 0), self.row("chat.conversation_load", 1, 1),
            self.row("context.pending_email", 2, .5), self.row("context.runtime", 5, 2),
            self.row("context.prompt", 6, 4), self.row("chat.prepare", 8, 6),
            self.row("model.request_characters", 9, count=400),
            self.row("model.request_start", 10), self.row("model.isolated_no_prompt_cache", 11, count=1),
            self.row("model.response_headers", 100, count=200),
            self.row("model.provider_processing", 100, duration=90),
            self.row("model.http_version_major", 100, count=1), self.row("model.http_version_minor", 100, count=1),
            self.row("model.first_sse", 110), self.row("model.first_argument_delta", 120),
            self.row("model.final_segment", 200, count=24), self.row("chat.final_segment", 209, count=0),
            self.row("chat.first_final_segment_flushed", 211, count=0),
            self.row("model.response_complete", 300), self.row("model.input_tokens", 300, count=1000),
            self.row("model.output_tokens", 300, count=20), self.row("model.cached_input_tokens", 300, count=900),
            self.row("model.reasoning_tokens", 300, count=0), self.row("model.service_tier.default", 300, count=1),
            self.row("model.tool_search_calls", 300, count=0), self.row("model.round_completed", 301, count=1)]
        result = profile.analyze_sample(self.sample, rows)
        self.assertEqual(result["profileStatus"], "complete_through_first_flush")
        self.assertEqual(result["api"]["beforeFirstModelMs"], 10)
        self.assertEqual(result["api"]["prepareMs"], 6)
        self.assertEqual(result["api"]["outsideProfileResidualMs"], 4)
        self.assertEqual(result["api"]["firstModelSegmentToApiSegmentMs"], 9)
        self.assertEqual(result["api"]["firstApiSegmentToFlushMs"], 2)
        current = result["rounds"][0]
        self.assertEqual(current["milestonesFromRequestStartMs"]["responseHeaders"], 90)
        self.assertEqual(current["intervalsMs"]["firstSseToFirstArgumentDelta"], 10)
        self.assertEqual(current["intervalsMs"]["firstArgumentToFirstFinalSegment"], 80)
        self.assertEqual(current["tokens"]["reasoning"], 0)
        self.assertIsNone(current["tokens"]["cacheWriteInput"])
        self.assertEqual(current["providerReportedProcessingMs"], 90)
        self.assertEqual(current["serviceTier"], "default")
        self.assertEqual(current["httpProtocol"]["version"], "1.1")
        self.assertEqual(current["hostedToolSearchCalls"], 0)
        self.assertEqual(current["isolatedNoPromptCache"], 1)
        self.assertEqual(len(result["contextStages"]), 3)
        safe = json.dumps(result)
        self.assertNotIn(self.turn, safe)
        self.assertNotIn("PRIVATE RESPONSE", safe)
    def test_tools_and_tokens_stay_with_their_own_round(self):
        rows = [self.row("chat.enter", 0), self.row("model.request_characters", 1, count=1000),
            self.row("model.request_start", 2), self.row("model.first_argument_delta", 20),
            self.row("model.response_complete", 30), self.row("model.input_tokens", 31, count=300),
            self.row("model.tool_search_calls", 31, count=1),
            self.row("model.round_completed", 32, count=1), self.row("tool.place_list", 50, duration=15),
            self.row("model.request_characters", 51, count=1100), self.row("model.request_start", 52),
            self.row("model.http_version_major", 60, count=2), self.row("model.http_version_minor", 60, count=0),
            self.row("model.first_argument_delta", 70), self.row("model.final_segment", 80),
            self.row("chat.final_segment", 83), self.row("chat.first_final_segment_flushed", 85),
            self.row("model.response_complete", 90), self.row("model.input_tokens", 91, count=400),
            self.row("model.round_completed", 92, count=2)]
        result = profile.analyze_sample(self.sample, rows)
        first, second = result["rounds"]
        self.assertEqual((first["requestCharacters"], second["requestCharacters"]), (1000, 1100))
        self.assertEqual((first["tokens"]["input"], second["tokens"]["input"]), (300, 400))
        self.assertIsNone(first["intervalsMs"]["firstArgumentToFirstFinalSegment"])
        self.assertEqual(second["intervalsMs"]["firstArgumentToFirstFinalSegment"], 10)
        self.assertEqual(first["toolsAfterRound"][0]["stage"], "tool.place_list")
        self.assertEqual(second["toolsAfterRound"], [])
        self.assertEqual(first["hostedToolSearchCalls"], 1)
        self.assertIsNone(second["hostedToolSearchCalls"])
        self.assertIsNone(first["httpProtocol"]["version"])
        self.assertEqual(second["httpProtocol"]["version"], "2.0")
    def test_missing_marks_remain_null_and_multiple_scopes_are_flagged(self):
        result = profile.analyze_sample(self.sample, [self.row("chat.enter", 0)])
        self.assertIsNone(result["api"]["outsideProfileResidualMs"])
        self.assertIsNone(result["api"]["prepareMs"])
        self.assertEqual(result["rounds"], [])
        result = profile.analyze_sample(self.sample, [self.row("chat.enter", 0), self.row("chat.enter", 1)])
        self.assertEqual(result["profileStatus"], "ambiguous_multiple_chat_scopes")
        self.assertNotIn("api", result)
    def test_source_export_copies_only_allowlisted_metadata(self):
        source = {"configurationAtCollectionRecorded": True, "image": "sha256:" + "a"*64,
            "project": "PRIVATE project", "unexpected": "PRIVATE extra",
            "configuration": {"model": "luna", "reasoningEffort": "low", "maxOutputTokens": 7000,
                "requestedServiceTier": "priority", "profilingEnabled": True, "preferHttp2": True, "deferTools": True, "toolNamespaces": True, "latencyNoPromptCache": True, "noPromptCacheDevelopmentGuard": True,
                "noPromptCacheIsolatedGuard": True, "secret": "PRIVATE value"}}
        exported = profile.public_source(source)
        self.assertEqual(exported["configuration"]["requestedServiceTier"], "priority")
        self.assertIs(exported["configuration"]["preferHttp2"], True)
        self.assertIs(exported["configuration"]["deferTools"], True)
        self.assertIs(exported["configuration"]["toolNamespaces"], True)
        self.assertIs(exported["configuration"]["latencyNoPromptCache"], True)
        self.assertIs(exported["configuration"]["noPromptCacheDevelopmentGuard"], True)
        self.assertIs(exported["configuration"]["noPromptCacheIsolatedGuard"], True)
        self.assertNotIn("PRIVATE", json.dumps(exported))
        self.assertEqual(profile.public_source({}), {"configurationAtCollectionRecorded": False})

    def test_private_identity_join_and_filtered_file_guard(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            public = {"conditions": {"isolated": True, "syntheticData": True, "origin": "hands_free",
                "voiceSegments": True, "speechRequests": 0}, "samples": [self.sample]}
            (root / "fixture-chat-segments.json").write_text(json.dumps(public))
            (root / "fixture-chat-segments.private.json").write_text(json.dumps([
                {"sample": "sample-001", "request": {"turnId": self.turn, "content": "PRIVATE"}}]))
            _, samples, turns = profile.load_inputs(root, "fixture")
            self.assertEqual(turns[self.turn], "sample-001")
            self.assertEqual(samples["sample-001"]["case"], "math")
            path = root / "filtered.private.log"
            path.write_text(profile.canonical_line(self.row("chat.enter", 0)))
            self.assertEqual(len(profile.read_profiles(path, turns)[self.turn]), 1)
            path.write_text("Unrelated PRIVATE provider payload\n")
            with self.assertRaises(ValueError):
                profile.read_profiles(path, turns)


class CollectionTests(unittest.TestCase):
    def test_only_selected_numeric_lines_are_persisted(self):
        turn, other = str(uuid.uuid4()), str(uuid.uuid4())
        raw = ("PRIVATE startup log with a credential\n"
            f"2026-11-16T19:22:00.123Z info: LatencyProfile {turn} chat.enter: elapsed=0.001ms duration=0.000ms count=\n"
            f"2026-11-16T19:22:00.124Z info: LatencyProfile {other} chat.enter: elapsed=0.001ms duration=0.000ms count=\n")
        class Process:
            stdout = io.StringIO(raw)
            def wait(self):
                return 0
            def kill(self):
                pass
        state = {"project": "aegis_latency_test", "database": "aegis_memory_v060_test"}
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(profile.segments.bench, "dc", return_value=b"a"*64), \
                patch.object(profile.segments.bench, "command", side_effect=[json.dumps({
                    "com.docker.compose.project": state["project"], "com.docker.compose.service": "api"}).encode(),
                    ("sha256:" + "b"*64).encode(), b"AEGIS_CHAT_MODEL=luna\nAEGIS_CHAT_REASONING_EFFORT=low\nAEGIS_MAX_OUTPUT_TOKENS=7000\nAEGIS_OPENAI_SERVICE_TIER=auto\nAEGIS_LATENCY_PROFILE=1\nAEGIS_OPENAI_HTTP2=true\nAEGIS_OPENAI_DEFER_TOOLS=true\nAEGIS_OPENAI_TOOL_NAMESPACES=true\nAEGIS_LATENCY_NO_PROMPT_CACHE=true\nASPNETCORE_ENVIRONMENT=Development\nAEGIS_MEMORY_EVAL_ISOLATED=true\n"]), \
                patch.object(profile.subprocess, "Popen", return_value=Process()) as popen:
            path = Path(directory) / "profile.private.log"
            self.assertEqual(profile.collect(state, {turn: "sample-001"}, path), 1)
            saved = path.read_text()
            self.assertNotIn("PRIVATE", saved)
            self.assertNotIn(other, saved)
            self.assertIn(turn, saved)
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            source = json.loads(path.with_suffix(".source.json").read_text())
            self.assertEqual(source["configuration"]["model"], "luna")
            self.assertEqual(source["configuration"]["maxOutputTokens"], 7000)
            self.assertEqual(source["configuration"]["requestedServiceTier"], "auto")
            self.assertIs(source["configuration"]["preferHttp2"], True)
            self.assertIs(source["configuration"]["deferTools"], True)
            self.assertIs(source["configuration"]["toolNamespaces"], True)
            self.assertIs(source["configuration"]["latencyNoPromptCache"], True)
            self.assertIs(source["configuration"]["noPromptCacheDevelopmentGuard"], True)
            self.assertIs(source["configuration"]["noPromptCacheIsolatedGuard"], True)
            self.assertEqual(popen.call_args.args[0][:3], ["docker", "logs", "--timestamps"])


if __name__ == "__main__":
    unittest.main()
