#!/usr/bin/env python3
"""Train one small ONNX head on frozen official openWakeWord embeddings, locally."""

import argparse
import copy
import importlib.metadata
import json
import os
from pathlib import Path
import time
import wave

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper
from openwakeword.utils import AudioFeatures
from sklearn.metrics import log_loss, confusion_matrix
from sklearn.neural_network import MLPClassifier
from sklearn.preprocessing import StandardScaler
from threadpoolctl import threadpool_limits
from scipy.signal import resample_poly

from prepare import private_output, sha256

SEED = 20261031
REFERENCE_IDS = {"android-positive-01", "android-positive-07", "android-positive-09",
                 "windows-positive-08", "windows-positive-09", "windows-positive-12"}


def read(path):
    with wave.open(str(path)) as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) != (1, 2, 16000):
            raise ValueError(f"Unexpected audio format: {path.name}")
        return np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2").copy()


def extract(features, audio, positive=False, keyword_end=None, all_negative_frames=False):
    # Seed native random warm-up deterministically; inference adapter does the same.
    np.random.seed(SEED)
    features.reset()
    vectors, labels = [], []
    for start in range(0, len(audio), 1280):
        block = audio[start:start + 1280]
        features(np.pad(block, (0, 1280 - len(block))))
        end = (start + 1280) / 16000
        if end < .48:
            continue
        target = positive and keyword_end <= end <= keyword_end + .48
        if target or (not positive and (all_negative_frames or (start // 1280) % 6 == 0)):
            vectors.append(features.get_features(16)[0].reshape(-1))
            labels.append(int(target))
    if not vectors:
        raise ValueError("No feature windows extracted")
    return vectors, labels


def export(path, scaler, model):
    nodes = [helper.make_node("Flatten", ["features"], ["flat"], axis=1),
             helper.make_node("Sub", ["flat", "mean"], ["centered"]),
             helper.make_node("Div", ["centered", "scale"], ["scaled"])]
    arrays = {"mean": scaler.mean_.astype(np.float32), "scale": scaler.scale_.astype(np.float32)}
    previous = "scaled"
    for i, (weights, bias) in enumerate(zip(model.coefs_, model.intercepts_)):
        arrays[f"w{i}"] = weights.astype(np.float32)
        arrays[f"b{i}"] = bias.astype(np.float32)
        nodes.extend([helper.make_node("MatMul", [previous, f"w{i}"], [f"mm{i}"]),
                      helper.make_node("Add", [f"mm{i}", f"b{i}"], [f"sum{i}"]),
                      helper.make_node("Sigmoid" if i == len(model.coefs_) - 1 else "Relu",
                                       [f"sum{i}"], ["score" if i == len(model.coefs_) - 1 else f"act{i}"])])
        previous = f"act{i}"
    graph = helper.make_graph(nodes, "Aegis-openWakeWord-custom-head",
                              [helper.make_tensor_value_info("features", TensorProto.FLOAT, [None, 16, 96])],
                              [helper.make_tensor_value_info("score", TensorProto.FLOAT, [None, 1])],
                              [numpy_helper.from_array(value, name) for name, value in arrays.items()])
    proto = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 13)])
    proto.ir_version = 8
    onnx.checker.check_model(proto)
    onnx.save(proto, path)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--training", type=Path, required=True)
    p.add_argument("--models", type=Path, required=True)
    p.add_argument("--references", type=Path, required=True)
    p.add_argument("--reuse-features", type=Path,
                   help="Reuse the initial sparse extraction with matching provenance; avoids repeating CPU extraction")
    p.add_argument("--output", type=private_output, required=True)
    args = p.parse_args()
    os.umask(0o077)
    if args.output.exists():
        p.error("Use a new private output directory")
    args.output.mkdir(parents=True)
    started = time.perf_counter()
    manifest_path = args.training / "manifest.json"
    corpus = json.loads(manifest_path.read_text())
    if corpus["owner_data_used"]:
        raise ValueError("The shared synthetic corpus must not contain owner data")
    features = AudioFeatures(melspec_model_path=str(args.models / "melspectrogram.onnx"),
                             embedding_model_path=str(args.models / "embedding_model.onnx"),
                             ncpu=1, inference_framework="onnx", device="cpu")
    data = {split: ([], []) for split in ("train", "validation")}
    references = sorted(args.references.glob("*.wav"))
    if {path.stem for path in references} != REFERENCE_IDS:
        raise ValueError("Only the six previously designated reference crops are permitted")
    if args.reuse_features:
        previous = json.loads(args.reuse_features.with_name("training.json").read_text())
        if (previous["synthetic_manifest_sha256"] != sha256(manifest_path)
                or previous["reference_hashes"] != {path.stem: sha256(path) for path in references}
                or previous.get("complete_temporal_negative_clips")):
            raise ValueError("Feature cache must be the initial extraction with identical sources")
        with np.load(args.reuse_features) as cached:
            for split, xkey, ykey in (("train", "x", "y"), ("validation", "xv", "yv")):
                data[split][0].extend(cached[xkey])
                data[split][1].extend(cached[ykey])
    else:
        for i, row in enumerate(corpus["records"]):
            values, labels = extract(features, read(args.training / row["path"]),
                                     row["label"] == "positive", row.get("keyword_end_seconds"))
            data[row["split"]][0].extend(values)
            data[row["split"]][1].extend(labels)
            if i % 250 == 0:
                print(f"Extracted {i}/{len(corpus['records'])} synthetic clips", flush=True)
        rng = np.random.default_rng(SEED)
        for path in references:
            source = read(path)
            for _ in range(64):
                speed = float(rng.uniform(.8, 1.2))
                speech = resample_poly(source.astype(np.float32), round(100 / speed), 100)
                offset = int(rng.uniform(.35, 1.15) * 16000)
                audio = rng.normal(0, rng.uniform(8, 90), 48000)
                audio[offset:offset + len(speech)] += speech * rng.uniform(.3, 1.2)
                audio = np.clip(audio, -32768, 32767).astype(np.int16)
                values, labels = extract(features, audio, True, (offset + len(speech)) / 16000)
                data["train"][0].extend(values)
                data["train"][1].extend(labels)
    # Sparse regular time sampling can hide between-grid false activations.
    # Cover every 80 ms position on a deterministic random sample, without mining
    # held-out mistakes. Neither these clips nor any validation clip is owner audio.
    full_frame_clips = {}
    temporal_rng = np.random.default_rng(SEED + 1)
    for split, limit in (("train", 400), ("validation", 200)):
        candidates = [row for row in corpus["records"] if row["split"] == split and row["label"] == "negative"]
        selected = temporal_rng.choice(len(candidates), min(limit, len(candidates)), replace=False)
        full_frame_clips[split] = [candidates[i]["id"] for i in selected]
        for i, index in enumerate(selected):
            values, labels = extract(features, read(args.training / candidates[index]["path"]), all_negative_frames=True)
            data[split][0].extend(values)
            data[split][1].extend(labels)
            if i % 100 == 0:
                print(f"Complete temporal negatives {split}: {i}/{len(selected)}", flush=True)
    x, y = np.asarray(data["train"][0], np.float32), np.asarray(data["train"][1])
    xv, yv = np.asarray(data["validation"][0], np.float32), np.asarray(data["validation"][1])
    np.savez_compressed(args.output / "features.npz", x=x, y=y, xv=xv, yv=yv)
    scaler = StandardScaler().fit(x)
    x, xv = scaler.transform(x), scaler.transform(xv)
    model = MLPClassifier(hidden_layer_sizes=(64, 32), random_state=SEED, batch_size=256,
                          alpha=.01, learning_rate_init=.001, max_iter=1)
    best_loss, best, history = float("inf"), None, []
    fit_rng = np.random.default_rng(SEED + 2)
    with threadpool_limits(limits=2):
        for epoch in range(40):
            order = fit_rng.permutation(len(x))
            model.partial_fit(x[order], y[order], classes=[0, 1])
            probability = model.predict_proba(xv)[:, 1]
            loss = log_loss(yv, probability, labels=[0, 1])
            history.append({"epoch": epoch + 1, "validation_log_loss": loss})
            if loss < best_loss:
                best_loss, best = loss, copy.deepcopy(model)
            print(f"Epoch {epoch + 1}/40: synthetic validation loss {loss:.5f}", flush=True)
    export(args.output / "aegis.onnx", scaler, best)
    import onnxruntime as ort
    session = ort.InferenceSession(str(args.output / "aegis.onnx"), providers=["CPUExecutionProvider"])
    check = np.asarray(data["validation"][0][:8], np.float32).reshape(-1, 16, 96)
    native = session.run(None, {"features": check})[0].ravel()
    expected = best.predict_proba(scaler.transform(check.reshape(-1, 1536)))[:, 1]
    assert np.allclose(native, expected, atol=1e-5), "ONNX export differs from trained head"
    report = {"seed": SEED, "architecture": "frozen official OWW mel/Google speech embedding + StandardScaler + MLP64/32/sigmoid",
              "training_windows": len(y), "validation_windows": len(yv),
              "synthetic_clips_total": len(corpus["records"]),
              "synthetic_train_clips": sum(row["split"] == "train" for row in corpus["records"]),
              "synthetic_validation_clips": sum(row["split"] == "validation" for row in corpus["records"]),
              "reference_augmentation_clips": len(references) * 64,
              "complete_temporal_negative_clips": full_frame_clips,
              "synthetic_validation_confusion_0.5": confusion_matrix(yv, best.predict(xv)).tolist(),
              "checkpoint_rule": "minimum synthetic-validation log loss among 40 fixed epochs; no owner holdout used",
              "best_epoch": min(history, key=lambda row: row["validation_log_loss"])["epoch"], "history": history,
              "uses_reference_audio": True, "adaptation_reference_ids": sorted(REFERENCE_IDS),
              "reference_hashes": {path.stem: sha256(path) for path in references},
              "synthetic_manifest_sha256": sha256(manifest_path),
              "model_sha256": sha256(args.output / "aegis.onnx"), "elapsed_seconds": time.perf_counter() - started,
              "packages": {name: importlib.metadata.version(name) for name in ("openwakeword", "scikit-learn", "numpy", "onnx", "onnxruntime")}}
    (args.output / "training.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({key: value for key, value in report.items() if key not in ("history", "reference_hashes")}, indent=2))


if __name__ == "__main__":
    main()
