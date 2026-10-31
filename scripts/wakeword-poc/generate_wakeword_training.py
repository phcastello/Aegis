#!/usr/bin/env python3
"""Make a small local Piper PT-BR corpus; never reads owner recordings."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import wave

import numpy as np
import onnxruntime as ort
from piper import PiperVoice, SynthesisConfig
from piper.config import PiperConfig
from scipy.signal import resample_poly, fftconvolve

from prepare import private_output, sha256


SEED = 20261031
REVISION = "b4e358ffcb232591bf73a540e70f7de528e4174a"
POSITIVE = ["Aéjis.", "Aéjis!", "Aéjis?", "Aéjis, que horas são?",
            "Aéjis, abra a agenda.", "Aéjis, me ajude agora."]
NEGATIVE = ["É isso.", "Ela disse.", "Hoje.", "Agis.", "Êxito.", "Edi.", "Exigir.",
            "Egeu.", "Ágil.", "É gente.", "Eu disse.", "Veja isso.", "Registre isso.",
            "A agenda está aberta.", "A gente chegou cedo.", "Preciso de ajuda.",
            "Abra a janela.", "Feche a porta.", "Que horas são?", "Bom dia.", "Boa noite.",
            "Vamos conversar depois.", "Não é verdade.", "O telefone está tocando.",
            "O cachorro está dormindo.", "Eu gosto de música.", "A chuva começou.",
            "O vento está forte.", "Agora está tudo pronto.", "O café está quente.",
            "Leia o livro.", "A imagem ficou boa.", "Esse é o resultado.", "O relógio parou.",
            "Lembre de comprar pão.", "Mais um exemplo.", "Estamos em casa.",
            "A voz está baixa.", "Ele pediu silêncio.", "Você entendeu?"]


def seed(value):
    return SEED ^ int(hashlib.sha256(value.encode()).hexdigest()[:8], 16)


def save(path, samples):
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as out:
        out.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
        out.writeframes(np.clip(samples * 32767, -32768, 32767).astype("<i2").tobytes())


def active(audio):
    # Keep fricatives: threshold is only 1% of peak, with 50 ms margins.
    frames = np.pad(audio, (0, (-len(audio)) % 160)).reshape(-1, 160)
    rms = np.sqrt(np.mean(frames ** 2, axis=1))
    indices = np.flatnonzero(rms > max(rms.max() * .01, 1e-5))
    start = max(0, int(indices[0]) * 160 - 800)
    end = min(len(audio), (int(indices[-1]) + 1) * 160 + 800)
    return audio[start:end]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--voices", required=True, type=Path)
    p.add_argument("--output", required=True, type=private_output)
    args = p.parse_args()
    os.umask(0o077)
    if args.output.exists():
        p.error("Use a new output directory")
    args.output.mkdir(parents=True)
    records, sources, voices = [], [], []
    for name in ("cadu", "faber", "jeff"):
        model = args.voices / name / f"pt_BR-{name}-medium.onnx"
        options = ort.SessionOptions()
        options.intra_op_num_threads = options.inter_op_num_threads = 1
        voice = PiperVoice(config=PiperConfig.from_dict(json.loads(Path(str(model) + ".json").read_text())),
                           session=ort.InferenceSession(str(model), sess_options=options,
                                                       providers=["CPUExecutionProvider"]))
        voices.append({"voice": name, "model_sha256": sha256(model),
                       "source": f"https://huggingface.co/rhasspy/piper-voices/tree/{REVISION}/pt/pt_BR/{name}/medium",
                       "model_card_dataset_license": "CC0", "phonemes": voice.phonemize("Aéjis.")})
        for speed in (.85, 1.0, 1.15):
            syn = SynthesisConfig(length_scale=speed, noise_scale=0.0, noise_w_scale=0.0)
            def synth(text):
                chunks = list(voice.synthesize(text, syn_config=syn))
                audio = np.concatenate([chunk.audio_float_array for chunk in chunks])
                return active(resample_poly(audio, 320, 441).astype(np.float32))
            isolated_length = len(synth("Aéjis.")) / 16000
            for label, texts in (("positive", POSITIVE), ("negative", NEGATIVE)):
                for text in texts:
                    identity = f"{name}|{speed}|{label}|{text}"
                    sid = hashlib.sha256(identity.encode()).hexdigest()[:12]
                    split = "validation" if seed(identity) % 5 == 0 else "train"
                    audio = synth(text)
                    source_path = Path("sources") / f"{sid}.wav"
                    save(args.output / source_path, audio)
                    sources.append({"source_id": sid, "path": str(source_path), "voice": name,
                                    "length_scale": speed, "label": label, "text": text, "split": split})
                    for augmentation in range(24 if label == "positive" else 8):
                        rid = f"{sid}-{augmentation:02d}"
                        rng = np.random.default_rng(seed(rid))
                        offset = int(rng.uniform(.35, 1.15) * 16000)
                        clip = np.zeros(48000, np.float32)
                        count = min(len(audio), len(clip) - offset)
                        clip[offset:offset + count] = audio[:count]
                        if augmentation % 3:
                            ir = np.zeros(2401, np.float32)
                            ir[0] = 1
                            for delay in (313, 787, 1609, 2399):
                                ir[delay] = rng.uniform(.02, .12)
                            clip = fftconvolve(clip, ir)[:48000]
                        gain = float(rng.uniform(.15, .85))
                        clip *= gain
                        snr = float(rng.uniform(15, 35))
                        power = max(float(np.sqrt(np.mean(clip ** 2))), .002)
                        clip += rng.normal(0, power / 10 ** (snr / 20), len(clip))
                        path = Path(split) / label / f"{rid}.wav"
                        save(args.output / path, clip)
                        records.append({"id": rid, "source_id": sid, "path": str(path), "label": label,
                                        "split": split, "voice": name, "gain": gain, "snr_db": snr,
                                        "keyword_start_seconds": offset / 16000 if label == "positive" else None,
                                        "keyword_end_seconds": (offset / 16000 + (len(audio) / 16000 if text in POSITIVE[:3] else isolated_length)) if label == "positive" else None,
                                        "interval_estimated": label == "positive" and text not in POSITIVE[:3]})
        print(f"Generated {name}: {len(records)} clips", flush=True)
    # Background-only negatives supplement speech/confusers without external data.
    for i in range(128):
        rng = np.random.default_rng(seed(f"noise-{i}"))
        split = "validation" if i % 5 == 0 else "train"
        audio = rng.normal(0, rng.uniform(.001, .04), 48000).astype(np.float32)
        if i % 2:
            audio += .01 * np.sin(np.arange(48000) * (2 * np.pi * 60 / 16000))
        path = Path(split) / "negative" / f"noise-{i:03d}.wav"
        save(args.output / path, audio)
        records.append({"id": f"noise-{i:03d}", "source_id": f"noise-{i:03d}", "path": str(path),
                        "label": "negative", "split": split, "voice": "noise"})
    manifest = {"seed": SEED, "piper_version": "1.3.0", "runtime_license": "GPL-3.0",
                "pronunciation": "Aéjis /aˈɛʒis/; PT-BR Piper phonemizer output recorded per voice",
                "audio_format": "16kHz mono PCM16, 3s", "split_rule": "SHA256(source identity) modulo5, before augmentation",
                "owner_data_used": False, "voices": voices, "sources": sources, "records": records}
    (args.output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    print(f"Saved {len(records)} synthetic records to {args.output}")


if __name__ == "__main__":
    main()
