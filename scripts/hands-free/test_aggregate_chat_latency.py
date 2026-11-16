#!/usr/bin/env python3
"""Offline report aggregation checks; no private source, HTTP or Docker access."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("aggregate", Path(__file__).with_name("aggregate-chat-latency.py"))
aggregate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(aggregate)


class AggregateTests(unittest.TestCase):
    def fixture(self, directory, label="current-recheck"):
        state = {"project": "aegis_latency_test", "database": "aegis_memory_v060_test"}
        (directory / "state.json").write_text(json.dumps(state))
        samples = [
            {"sample": "sample-001", "case": "math", "firstCallInHarness": True,
                "firstFinalSegmentMs": 2000, "generationDoneMs": 2100, "transportEndMs": 2200,
                "toolStatusStarted": 0, "privateExtra": "PRIVATE TEXT"},
            {"sample": "sample-002", "case": "read_tool", "firstCallInHarness": False,
                "firstFinalSegmentMs": 1000, "generationDoneMs": 1200, "transportEndMs": 1300,
                "toolStatusStarted": 1},
        ]
        public = {"sourceCommit": "a"*40, "image": "sha256:" + "b"*64, "conditions": {
            "isolated": True, "syntheticData": True, "origin": "hands_free", "voiceSegments": True,
            "speechRequests": 0, "database": state["database"]}, "samples": samples}
        (directory / (label+"-chat-segments.json")).write_text(json.dumps(public))
        # It must be impossible for the report to require parsing this private file.
        (directory / (label+"-chat-segments.private.json")).write_text("PRIVATE INVALID JSON")
        analysis = {"source": {"configurationAtCollectionRecorded": True, "image": "sha256:"+"b"*64,
            "configuration": {"model": "luna", "preferHttp2": True, "requestedServiceTier": "auto",
                "profilingEnabled": True, "privateExtra": "PRIVATE CONFIG"}}, "samples": [{
                "sample": "sample-001", "case": "math", "firstCallInHarness": True, "profileStatus": "complete_through_first_flush",
                "api": {"beforeFirstModelMs": 25, "prepareMs": 10, "firstModelSegmentToApiSegmentMs": 5,
                    "firstApiSegmentToFlushMs": 2, "outsideProfileResidualMs": 3, "historyMessages": 0},
                "contextStages": [{"stage": "context.prompt", "durationMs": 7}],
                "rounds": [{"serviceTier": "default", "httpProtocol": {"version": "1.1"},
                    "tokens": {"input": 100, "cachedInput": 90, "reasoning": 0},
                    "intervalsMs": {"requestToResponseHeaders": 500, "requestToFirstFinalSegment": 900},
                    "milestonesFromRequestStartMs": {"firstArgumentDelta": 800},
                    "toolsAfterRound": []}],
            }]}
        (directory / (label+"-analysis.json")).write_text(json.dumps(analysis))
        return public

    def test_missing_measurements_are_null_and_observed_zero_is_retained(self):
        self.assertEqual(aggregate.stats([]), {"n": 0, "avg": None, "median": None,
            "range": {"min": None, "max": None}})
        self.assertIn("http2-control", aggregate.DEFAULT_LABELS)
        self.assertIn("http1-control", aggregate.DEFAULT_LABELS)
        self.assertNotIn("http2-profile", aggregate.DEFAULT_LABELS)
        self.assertEqual(aggregate.token_stats([None, 0])["observedTotal"], 0)
        self.assertIsNone(aggregate.token_stats([None])["observedTotal"])

    def test_report_is_sanitized_and_retains_future_labels_and_protocol_fallback(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.fixture(root)
            with patch.object(aggregate.profile.segments.bench, "http", side_effect=AssertionError("HTTP forbidden")):
                report = aggregate.build_report(root, ["current-recheck", "http2-profile", "default-control"])
            self.assertEqual(report["totals"]["syntheticChatRequests"], 2)
            self.assertEqual(report["totals"]["samplesWithDetailedProfiles"], 1)
            self.assertEqual(report["totals"]["datasetsNotAvailable"], 2)
            current = report["datasets"][0]
            self.assertEqual(current["firstFinalSegmentMs"]["all"]["avg"], 1500)
            self.assertEqual(current["firstFinalSegmentMs"]["subsequent"]["median"], 1000)
            self.assertEqual(current["firstFinalSegmentMs"]["subsequentWithoutToolEvents"]["n"], 0)
            self.assertIs(current["configuration"]["configuration"]["preferHttp2"], True)
            self.assertEqual(current["profile"]["provider"]["httpProtocolsObserved"], {"1.1": 1})
            self.assertEqual(current["profile"]["tokensPerRound"]["reasoning"]["observedTotal"], 0)
            self.assertEqual(current["profileByCohort"]["firstInHarness"]["samples"], 1)
            self.assertEqual(report["profiledByCohort"]["subsequent"]["samples"], 0)
            self.assertIsNone(current["profile"]["tokensPerRound"]["output"]["avg"])
            self.assertFalse(report["historicalPhoneAggregate"]["partialServerAttributionAvailable"])
            self.assertEqual(report["phoneValidation"]["status"], "pending")
            self.assertTrue(report["userConstraints"]["normalOpenAIRequestsAllowed"])
            self.assertTrue(report["userConstraints"]["premiumFastPriorityProhibited"])
            self.assertFalse(report["userConstraints"]["premiumFastPriorityAllowed"])
            self.assertFalse(report["qualitativeReview"]["allSamplesApproved"])
            self.assertEqual(report["qualitativeReview"]["reviewedSamples"], 2)

            self.assertNotIn("PRIVATE", json.dumps(report))

    def test_profile_stages_keep_authorization_and_flush_separate(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.fixture(root)
            report = aggregate.build_report(root, ["current-recheck"])
            timing = report["profiledAggregate"]
            self.assertEqual(timing["api"]["firstModelSegmentToAuthorizedPersistedSegmentMs"]["avg"], 5)
            self.assertEqual(timing["api"]["authorizedSegmentToFlushMs"]["avg"], 2)
            self.assertEqual(timing["context"]["prepareMs"]["avg"], 10)
            self.assertEqual(timing["context"]["subStagesMs"]["context.prompt"]["avg"], 7)

    def test_historical_partial_recovery_does_not_export_ids_or_claim_full_attribution(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            rows = [{"selected_turn_count": "4", "successful_audit_matches": "1", "first_segment_sequence": "0",
                "voice_segments_complete": "t", "clock_order_consistent": "t",
                "user_to_runtime_context_ms": 29.59175, "runtime_to_first_segment_created_ms": 3994.43175,
                "user_to_first_segment_created_ms": 4024.0235, "full_model_tool_loop_ms": 4218.25,
                "model_calls": 1 if index < 2 else 2, "tool_calls": 0 if index < 2 else 1,
                "session_id": "PRIVATE ID", "user_message_created_at": "PRIVATE TIMESTAMP"} for index in range(4)]
            (root / "historical-attribution.json").write_text(json.dumps({"rows": rows,
                "sourceApiCommit": "a"*40, "phoneChatToFirstFinalSegmentAggregateMeanMs": 4286,
                "matching": "PRIVATE MAPPING"}))
            features = [{"audit_matches": 1, "all_requests_effort_low": True,
                "last_input_tokens": 1000, "last_cached_input_tokens": 0 if index < 2 else 900,
                "last_reasoning_tokens": [122, 50, 0, 0][index], "session_id": "PRIVATE ID"} for index in range(4)]
            (root / "historical-numeric-features.json").write_text(json.dumps(features))
            result = aggregate.historical_attribution(root)
            self.assertEqual(result["attributionStatus"], "partial_recovered")
            self.assertTrue(result["partialServerAttributionAvailable"])
            self.assertFalse(result["individualFirstFlushAvailable"])
            self.assertEqual(result["remainingUnattributedAggregateMeanMs"], 261.9765)
            self.assertEqual(result["meansMs"]["userMessageCreatedToRuntimeContext"], 29.59175)
            self.assertEqual(result["modelCalls"], [1, 1, 2, 2])
            self.assertTrue(result["numericFeatures"]["allRequestsLowEffortConfirmed"])
            self.assertEqual(result["numericFeatures"]["observations"][0]["lastRoundTokens"]["cachedInput"], 0)
            self.assertNotIn("PRIVATE", json.dumps(result))
    def test_network_control_retains_single_pair_uncertainty_and_no_identifiers(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "network-control.json"
            rows = [{"Sequence": 1, "Policy": "baseline", "Phase": "after135s", "Status": 401,
                "Version": "1.1", "NewConnectionCount": 1, "ReusedConnection": False,
                "DurationsMs": {"dns": [5.942], "tcp": [5.573], "tls": [15.09]}, "Connections": ["PRIVATE ID"]},
                {"Sequence": 2, "Policy": "retained", "Phase": "after135s", "Status": 401,
                "Version": "1.1", "NewConnectionCount": 0, "ReusedConnection": True, "DurationsMs": {}}]
            path.write_text(json.dumps({"InferenceRequests": 0, "AuthenticationUsed": False,
                "ExpectedStatus": 401, "Samples": rows, "privateExtra": "PRIVATE TOKEN"}))
            result = aggregate.network_control(path)
            self.assertEqual(result["after135s"]["observedPairs"], 1)
            self.assertEqual(result["after135s"]["baselineReportedSetupMs"], 26.605)
            self.assertIs(result["after135s"]["retainedConnectionReused"], True)
            self.assertFalse(result["quantitativePerformanceGainEstablished"])
            self.assertNotIn("PRIVATE", json.dumps(result))
    def test_readonly_reports_accept_old_priority_artifacts_without_cost_guard(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.fixture(root)
            path = root / "current-recheck-analysis.json"
            data = json.loads(path.read_text())
            data["source"]["configuration"]["requestedServiceTier"] = "priority"
            path.write_text(json.dumps(data))
            with patch.object(aggregate.profile.segments, "verify_default_configuration",
                    side_effect=AssertionError("inference guard must not run during offline analysis")):
                result = aggregate.build_report(root, ["current-recheck"])
            self.assertEqual(result["datasets"][0]["configuration"]["configuration"]["requestedServiceTier"], "priority")

    def test_dataset_identity_and_label_duplicates_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            public = self.fixture(root)
            with self.assertRaises(ValueError):
                aggregate.build_report(root, ["current-recheck", "current-recheck"])
            public["conditions"]["database"] = "owner_database"
            (root / "current-recheck-chat-segments.json").write_text(json.dumps(public))
            with self.assertRaises(ValueError):
                aggregate.build_report(root, ["current-recheck"])


if __name__ == "__main__":
    unittest.main()
