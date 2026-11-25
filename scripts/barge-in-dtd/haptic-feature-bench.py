#!/usr/bin/env python3
"""Offline study of a narrow mechanical-vibration feature veto.

This is NOT the production AGD4/libfvad classifier. It deliberately grants a
positive voice vote to *every* energetic frame, to measure the worst-case
potential cuts and opportunity loss attributable to the candidate alone.
Supply public or previously synthesized audio, never private user recordings.
No audio is saved, uploaded or included in the aggregate report.
"""

import argparse
import itertools
import json
from pathlib import Path
import re
import subprocess

import numpy as np


RATE = 16_000
BLOCK = 320
FFT = 1024
FREQ = np.arange(FFT // 2 + 1) * RATE / FFT
WINDOW = np.hanning(BLOCK * 2)
SHAPES = ("sine", "harmonic", "motor", "pulsed", "resonance", "contact", "ramp")
PARAMETERS = {"low_ratio": 0.85, "high_ratio": 0.08, "peak_ratio": 0.95,
              "periodicity": 0.985, "spectral_stationarity": 0.999}


def read_audio(path):
    """Raw PCM = little-endian int16, 16 kHz mono; WAV converted in memory."""
    if path.suffix.lower() == ".wav":
        data = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-ac", "1",
                               "-ar", str(RATE), "-f", "s16le", "pipe:1"],
                              check=True, stdout=subprocess.PIPE).stdout
        return np.frombuffer(data, dtype="<i2").astype(np.float64)
    return np.fromfile(path, dtype="<i2").astype(np.float64)


def rms(samples):
    return np.sqrt(np.mean(samples ** 2))


def buzz(shape, frequency, seconds, amplitude, seed):
    """Synthetic vibration analogues, not physical phone recordings."""
    rng = np.random.default_rng(seed)
    n = round(seconds * RATE)
    t = np.arange(n) / RATE
    p = 2 * np.pi * frequency * t
    if shape == "sine":
        y = np.sin(p)
    elif shape == "harmonic":
        y = np.sin(p) + .34*np.sin(2*p+.2) + .18*np.sin(3*p+.4) + .07*np.sin(4*p)
    elif shape == "motor":
        y = np.tanh(2.5*np.sin(p)) + .15*np.sin(2*np.pi*(frequency+4)*t) + .025*rng.normal(size=n)
    elif shape == "pulsed":
        y = (np.sin(p)+.4*np.sin(2*p)) * (np.mod(t, .15) < .09)
    elif shape == "resonance":
        y = .74*np.sin(p)+.25*np.sin(2*p+.3)+.20*np.sin(3*p)+.15*np.sin(5*p)+.1*np.sin(7*p)+.08*rng.normal(size=n)
    elif shape == "contact":
        y = .50*np.sin(p)+.30*np.sin(2*p)+.18*np.sin(3*p)+.16*np.sin(4*p)+.24*np.sin(2*np.pi*725*t)*(.5+.5*np.sin(p))+.16*rng.normal(size=n)
    elif shape == "ramp":
        phase = 2*np.pi*(frequency*t+50*t*t)
        y = np.sin(phase)+.2*np.sin(2*phase)+.07*rng.normal(size=n)
    else:
        raise ValueError("unknown synthetic control")
    ramp = min(n//3, int(.014*RATE))
    y[:ramp] *= np.linspace(0, 1, ramp)
    y[-ramp:] *= np.linspace(1, 0, ramp)
    return amplitude * y / max(rms(y), 1e-9)


def evidence(samples):
    """20 ms decisions, 40 ms causal window, frozen feature thresholds."""
    x = np.pad(samples, (BLOCK, (-len(samples)) % BLOCK))
    previous = None
    result = []
    for offset in range(BLOCK, len(x), BLOCK):
        current = x[offset:offset+BLOCK]
        ctx = x[offset-BLOCK:offset+BLOCK]
        ctx = ctx - ctx.mean()
        spectrum = np.abs(np.fft.rfft(ctx * WINDOW, FFT)) ** 2
        total = spectrum[(FREQ >= 60) & (FREQ <= 5000)].sum() + 1e-8
        low = spectrum[(FREQ >= 60) & (FREQ <= 450)].sum() / total
        high = spectrum[(FREQ >= 600) & (FREQ <= 4000)].sum() / total
        peak = int(np.argmax(np.where((FREQ >= 60) & (FREQ <= 400), spectrum, 0)))
        concentration = spectrum[max(0, peak-3):peak+4].sum() / total
        shape = spectrum[(FREQ >= 60) & (FREQ <= 4000)]
        shape /= max(np.linalg.norm(shape), 1e-9)
        stationarity = 0.0 if previous is None else float(np.dot(shape, previous))
        previous = shape
        energetic = rms(current) >= 45.0
        tonal = (energetic and low >= PARAMETERS["low_ratio"] and high <= PARAMETERS["high_ratio"]
                 and (concentration >= PARAMETERS["peak_ratio"] or stationarity >= PARAMETERS["spectral_stationarity"]))
        if tonal:
            # This deliberately narrow signature avoids rejecting ordinary
            # voiced vowels merely because a fundamental pitch exists.
            periodicity = max(np.dot(ctx[lag:], ctx[:-lag]) /
                              max(np.linalg.norm(ctx[lag:]) * np.linalg.norm(ctx[:-lag]), 1e-6)
                              for lag in range(40, 201))
            tonal = periodicity >= PARAMETERS["periodicity"]
        result.append((bool(energetic), bool(tonal)))
    return result


def trigger(frames, apply_veto):
    votes = 0
    for energetic, vetoed in frames:
        active = energetic and not (apply_veto and vetoed)
        votes = ((votes << 1) | int(active)) & 4095
        if active and votes.bit_count() >= 6:
            return True
    return False


def report(cases):
    totals = {}
    for category, frames in cases:
        out = totals.setdefault(category, {"trials": 0, "without_veto": 0, "with_veto": 0})
        out["trials"] += 1
        out["without_veto"] += int(trigger(frames, False))
        out["with_veto"] += int(trigger(frames, True))
    return totals


def noise_cases(frequencies, amplitudes, seed, clip=False):
    cases = []
    for shape, hz, duration, level in itertools.product(SHAPES, frequencies, (.16, .32, .56), amplitudes):
        samples = buzz(shape, hz, duration, level, seed + len(cases))
        if clip:
            samples = np.clip(samples, -32768, 32767)
        cases.append((shape, evidence(samples)))
    return report(cases)


def source_files(directory):
    # A WAV and its already-converted PCM are the same source, not two trials.
    unique = {}
    for path in sorted(directory.iterdir()):
        if path.is_file() and path.suffix.lower() in (".wav", ".pcm", ".s16"):
            if path.stem not in unique or path.suffix.lower() != ".wav":
                unique[path.stem] = path
    return sorted(unique.values())


def synthetic_speech(directory):
    # Preserve the speaker-disjoint Supertonic benchmark split by voice index.
    naming = re.compile(r"supertonic3-[fm](\d+)-(?:oi-diz|command)")
    inputs = [path for path in source_files(directory) if naming.fullmatch(path.stem)]
    if not inputs:
        raise ValueError("No expected public/synthesized Supertonic clip names")
    by_split = {"development": [], "synthetic_comparison": []}
    for path in inputs:
        x = read_audio(path)
        first = next((i for i in range(0, len(x)-BLOCK, BLOCK) if rms(x[i:i+BLOCK]) > 150), None)
        if first is None:
            continue
        clip = x[first:first+BLOCK*23]
        clip /= max(rms(clip), 1)
        split = "development" if int(naming.fullmatch(path.stem).group(1)) <= 3 else "synthetic_comparison"
        cases = by_split[split]
        for amp, dur in itertools.product((100, 350, 1000), (.16, .28, .46)):
            cases.append(("dry", evidence(clip[:int(dur*RATE)]*amp)))
        for ratio, shape in itertools.product((.5, 1, 3, 10), ("harmonic", "contact", "ramp")):
            n = min(len(clip), int(.46*RATE))
            cases.append((f"mixed_{ratio}", evidence(clip[:n]*350 + buzz(shape, 180, n/RATE, 350*ratio, 17))))
    return {key: report(cases) for key, cases in by_split.items()}


def public_speech(directory):
    # Public pairs prepared by scripts/windows-barge-in-bench/prepare.py. Use
    # only the far-end voice; the mic file contains the recorded acoustic echo.
    room_name = re.compile(r"room-(\d+)-render")
    inputs = [p for p in source_files(directory) if room_name.fullmatch(p.stem)
              and int(room_name.fullmatch(p.stem).group(1)) >= 19]
    if not inputs:
        inputs = [p for p in source_files(directory) if not p.stem.endswith("-mic")]
    if not inputs:
        raise ValueError("No public speech fixtures")
    ordinary, loud, after = [], [], []
    used = 0
    for index, path in enumerate(inputs, 19):
        room = room_name.fullmatch(path.stem)
        idx = int(room.group(1)) if room else index
        x = read_audio(path)
        levels = np.array([rms(x[i:i+BLOCK]) for i in range(0, min(len(x), RATE*12)-BLOCK, BLOCK)])
        if len(levels) < 24:
            continue
        floor = max(80, np.percentile(levels, 65)*.20)
        start = next((i for i in range(len(levels)-23) if (levels[i:i+23] >= floor).sum() >= 17 and levels[i] >= floor), None)
        if start is None:
            continue
        clip = x[start*BLOCK:(start+23)*BLOCK]
        clip /= max(rms(clip), 1)
        used += 1
        for amp, dur in itertools.product((100, 350, 1000), (.16, .28, .46)):
            ordinary.append(("dry", evidence(clip[:int(dur*RATE)]*amp)))
        for ratio, shape in itertools.product((.5, 1, 3, 10), ("harmonic", "contact", "ramp")):
            mix = clip*350 + buzz(shape, 170+10*(idx%5), len(clip)/RATE, 350*ratio, 9917+idx)
            ordinary.append((f"mixed_{ratio}", evidence(mix)))
        for amp, dur in itertools.product((9000, 17500, 25000), (.16, .28, .46)):
            loud.append(("loud_voice", evidence(np.clip(clip[:int(dur*RATE)]*amp, -32768, 32767))))
        for amp, shape in itertools.product((1000, 2000, 3500), ("harmonic", "contact", "ramp")):
            mix = clip*amp + buzz(shape, 175, len(clip)/RATE, 17500, 44100+idx)
            loud.append((f"mixed_voice_{amp}", evidence(np.clip(mix, -32768, 32767))))
        for gap, shape in itertools.product((0, .08, .18), ("harmonic", "contact", "ramp")):
            joined = np.r_[buzz(shape, 175, .24, 17500, 44100+idx), np.zeros(int(gap*RATE)), clip[:BLOCK*15]*350]
            after.append((gap, evidence(np.clip(joined, -32768, 32767))))
    recovery = {}
    for gap, frames in after:
        n = 12 + round(gap/.02)
        out = recovery.setdefault(str(gap), {"trials": 0, "before_voice_cut": 0, "speech_after": 0})
        out["trials"] += 1
        out["before_voice_cut"] += int(trigger(frames[:n], True))
        out["speech_after"] += int(trigger(frames[n:], True))
    return {"sources": used, "ordinary": report(ordinary), "high_amplitude": report(loud), "recovery": recovery}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--synthetic-dir", type=Path, help="Previously synthesized Supertonic WAV/PCM clips")
    parser.add_argument("--public-dir", type=Path, help="Public natural voice WAV/PCM directory (e.g. AEC Challenge final render fixtures)")
    parser.add_argument("--output", type=Path, help="Optional aggregate JSON report, without audio or clip names")
    args = parser.parse_args()
    if not args.synthetic_dir and not args.public_dir:
        parser.error("At least one public or synthetic speech directory is required")
    output = {"method": "worst-case energetic voting (6 of 12); not AGD4/libfvad or a physical device",
              "frame_ms": 20, "window_ms": 40, "parameters": PARAMETERS, "controls": {
                  "development": noise_cases((90,140,180,230,280), (150,600,2400), 42),
                  "synthetic_comparison": noise_cases((110,160,200,255,310), (150,600,2400), 10042),
                  "fresh": noise_cases((120,170,210,245,290), (110,450,3000), 265000),
                  "high_amplitude_clipping": noise_cases((125,175,215,260,295), (9500,17500,25000), 44100, True)}}
    if args.synthetic_dir:
        output["synthetic_speech"] = synthetic_speech(args.synthetic_dir)
    if args.public_dir:
        output["public_speech"] = public_speech(args.public_dir)
    text = json.dumps(output, indent=2) + "\n"
    if args.output:
        args.output.write_text(text, encoding="utf-8")
    print(text, end="")


if __name__ == "__main__":
    main()
