#!/usr/bin/env python3
"""Local-only replay of pre-selected held-out positives and media. The archive
must contain index.json with name, decisionSample, pcmSha256 for each WAV.
No audio, private filenames, event offsets or per-clip scores enter the report.
"""
import argparse
import hashlib
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import tempfile
import wave
import zipfile
import numpy as np

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("aegis_external_media_bench", HERE / "benchmark-external-media.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)
spec = importlib.util.spec_from_file_location("aegis_open_verifier", HERE.parent / "wakeword-poc/openwakeword_verifier_reference.py")
open_verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(open_verifier)

EXPECTED = {
    "aegis.int8.tflite": "1efa802e2765a6ba276dbeaf0b171901c421570236c31332cf5d6ecc63dc5291",
    "melspectrogram.onnx": "ba2b0e0f8b7b875369a2c89cb13360ff53bac436f2895cced9f479fa65eb176f",
    "embedding_model.onnx": "70d164290c1d095d1d4ee149bc5e00543250a7316b59f31d056cff7bd3075c1f",
    "aegis.onnx": "f8972e52ef82db7735132ff54a725e69c0462ebda2bff14446a612633aa46c8b",
    "initial_features.f32le": "b4ee1271c05d64fed3032f8ddac4d9731c06e03e142052cf231858657e86df16",
}
THRESHOLD = 0.0692
MS = 16
START = 2 * bench.RATE
SNR_VALUES = (0, 6, 12)


def digest(value):
    return hashlib.sha256(value).hexdigest()


def load_clips(path):
    with zipfile.ZipFile(path) as archive:
        entries = json.loads(archive.read("index.json"))
        clips = []
        for row in entries:
            with wave.open(io.BytesIO(archive.read(row["name"]))) as w:
                assert (w.getnchannels(), w.getframerate(), w.getsampwidth()) == (1, bench.RATE, 2)
                pcm = w.readframes(w.getnframes())
            assert digest(pcm) == row["pcmSha256"]
            samples = np.frombuffer(pcm, dtype="<i2").copy()
            assert 0 < row["decisionSample"] <= len(samples)
            clips.append((samples.astype(float), row["decisionSample"]))
    return clips


def cascade(detector, verifier, samples):
    samples = bench.pcm16(samples)
    candidates = detector.run(samples)["eventSamples"]
    accepted = []
    for event in candidates:
        start = max(0, event - 40000)
        end = min(len(samples), event + 2560)
        if verifier.score(samples[start:end], event - start) >= THRESHOLD:
            accepted.append(event)
    return candidates, accepted


def is_hit(events, marker):
    # Annotation marks an original detector decision, which can differ in its
    # exact timing from the candidate. Count the annotated wake, not media
    # events anywhere in the preceding warm-up.
    return any(marker - 1300*MS <= e <= marker + 850*MS for e in events)


def foreground(samples, marker, echo, snr):
    near = samples.copy()
    # Scale the keyword horizon, with the rest of the labeled clip retained.
    left = max(0, marker - 1000*MS); right = min(len(near), marker + 160*MS)
    nearRms = bench.rms(near[left:right])
    echoRms = bench.rms(echo[START+left:START+right])
    near *= max(80., echoRms) / max(1., nearRms) * 10**(snr / 20)
    mixed = echo.copy()
    mixed[START:START+len(near)] += near
    gain = min(1., 30000 / max(1., np.max(np.abs(mixed))))
    return mixed * gain


def reference_wake_matches(detector, verifier, render, event):
    # Expensive wake inference only on a candidate-associated reference
    # window; compare decisions within a generous ±600ms tolerance. This is
    # an evaluated heuristic, never a production veto merely because media
    # contains the same word as the owner.
    start = max(0, event - 40000)
    end = min(len(render), event + 2560)
    candidates, accepted = cascade(detector, verifier, render[start:end])
    return any(abs(start+decision-event) <= 600*MS for decision in candidates), any(
        abs(start+decision-event) <= 600*MS for decision in accepted)


def counts():
    return {"cases": 0, "primaryHits": 0, "cascadeHits": 0,
            "referencePrimaryWouldVeto": 0, "referenceCascadeWouldVeto": 0}


def evaluate(detector, verifier, state, sample, marker, render=None):
    candidates, accepted = cascade(detector, verifier, sample)
    state["cases"] += 1
    state["primaryHits"] += int(is_hit(candidates, marker))
    hits = [e for e in accepted if is_hit([e], marker)]
    state["cascadeHits"] += int(bool(hits))
    if hits and render is not None:
        matched = [reference_wake_matches(detector, verifier, render, event) for event in hits]
        state["referencePrimaryWouldVeto"] += int(all(x[0] for x in matched))
        state["referenceCascadeWouldVeto"] += int(all(x[1] for x in matched))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--heldout", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--reference-dir", type=Path, required=True)
    parser.add_argument("--batch-argv", type=Path, required=True)
    parser.add_argument("--detector-lib", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--gaps", type=int, choices=[0, 1, 2], default=0)
    args = parser.parse_args()
    os.umask(0o077)
    clips = load_clips(args.heldout)
    assert len(clips) == 19, "The fixed held-out evaluation has exactly 19 recordings"
    with tempfile.TemporaryDirectory(prefix="aegis-private-verifier-") as temp:
        directory = Path(temp)
        with zipfile.ZipFile(args.bundle) as archive:
            for name, expected in EXPECTED.items():
                value = archive.read(name)
                assert digest(value) == expected
                (directory/name).write_bytes(value)
        detector = bench.Detector(args.detector_lib, directory/"aegis.int8.tflite")
        verifier = open_verifier.OpenWakeWordVerifier(directory)
        command = json.loads(args.batch_argv.read_text()) + [str(args.gaps)]
        echo = bench.Echo(command)
        public_pairs = []
        for path in sorted(args.reference_dir.glob("*_lpb.wav"))[:4]:
            mic = bench.read_wave(path.with_name(path.name.replace("_lpb.wav", "_mic.wav")))
            render = bench.read_wave(path)
            start = 2 * bench.RATE
            public_pairs.append((render[start:start+bench.SAMPLES], mic[start:start+bench.SAMPLES]))
        assert len(public_pairs) == 4
        music_pairs = [(bench.instrumental(i), bench.room(bench.instrumental(i), i)) for i in range(4)]
        clean = counts()
        near = {source:{str(snr):{method:counts() for method in ("raw","gatedLinear")}
                        for snr in SNR_VALUES} for source in ("public-speech","synthetic-instrumental")}
        for i, (voice, marker) in enumerate(clips):
            samples = np.zeros(bench.SAMPLES);samples[START:START+len(voice)] = voice
            evaluate(detector, verifier, clean, samples, START+marker)
            for source, pairs in (("public-speech",public_pairs),("synthetic-instrumental",music_pairs)):
                render, mic = pairs[i%len(pairs)]
                for snr in SNR_VALUES:
                    mixed = foreground(voice, marker, mic, snr)
                    gated = echo.run(render,mixed)["gatedLinear"]
                    for method, output in (("raw",mixed),("gatedLinear",gated)):
                        evaluate(detector, verifier, near[source][str(snr)][method], output, START+marker, render)
            print("Held-out positive", i+1, "/", len(clips), flush=True)

        # Wake-like media control: play exactly one real held-out utterance;
        # no near speaker. Then inject a different real near utterance with
        # overlapping wake decision (+12dB over the media echo).
        media = {method:{"mediaOnly":counts(),"coincidentNear":counts()}
                 for method in ("raw","gatedLinear")}
        for i,(mediaVoice, mediaMarker) in enumerate(clips[:8]):
            base = bench.instrumental(i+27)*.35
            rendered = base.copy()
            mediaVoice = mediaVoice * (4000 / max(1, bench.rms(mediaVoice)))
            rendered[START:START+len(mediaVoice)] += mediaVoice
            mic = bench.room(rendered, i+27)
            gated = echo.run(rendered,mic)["gatedLinear"]
            for method,output in (("raw",mic),("gatedLinear",gated)):
                evaluate(detector,verifier,media[method]["mediaOnly"],output,START+mediaMarker,rendered)
            own, ownMarker = clips[(i+7)%len(clips)]
            # Shift the media utterance to coincide with the near decision.
            delta = ownMarker-mediaMarker
            render = bench.shift(rendered,delta)
            farEcho = bench.room(render,i+27)
            mixed = foreground(own,ownMarker,farEcho,12)
            gated = echo.run(render,mixed)["gatedLinear"]
            for method,output in (("raw",mixed),("gatedLinear",gated)):
                evaluate(detector,verifier,media[method]["coincidentNear"],output,START+ownMarker,render)
        result = {"heldoutClipCount":len(clips),"modelHashes":EXPECTED,"verifierThreshold":THRESHOLD,
                  "referenceMissingTailFramesPer200msRead":args.gaps,
                  "cleanBaseline":clean,"nearOverMedia":near,"wakeLikeMediaControls":media,
                  "hostAecProcessCpu":echo.finish(),
                  "note":"Data were evaluated previously for wake-model selection; these local replay results are not new independent wakeword field validation."}
        args.output.write_text(json.dumps(result,indent=2)+"\n")
        print(json.dumps(result,indent=2))


if __name__ == "__main__":
    main()
