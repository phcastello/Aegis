"""No provider calls: prove that audio backpressure cannot delay the done clock."""
import importlib.util
import json
from pathlib import Path
import threading
import time
import unittest
from unittest.mock import patch
import uuid

spec = importlib.util.spec_from_file_location("pipeline", Path(__file__).with_name("benchmark-pipeline.py"))
pipeline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pipeline)


class PipelineClockTests(unittest.TestCase):
    def test_done_is_observed_before_a_blocked_speech_download_finishes(self):
        drained_text = threading.Event()
        turn, conversation, message = [str(uuid.uuid4()) for _ in range(3)]
        events = [{"type": "final_segment", "turnId": turn, "conversationId": conversation,
                   "assistantMessageId": message, "segmentId": str(uuid.uuid4()),
                   "sequence": i, "content": "Unidade final."} for i in range(3)]
        events.append({"type": "done"})
        started = time.perf_counter()

        class Response:
            headers = {"X-Aegis-Audio-Format": "pcm_s16le", "X-Aegis-Sample-Rate": "24000", "X-Aegis-Channels": "1"}
            def __enter__(self): return self
            def __exit__(self, *args): return False
            def __iter__(self):
                for event in events:
                    yield json.dumps(event).encode()
                drained_text.set()

        def pcm(response, clock):
            if not drained_text.wait(.5):
                raise AssertionError("text reader waited for audio instead of observing done")
            return {"firstSignalMs": (time.perf_counter() - clock) * 1000}

        with patch.object(pipeline.bench, "http", return_value=Response()), patch.object(pipeline.bench, "pcm", side_effect=pcm):
            metrics, observed = pipeline.segmented("http://unused.invalid", {"turnId": turn}, started)
        self.assertEqual(len(observed), 4)
        self.assertEqual(len(metrics["segments"]), 3)
        self.assertLess(metrics["generationDoneMs"], metrics["firstSignalMs"])
        self.assertEqual([s["sequence"] for s in metrics["segments"]], [0, 1, 2])
        self.assertTrue(all(s["queuedAtMs"] <= s["requestAtMs"] for s in metrics["segments"]))


if __name__ == "__main__":
    unittest.main()
