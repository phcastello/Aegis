#!/usr/bin/env python3
"""Replay external media through the APK's AEC3 JNI. Input echo-only pairs are
public AEC Challenge WAVs; instrumental music and wake utterances are synthetic.
No audio is written to the report. Compile ExternalMediaBatch.kt with the APK's
NativeHandsFreeEcho.kt and NodeHandsFreeExternalMediaProcessor.kt (the Java
classpath and JNI library setup is the same as BatchEcho.kt, documented in
scripts/barge-in-dtd/README.md). Provide the frozen model and host
microWakeWord C ABI library to measure the primary detector.
"""
import argparse
import ctypes
import io
import hashlib
import json
import math
import resource
import struct
import subprocess
import time
import wave
from pathlib import Path
import numpy as np
from scipy.signal import resample_poly, lfilter

RATE = 16000
SECONDS = 7
SAMPLES = RATE * SECONDS
ONSET = 3 * RATE
BLOCK = 320


def rms(x):
    return float(np.sqrt(np.mean(np.square(x.astype(np.float64)))))


def pcm16(x):
    return np.rint(np.clip(x, -32768, 32767)).astype("<i2")


def read_wave(path):
    with wave.open(str(path), "rb") as w:
        assert w.getsampwidth() == 2
        samples = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2")
        samples = samples.reshape(-1, w.getnchannels()).astype(float).mean(axis=1)
        rate = w.getframerate()
    gcd = math.gcd(rate, RATE)
    return resample_poly(samples, RATE // gcd, rate // gcd)


def shift(x, n):
    if n > 0:
        return np.pad(x, (n, 0))[:len(x)]
    if n < 0:
        return np.pad(x[-n:], (0, -n))
    return x.copy()


class Echo:
    def __init__(self, command):
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        self.before = resource.getrusage(resource.RUSAGE_CHILDREN)
        self.started = time.monotonic()
        self.samples = 0
        self.gated_cpu_ns = 0

    def run(self, render, mic):
        n = len(render)
        assert n == len(mic) and n % BLOCK == 0
        self.samples += n
        self.process.stdin.write(struct.pack(">i", n))
        self.process.stdin.write(pcm16(render).tobytes())
        self.process.stdin.write(pcm16(mic).tobytes())
        self.process.stdin.flush()
        assert struct.unpack(">i", self.process.stdout.read(4))[0] == n
        clean = np.frombuffer(self.process.stdout.read(n * 2), dtype="<i2").copy()
        linear = np.frombuffer(self.process.stdout.read(n * 2), dtype="<i2").copy()
        gated = np.frombuffer(self.process.stdout.read(n * 2), dtype="<i2").copy()
        self.gated_cpu_ns += struct.unpack(">q", self.process.stdout.read(8))[0]
        assert len(clean) == n and len(linear) == n and len(gated) == n
        return {"nonlinear": clean, "linear": linear, "gatedLinear": gated}

    def finish(self):
        self.process.stdin.write(struct.pack(">i", 0)); self.process.stdin.flush()
        assert self.process.wait() == 0
        after = resource.getrusage(resource.RUSAGE_CHILDREN)
        simulated = self.samples / RATE
        cpu = after.ru_utime + after.ru_stime - self.before.ru_utime - self.before.ru_stime
        return {"processedAudioSeconds": simulated, "cpuSeconds": cpu,
                "cpuMillisecondsPerAudioSecond": cpu * 1000 / max(1, simulated),
                "gatedLinearThreadCpuMillisecondsPerAudioSecond": self.gated_cpu_ns / 1e6 / max(1, simulated),
                "wallSecondsIncludingPipe": time.monotonic() - self.started}


class Detector:
    def __init__(self, path, model):
        self.lib = ctypes.CDLL(str(path))
        self.model = model.read_bytes()
        self.lib.aegis_mww_create.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
        self.lib.aegis_mww_create.restype = ctypes.c_void_p
        self.lib.aegis_mww_process.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int16), ctypes.c_size_t]
        self.lib.aegis_mww_process.restype = ctypes.c_int64
        self.lib.aegis_mww_destroy.argtypes = [ctypes.c_void_p]
        self.lib.aegis_mww_max_score.argtypes = [ctypes.c_void_p]
        self.lib.aegis_mww_max_score.restype = ctypes.c_float

    def run(self, samples, onset=0, duration=None):
        handle = self.lib.aegis_mww_create(ctypes.c_char_p(self.model), len(self.model))
        assert handle
        samples = pcm16(samples)
        events = []
        try:
            for at in range(0, len(samples), 160):
                x = np.ascontiguousarray(samples[at:at+160], dtype=np.int16)
                result = self.lib.aegis_mww_process(handle, x.ctypes.data_as(ctypes.POINTER(ctypes.c_int16)), len(x))
                assert result != -2
                if result >= 0: events.append(result)
            maximum = self.lib.aegis_mww_max_score(handle)
        finally:
            self.lib.aegis_mww_destroy(handle)
        return {"events": len(events), "eventSamples": events, "maxScore": float(maximum),
                "hit": duration is not None and any(onset - BLOCK <= e <= onset + duration + RATE for e in events),
                "false": sum(duration is None or not onset - BLOCK <= e <= onset + duration + RATE for e in events)}


def wake_bank(detector):
    # Filter by clean detector acceptance so the experiment measures added media
    # and treatment, rather than ambiguity in synthetic pronunciations.
    voices = ["pt-br+f3", "pt-br+f5", "pt-br+m5", "pt-br+f2", "pt-br+m2"]
    bank = []
    for voice in voices:
        for phrase in ["Aéjis", "Êigis", "Aegis"]:
            for speed in [160, 180, 200]:
                generated = subprocess.check_output(["espeak-ng", "-v", voice, "-s", str(speed), "--stdout", phrase])
                with wave.open(io.BytesIO(generated)) as w:
                    rate = w.getframerate()
                    samples = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(float)
                gcd = math.gcd(rate, RATE)
                samples = resample_poly(samples, RATE // gcd, rate // gcd)
                samples *= 1600 / max(1, rms(samples))
                clean = np.zeros(SAMPLES); clean[ONSET:ONSET+len(samples)] = samples
                if detector.run(clean, ONSET, len(samples))["hit"]:
                    bank.append(samples)
    return bank


def instrumental(seed):
    rng = np.random.default_rng(seed)
    t = np.arange(SAMPLES) / RATE
    signal = np.zeros(SAMPLES)
    notes = [60, 64, 67, 69, 65, 67, 62, 59]
    for bar in range(14):
        at = bar * 8000
        local = np.arange(min(8000, SAMPLES-at)) / RATE
        for offset, amp in [(0, 1.0), (4, .55), (7, .34), (-12, .45)]:
            f = 440 * 2 ** ((notes[(bar+seed)%len(notes)] + offset - 69)/12)
            tone = sum((.55**(h-1)) * np.sin(2*np.pi*f*h*local + rng.uniform(0, 2*np.pi))
                       for h in range(1, 7) if f*h < 7500)
            env = np.minimum(local/.015, 1) * np.exp(-4*local)
            signal[at:at+len(local)] += amp * tone * env
        # Kick and snare-like broad-band percussive energy.
        n = len(local)
        signal[at:at+n] += .4*np.sin(2*np.pi*(65*local+15*(1-np.exp(-local*15))))*np.exp(-28*local)
        signal[at:at+n] += .18*rng.normal(size=n)*np.exp(-40*local)
    return signal * (4500 / rms(signal))


def room(render, seed):
    rng = np.random.default_rng(seed)
    taps = np.zeros(1801)
    base = [int(RATE*.025), int(RATE*.043), int(RATE*.086)]
    for lag, gain in zip(base, [.52, .22, -.12]):
        taps[lag] = gain
    for i in range(45):
        lag = int(rng.integers(base[0]+1, len(taps)))
        taps[lag] += rng.uniform(-.035, .035)*np.exp(-lag/1000)
    return lfilter(taps, [1], render) + rng.normal(0, 12, len(render))


def report_metric(output, near, delay):
    aligned = output[ONSET+delay:ONSET+delay+len(near)].astype(float)
    near = near[:len(aligned)]
    projection = float(np.dot(aligned, near)/max(1, np.dot(near, near)))
    error = aligned - projection*near
    return {
        "gainDb": 20*math.log10(max(1e-6, abs(projection))),
        "projectionSnrDb": 20*math.log10(max(1e-6, rms(projection*near))/max(1e-6, rms(error))),
    }


def median(rows, key):
    return round(float(np.median([x[key] for x in rows])), 3)


def summarize(rows):
    summaries = []
    for source in sorted({r["source"] for r in rows}):
        for offset in sorted({r["offsetMs"] for r in rows}):
            for method in ["raw", "linear", "nonlinear", "gatedLinear"]:
                selected = [r for r in rows if r["source"] == source and r["offsetMs"] == offset and r["method"] == method]
                if not selected: continue
                positives = [r for r in selected if r["positive"]]
                negatives = [r for r in selected if not r["positive"]]
                item = {"source": source, "referenceOffsetMs": offset, "method": method,
                        "echoOnlyCases": len(negatives), "speechCases": len(positives),
                        "echoReductionMedianDb": median(negatives, "echoReductionDb"),
                        "falseWakeEvents": sum(r["detector"]["false"] for r in negatives),
                        "nearBySnrDb": {}}
                for snr in sorted({r["snrDb"] for r in positives}):
                    measured = [r for r in positives if r["snrDb"] == snr]
                    item["nearBySnrDb"][str(snr)] = {
                        "cases": len(measured), "wakeHits": sum(r["detector"]["hit"] for r in measured),
                        "nearGainMedianDb": median(measured, "gainDb"),
                        "nearProjectionSnrMedianDb": median(measured, "projectionSnrDb")}
                summaries.append(item)
    return summaries


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--reference-dir", type=Path, required=True)
    p.add_argument("--batch-argv", type=Path, required=True)
    p.add_argument("--detector-lib", type=Path, required=True)
    p.add_argument("--model", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--sources", type=int, default=6)
    p.add_argument("--offsets", nargs="+", type=int, default=[-80, -20, 0, 20, 80])
    p.add_argument("--snr", nargs="+", type=int, default=[0, 6, 12])
    args = p.parse_args()
    detector = Detector(args.detector_lib, args.model)
    voices = wake_bank(detector)
    assert len(voices) >= 3, "synthetic wake examples not accepted in clean input"
    print("Clean-accepted synthetic wake samples:", len(voices), flush=True)
    echo = Echo(json.loads(args.batch_argv.read_text()))
    rows = []
    sources = []
    for ref in sorted(args.reference_dir.glob("*_lpb.wav"))[:args.sources]:
        path = ref.with_name(ref.name.replace("_lpb.wav", "_mic.wav"))
        render, mic = read_wave(ref), read_wave(path)
        # Preserve recorded time offset and acoustic path. Assess a segment from
        # the same recording, with 3s for AEC3 convergence before the wake word.
        start = 2*RATE
        r, m = render[start:start+SAMPLES], mic[start:start+SAMPLES]
        if len(r) != SAMPLES or len(m) != SAMPLES or rms(m) < 30: continue
        sources.append(("public-speech", r, m))
    for i in range(args.sources):
        r = instrumental(i); sources.append(("synthetic-instrumental", r, room(r, i)))
    # Negative controls where media itself speaks the wake word. Preserve a
    # warm-up of instrumental audio, then insert clean-accepted synthetic vocal.
    for i, voice in enumerate(voices[:4]):
        r = instrumental(i+31)
        speech = voice * (5000 / rms(voice))
        r[ONSET:ONSET+len(speech)] *= .08
        r[ONSET:ONSET+len(speech)] += speech
        sources.append(("synthetic-wake-media", r, room(r, i+31)))
    # A silent (but available) reference must not treat the microphone.
    silence_control = echo.run(np.zeros(SAMPLES), sources[0][2])
    assert np.array_equal(silence_control["gatedLinear"], pcm16(sources[0][2]))
    for case, (source, render, echo_only) in enumerate(sources):
        for offset in args.offsets:
            ref = shift(render, offset*16)
            clean = echo.run(ref, echo_only)
            clean["raw"] = pcm16(echo_only)
            for method, output in clean.items():
                window = slice(2*RATE, SAMPLES)
                rows.append({"source":source,"offsetMs":offset,"method":method,
                    "positive":False,"detector":detector.run(output),
                    "echoReductionDb":20*math.log10(max(1,rms(echo_only[window]))/max(1,rms(output[window])))})
            if source == "synthetic-wake-media": continue
            for snr in args.snr:
                voice = voices[(case+offset//20)%len(voices)].copy()
                voice *= max(80, rms(echo_only[ONSET:ONSET+len(voice)]))/max(1,rms(voice))*10**(snr/20)
                near = np.zeros(SAMPLES); near[ONSET:ONSET+len(voice)] = voice
                mixed = echo_only + near
                # Scale both near and far together rather than clip a louder
                # independent utterance at high SNR.
                gain = min(1., 30000/max(1., np.max(np.abs(mixed))))
                mixed *= gain; voice *= gain
                processed = echo.run(ref, mixed)
                processed["raw"] = pcm16(mixed)
                for method, output in processed.items():
                    delay = {"raw":0,"linear":64,"nonlinear":128,"gatedLinear":64}[method]
                    # The gated processor falls back to raw, with 0-sample
                    # delay, on unqualified paths. Use the better correlation
                    # of the only two possible fixed branch latencies for
                    # approximate retention metrics, not wake event scoring.
                    metric = report_metric(output, voice, delay)
                    if method == "gatedLinear":
                        raw_timing = report_metric(output, voice, 0)
                        if raw_timing["projectionSnrDb"] > metric["projectionSnrDb"]:
                            metric = raw_timing
                    rows.append({"source":source,"offsetMs":offset,"method":method,"positive":True,"snrDb":snr,
                                 "detector":detector.run(output, ONSET+delay, len(voice)),**metric})
        print("Completed source", case+1, "/",len(sources), flush=True)
    cpu = echo.finish()
    result = {"cleanAcceptedSyntheticWakeCount":len(voices),
              "independentPublicEchoSources":sum(s[0]=="public-speech" for s in sources),
              "syntheticMusicSources":sum(s[0]=="synthetic-instrumental" for s in sources),
              "modelSha256":hashlib.sha256(args.model.read_bytes()).hexdigest(),
              "nominalNearToEchoRmsDb":args.snr,
              "quietReferenceByteIdentical":True,
              "hostAecProcessCpu":cpu,
              "methods":summarize(rows)}
    args.output.write_text(json.dumps(result,indent=2)+"\n")
    print(json.dumps(result,indent=2))


if __name__ == "__main__":
    main()
