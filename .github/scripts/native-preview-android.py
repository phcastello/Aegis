#!/usr/bin/env python3
"""Trusted APK validation/signing. Candidate code and Gradle never run here."""
import base64
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("distribution", ROOT / "apps/aegis-node/scripts/verify-android-distribution.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


def firebase():
    try:
        value = base64.b64decode(os.environ.get("FIREBASE_CLIENT_B64", ""), validate=True)
        config = json.loads(value)
        guard.firebase_expected(config)
        # This is the public google-services client config embedded in every APK.
        # A service-account private key must never enter candidate build jobs.
        guard.require("private_key" not in config and "private_key_id" not in config, "firebase_service_account_forbidden")
        guard.require(config.get("type") != "service_account", "firebase_service_account_forbidden")
        return value, config
    except (ValueError, KeyError, TypeError, guard.VerificationError):
        raise SystemExit("Preview requires the existing public Android Firebase client configuration.") from None


def run(arguments):
    result = subprocess.run(arguments, capture_output=True, text=True, check=False)
    guard.require(result.returncode == 0, "preview_android_tool_failed")
    return result.stdout


def sign(candidate, output, plan_file):
    value, config = firebase()
    del value
    candidate, output = Path(candidate), Path(output)
    plan = json.loads(Path(plan_file).read_text())
    apk = candidate / "Aegis-Android-arm64.apk"
    tools = Path(os.environ["ANDROID_HOME"]) / "build-tools/37.0.0"
    analyzer = str(Path(os.environ["ANDROID_HOME"]) / "cmdline-tools/latest/bin/apkanalyzer")
    guard.verify_build_config((candidate / "BuildConfig.java").read_text())
    guard.verify_manifest(run([analyzer, "manifest", "print", str(apk)]), plan["version"], plan["versionCode"])
    guard.verify_archive(apk)
    guard.verify_firebase(config, lambda name: run([analyzer, "resources", "value", "--config", "default", "--type", "string", "--name", name, str(apk)]))
    run([str(tools / "zipalign"), "-c", "-P", "16", "4", str(apk)])
    required = ("ANDROID_KEYSTORE_BASE64", "ANDROID_KEYSTORE_PASSWORD", "ANDROID_KEY_ALIAS", "ANDROID_KEY_PASSWORD")
    guard.require(all(os.environ.get(name) for name in required), "missing_official_preview_signing_secret")
    output.mkdir(parents=True, exist_ok=True)
    signed = output / apk.name
    # Temporary private file is removed on success and failure, never uploaded.
    with tempfile.TemporaryDirectory(prefix="aegis-preview-key-") as directory:
        keystore = Path(directory) / "official.p12"
        keystore.write_bytes(base64.b64decode(os.environ["ANDROID_KEYSTORE_BASE64"], validate=True))
        keystore.chmod(0o600)
        run([str(tools / "apksigner"), "sign", "--ks", str(keystore), "--ks-key-alias", os.environ["ANDROID_KEY_ALIAS"],
             "--ks-pass", "env:ANDROID_KEYSTORE_PASSWORD", "--key-pass", "env:ANDROID_KEY_PASSWORD", "--out", str(signed), str(apk)])
    guard.verify_certificate(run([str(tools / "apksigner"), "verify", "--verbose", "--print-certs", str(signed)]))
    guard.verify_manifest(run([analyzer, "manifest", "print", str(signed)]), plan["version"], plan["versionCode"])
    guard.verify_archive(signed)
    run([str(tools / "zipalign"), "-c", "-P", "16", "4", str(signed)])
    # apksigner's optional incremental-install sidecar is unnecessary for delivery.
    signed.with_suffix(".apk.idsig").unlink(missing_ok=True)
    print("Preview Android official identity, certificate, version, Firebase, ARM64 and fixture guards PASS.")


def verify(candidate, plan_file):
    """Recheck final public bytes without loading any private signing credential."""
    _, config = firebase()
    apk = Path(candidate) / "Aegis-Android-arm64.apk"
    plan = json.loads(Path(plan_file).read_text())
    tools = Path(os.environ["ANDROID_HOME"]) / "build-tools/37.0.0"
    analyzer = str(Path(os.environ["ANDROID_HOME"]) / "cmdline-tools/latest/bin/apkanalyzer")
    guard.verify_manifest(run([analyzer, "manifest", "print", str(apk)]), plan["version"], plan["versionCode"])
    guard.verify_archive(apk)
    guard.verify_certificate(run([str(tools / "apksigner"), "verify", "--verbose", "--print-certs", str(apk)]))
    guard.verify_firebase(config, lambda name: run([analyzer, "resources", "value", "--config", "default", "--type", "string", "--name", name, str(apk)]))
    run([str(tools / "zipalign"), "-c", "-P", "16", "4", str(apk)])
    print("Final Android identity, official certificate, version, Firebase and archive guards PASS.")


if __name__ == "__main__":
    try:
        if sys.argv[1] == "firebase":
            value, _ = firebase()
            target = Path(sys.argv[2])
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(value)
        elif sys.argv[1] == "sign":
            sign(*sys.argv[2:])
        elif sys.argv[1] == "verify":
            verify(*sys.argv[2:])
        else:
            raise SystemExit("Unknown preview Android command.")
    except (OSError, ValueError, KeyError, TypeError, guard.VerificationError):
        raise SystemExit("Preview Android validation/signing rejected; no credentials are logged.") from None
