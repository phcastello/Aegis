#!/usr/bin/env python3
"""Offline contract checks; never starts services or sends inference requests."""
from contextlib import redirect_stdout
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

spec = importlib.util.spec_from_file_location("segments", Path(__file__).with_name("benchmark-chat-segments.py"))
segments = importlib.util.module_from_spec(spec)
spec.loader.exec_module(segments)


class Response:
    def __init__(self, data=None, events=None):
        self.data = data
        self.events = events or []
    def __enter__(self):
        return self
    def __exit__(self, *args):
        return False
    def __iter__(self):
        return iter([json.dumps(event).encode() + b"\n" for event in self.events])
    def read(self):
        return json.dumps(self.data).encode()


class StreamTests(unittest.TestCase):
    def setUp(self):
        self.turn, self.conversation, self.assistant = [str(uuid.uuid4()) for _ in range(3)]
    def unit(self, sequence=0, **changes):
        result = {"type": "final_segment", "turnId": self.turn,
            "conversationId": self.conversation, "assistantMessageId": self.assistant,
            "segmentId": str(uuid.uuid4()), "sequence": sequence,
            "content": "Resposta sintética que não deve aparecer no relatório público."}
        result.update(changes)
        return result
    def done(self):
        return {"type": "done", "turnId": self.turn,
            "conversationId": self.conversation, "assistantMessageId": self.assistant,
            "voiceContinuation": "close"}
    def read(self, events):
        ticks = iter(range(1, 100))
        return segments.read_stream(Response(events=events), self.turn, 0,
            clock=lambda: next(ticks)/1000)
    def test_timing_counts_only_final_units_and_consumes_eof(self):
        events = [{"type": "conversation", "turnId": self.turn, "conversationId": self.conversation},
            {"type": "token", "turnId": self.turn, "content": "Not finalized"},
            self.unit(), self.unit(1), self.done()]
        public, private = self.read(events)
        self.assertEqual(public["firstFinalSegmentMs"], 3)
        self.assertEqual(public["generationDoneMs"], 5)
        self.assertEqual(public["transportEndMs"], 6)
        self.assertEqual(public["segments"], 2)
        self.assertNotIn("Resposta", json.dumps(public))
        self.assertEqual(private["events"], events)
    def test_incomplete_or_late_error_stream_is_rejected(self):
        for events in ([self.unit()], [self.unit(), self.done(),
                {"type": "error", "turnId": self.turn, "message": "private provider error"}]):
            with self.subTest(events=len(events)), self.assertRaises(segments.ProtocolError):
                self.read(events)
    def test_wrong_turn_sequence_or_conversation_is_rejected(self):
        for changed in [self.unit(turnId=str(uuid.uuid4())), self.unit(2),
                self.unit(sequence=True)]:
            with self.subTest(sequence=changed["sequence"]), self.assertRaises(segments.ProtocolError):
                self.read([changed, self.done()])
        with self.assertRaises(segments.ProtocolError):
            self.read([self.unit(), self.unit(1, conversationId=str(uuid.uuid4())), self.done()])
    def test_duplicate_segment_and_message_change_are_rejected(self):
        first = self.unit()
        for changed in [self.unit(1, segmentId=first["segmentId"]),
                self.unit(1, assistantMessageId=str(uuid.uuid4()))]:
            with self.assertRaises(segments.ProtocolError):
                self.read([first, changed, self.done()])


class HarnessTests(unittest.TestCase):
    def state(self, directory=None):
        result = {"project": "aegis_latency_0123456789", "database": "aegis_memory_v060_0123456789",
            "base": "http://127.0.0.1:12345", "sourceCommit": "test", "sourceImage": "test-image"}
        if directory is not None:
            result["compose"] = str(directory / "compose.private.json")
        return result
    def configured_state(self, directory, tier="default"):
        state = self.state(directory)
        environment = {"AEGIS_MEMORY_EVAL_ISOLATED": "true",
            "AEGIS_MEMORY_QDRANT_COLLECTION": state["database"]}
        if tier is not None:
            environment["AEGIS_OPENAI_SERVICE_TIER"] = tier
        (directory / "compose.private.json").write_text(json.dumps({"services": {"api": {"environment": environment}}}))
        return state
    def test_nonisolated_database_is_refused_before_chat(self):
        with patch.object(segments.bench, "http", return_value=Response(data={
                "isolated": True, "database": "owner-db", "qdrantCollection": "owner-db"})) as http:
            with self.assertRaises(RuntimeError):
                segments.verify_isolated(self.state())
            self.assertEqual(http.call_count, 1)
        with patch.object(segments.bench, "http") as http:
            with self.assertRaises(RuntimeError):
                segments.verify_isolated(dict(self.state(), base="http://10.1.1.22"))
            http.assert_not_called()
    def test_premium_auto_or_absent_tiers_fail_before_any_http(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for tier in ("priority", "fast", "ultrafast", "auto", "flex", "scale", "", None):
                with self.subTest(tier=tier), patch.object(segments.bench, "http") as http:
                    state = self.configured_state(root, tier)
                    with patch.object(segments.bench, "command") as sql:
                        for cases in (["math"], ["create_reminder"]):
                            with self.assertRaises(RuntimeError):
                                segments.run(state, root, "blocked", 1, cases)
                        sql.assert_not_called()
                    http.assert_not_called()
    def test_private_path_is_checked_before_opening_or_http(self):
        with tempfile.TemporaryDirectory() as temp, tempfile.TemporaryDirectory() as outside:
            root = Path(temp)
            state = self.state()
            state["compose"] = str(Path(outside) / "compose.private.json")
            Path(state["compose"]).write_text("PRIVATE FILE MUST NOT BE OPENED")
            with patch.object(Path, "read_text", side_effect=AssertionError("private file was read")), \
                    patch.object(segments.bench, "http") as http:
                with self.assertRaises(RuntimeError):
                    segments.verify_default_configuration(state, root)
                http.assert_not_called()
    def test_prepare_forces_default_without_exposing_or_mutating_source_credentials(self):
        live = {"Image": "sha256:" + "a"*64, "Config": {"Env": ["OPENAI_API_KEY=PRIVATE-TEST-SECRET",
            "AEGIS_OPENAI_SERVICE_TIER=auto", "AEGIS_CHAT_MODEL=luna"],
            "Labels": {"org.opencontainers.image.revision": "b"*40}}}
        class Socket:
            def __enter__(self): return self
            def __exit__(self, *args): return False
            def bind(self, target): pass
            def getsockname(self): return ("127.0.0.1", 12345)
        with tempfile.TemporaryDirectory() as temp, \
                patch.object(segments.bench, "command", return_value=json.dumps([live]).encode()) as command, \
                patch.object(segments.bench.socket, "socket", return_value=Socket()):
            root = Path(temp) / "new"
            output = io.StringIO()
            with redirect_stdout(output):
                segments.bench.prepare(root)
            state = json.loads((root / "state.json").read_text())
            config = json.loads((root / "compose.private.json").read_text())
            self.assertEqual(state["requestedSourceTier"], "auto")
            self.assertEqual(state["experimentServiceTier"], "default")
            self.assertIs(state["forcedDefaultServiceTier"], True)
            self.assertEqual(config["services"]["api"]["environment"]["AEGIS_OPENAI_SERVICE_TIER"], "default")
            self.assertNotIn("PRIVATE-TEST-SECRET", output.getvalue() + json.dumps(state))
            self.assertIn("AEGIS_OPENAI_SERVICE_TIER=auto", live["Config"]["Env"])
            self.assertEqual(command.call_count, 1)
            segments.verify_default_configuration(state, root)

    def test_chat_only_continuation_privacy_and_cleanup(self):
        calls = []
        conversation = str(uuid.uuid4())
        def fake_http(base, path, body=None, method=None, headers=None):
            calls.append((path, body, method))
            if path == "/api/memory/diagnostics/environment":
                return Response(data={"isolated": True, "database": "aegis_memory_v060_0123456789",
                    "qdrantCollection": "aegis_memory_v060_0123456789", "memoryCount": 0, "extractionJobCount": 0})
            if method == "DELETE" and path.startswith("/api/chat/turns/"):
                return Response(data={"speechCancellationRequested": False})
            self.assertEqual(path, "/api/chat/messages/stream")
            self.assertIs(body["voiceSegments"], True)
            self.assertEqual(body["origin"], "hands_free")
            self.assertEqual(headers["X-Aegis-Turn-Id"], body["turnId"])
            message = str(uuid.uuid4())
            return Response(events=[
                {"type": "conversation", "turnId": body["turnId"], "conversationId": conversation},
                {"type": "final_segment", "turnId": body["turnId"],
                    "conversationId": conversation, "assistantMessageId": message,
                    "segmentId": str(uuid.uuid4()), "sequence": 0, "content": "Resposta privada sintética."},
                {"type": "done", "turnId": body["turnId"], "conversationId": conversation,
                    "assistantMessageId": message, "voiceContinuation": "continue"}])
        with tempfile.TemporaryDirectory() as temp, patch.object(segments.bench, "http", side_effect=fake_http):
            output = io.StringIO()
            with redirect_stdout(output):
                public, private = segments.run(self.configured_state(Path(temp)), Path(temp), "offline", 1,
                    ["continuation", "continuation_reply"])
            requests = [body for path, body, method in calls if path == "/api/chat/messages/stream"]
            self.assertEqual(len(requests), 2)
            self.assertNotIn("conversationId", requests[0])
            self.assertEqual(requests[1]["conversationId"], conversation)
            self.assertEqual(requests[0]["voiceSessionId"], requests[1]["voiceSessionId"])
            self.assertEqual(sum(method == "DELETE" for _, _, method in calls), 2)
            self.assertEqual(public["conditions"]["speechRequests"], 0)
            safe = json.dumps(public) + output.getvalue()
            self.assertNotIn("Resposta privada", safe)
            for request in requests:
                self.assertNotIn(request["turnId"], safe)
                self.assertNotIn(request["content"], safe)
            self.assertEqual(private[0]["turnId"], requests[0]["turnId"])
            private_file = Path(temp) / "offline-chat-segments.private.json"
            self.assertEqual(private_file.stat().st_mode & 0o777, 0o600)
            self.assertTrue(all("/voice/" not in path for path, _, _ in calls))


    def test_optional_reminder_date_uses_sao_paulo_day_and_exact_utc(self):
        self.assertEqual(len(segments.CORPUS), 8)
        self.assertNotIn("create_reminder", [case for case, _ in segments.CORPUS])
        question, due = segments.reminder_request(segments.datetime(2026, 11, 17, 1, tzinfo=segments.timezone.utc))
        self.assertIn("2026-11-18", question)
        self.assertEqual(due, "2026-11-18T13:00:00+00:00")

    def test_reminder_verifier_rejects_wrong_due_missing_or_duplicate_creation(self):
        identifier = str(uuid.uuid4())
        reminder = {"reminderId": identifier, "text": "PRIVATE TASK", "dueAt": "2026-11-18T13:00:00Z",
            "timeZoneId": "America/Sao_Paulo", "status": "Scheduled"}
        due = "2026-11-18T13:00:00+00:00"
        result = segments.verify_created_reminder([], [reminder], due)
        self.assertTrue(result["exactlyOneCreated"])
        self.assertTrue(all(type(value) in (int, bool) for value in result.values()))
        self.assertNotIn(identifier, json.dumps(result))
        for after in ([], [dict(reminder, dueAt="2026-11-18T10:00:00Z")],
                [reminder, dict(reminder, reminderId=str(uuid.uuid4()))]):
            with self.assertRaises(segments.ProtocolError):
                segments.verify_created_reminder([], after, due)

    def test_optional_reminder_uses_authenticated_reads_and_private_effect_details(self):
        calls, secrets = [], []
        identifier = str(uuid.uuid4())
        due = "2026-11-18T13:00:00+00:00"
        reminders = []
        conversation, message = str(uuid.uuid4()), str(uuid.uuid4())
        def fake_http(base, path, body=None, method=None, headers=None):
            calls.append((path, method))
            if path == "/api/memory/diagnostics/environment":
                return Response(data={"isolated": True, "database": "aegis_memory_v060_0123456789",
                    "qdrantCollection": "aegis_memory_v060_0123456789"})
            if path == "/api/nodes/reminders":
                self.assertTrue(headers["Authorization"].startswith("AegisNode aegis-node-v1."))
                secrets.append(headers["Authorization"])
                return Response(data={"reminders": list(reminders), "hasMore": False})
            if method == "DELETE":
                return Response(data={})
            self.assertEqual(path, "/api/chat/messages/stream")
            reminders.append({"reminderId": identifier, "text": "PRIVATE TASK", "dueAt": due,
                "timeZoneId": "America/Sao_Paulo", "status": "Scheduled"})
            return Response(events=[
                {"type": "final_segment", "turnId": body["turnId"], "conversationId": conversation,
                    "assistantMessageId": message, "segmentId": str(uuid.uuid4()), "sequence": 0, "content": "Lembrete criado."},
                {"type": "done", "turnId": body["turnId"], "conversationId": conversation,
                    "assistantMessageId": message, "voiceContinuation": "close"}])
        with tempfile.TemporaryDirectory() as temp, patch.object(segments.bench, "http", side_effect=fake_http), \
                patch.object(segments.bench, "command", return_value=b"") as sql, \
                patch.object(segments, "reminder_request", return_value=("Crie o lembrete sintético.", due)):
            root = Path(temp)
            output = io.StringIO()
            with redirect_stdout(output):
                public, private = segments.run(self.configured_state(root), root, "reminder", 1, ["create_reminder"])
            sample = public["samples"][0]
            self.assertTrue(sample["reminderVerification"]["dueAtMatches"])
            self.assertIn("firstFinalSegmentMs", sample)
            self.assertIn("reminderVerificationMs", sample)
            self.assertEqual(sum(path == "/api/nodes/reminders" for path, _ in calls), 3)
            self.assertEqual(sql.call_count, 2)
            self.assertIn("INSERT INTO", sql.call_args_list[0].kwargs["input"].decode())
            self.assertIn("DELETE FROM", sql.call_args_list[1].kwargs["input"].decode())
            self.assertNotIn(identifier, json.dumps(public) + output.getvalue())
            self.assertIn(identifier, json.dumps(private))
            for secret in secrets:
                self.assertNotIn(secret, json.dumps(private) + json.dumps(public))
            self.assertTrue(all("/voice/" not in path for path, _ in calls))

    def test_reminder_read_failure_stops_before_any_paid_chat_and_removes_fixture(self):
        paths = []
        def fake_http(base, path, *args, **kwargs):
            paths.append(path)
            if path == "/api/memory/diagnostics/environment":
                return Response(data={"isolated": True, "database": "aegis_memory_v060_0123456789",
                    "qdrantCollection": "aegis_memory_v060_0123456789"})
            if path == "/api/nodes/reminders":
                raise RuntimeError("readiness failed")
            self.fail("a paid chat was attempted")
        with tempfile.TemporaryDirectory() as temp, patch.object(segments.bench, "http", side_effect=fake_http), \
                patch.object(segments.bench, "command", return_value=b"") as sql:
            root = Path(temp)
            with self.assertRaises(RuntimeError):
                segments.run(self.configured_state(root), root, "readiness", 1, ["create_reminder"])
            self.assertEqual(sql.call_count, 2)
            self.assertNotIn("/api/chat/messages/stream", paths)


if __name__ == "__main__":
    unittest.main()
