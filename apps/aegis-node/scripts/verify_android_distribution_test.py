import importlib.util
import contextlib
import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("android_distribution", Path(__file__).with_name("verify-android-distribution.py"))
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class DistributionTests(unittest.TestCase):
    def manifest(self, extra="", version="0.7.0-unstable.12", code=7000012, package="com.aegis.node", child=""):
        return f'<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{package}" android:versionName="{version}" android:versionCode="{code}"><application android:usesCleartextTraffic="false" {extra}/>{child}</manifest>'

    def test_manifest_accepts_release_and_rejects_identity_version_or_test_flags(self):
        guard.verify_manifest(self.manifest(), "0.7.0-unstable.12", 7000012)
        for source in [self.manifest(package="other.app"), self.manifest(version="0.7.0-unstable.11"), self.manifest(code=7000011),
                       self.manifest(extra='android:debuggable="true"'), self.manifest(extra='android:testOnly="true"'),
                       self.manifest(child='<instrumentation android:name="test.Runner"/>'), self.manifest().replace('usesCleartextTraffic="false"', 'usesCleartextTraffic="true"')]:
            with self.subTest(source=source), self.assertRaises(guard.VerificationError):
                guard.verify_manifest(source, "0.7.0-unstable.12", 7000012)

    def test_certificate_is_pinned_and_multiple_signers_are_rejected(self):
        header = "Number of signers: 1\n"
        line = "Signer #1 certificate SHA-256 digest: " + guard.CERTIFICATE + "\n"
        guard.verify_certificate(header + line)
        for output in ["", line, header + line.replace(guard.CERTIFICATE, "a" * 64), header + line + line.replace("#1", "#2"), header.replace("1", "2") + line, header + header + line]:
            with self.assertRaises(guard.VerificationError):
                guard.verify_certificate(output)

    def test_build_tools_37_scheme_labels_preserve_the_same_single_signer_pin(self):
        for label in ("V1 Signer:", "V2 Signer:", "V3.0 Signer:"):
            output = "Number of signers: 1\r\n" + label + " certificate SHA-256 digest: " + guard.CERTIFICATE + "\r\n"
            with self.subTest(label=label):
                guard.verify_certificate(output)
                for invalid in (output.replace(guard.CERTIFICATE, "a" * 64), output.replace("signers: 1", "signers: 2"), output + output.splitlines()[1] + "\n", output.replace(label, "Unknown Signer:")):
                    with self.assertRaises(guard.VerificationError): guard.verify_certificate(invalid)

    def test_modern_multi_signer_and_mixed_or_rotation_layouts_are_rejected(self):
        pin = guard.CERTIFICATE
        for output in (
            f"Number of signers: 2\nV2 Signer #1: certificate SHA-256 digest: {pin}\nV2 Signer #2: certificate SHA-256 digest: {pin}\n",
            f"Number of signers: 1\nSigner #1 certificate SHA-256 digest: {pin}\nV2 Signer: certificate SHA-256 digest: {pin}\n",
            f"Number of signers: 1\nV3.1 Signer (minSdkVersion=33): certificate SHA-256 digest: {pin}\n",
            f"Number of signers: 1\nV2 Signer: certificate SHA-256 digest: {pin}\nUnknown Signer: certificate SHA-256 digest: {'a' * 64}\n",
        ):
            with self.subTest(output=output), self.assertRaises(guard.VerificationError): guard.verify_certificate(output)

    def test_generated_build_config_is_explicitly_production(self):
        source = "package com.aegis.node; public static final boolean AEGIS_FRAMEWORK_FIXTURE = false; public static final boolean DEBUG = false;"
        guard.verify_build_config(source)
        for invalid in [source.replace("FIXTURE = false", "FIXTURE = true"), source.replace("DEBUG = false", "DEBUG = true"), source.replace("AEGIS_FRAMEWORK_FIXTURE", "OTHER"), source.replace("com.aegis.node", "other.app")]:
            with self.assertRaises(guard.VerificationError):
                guard.verify_build_config(invalid)

    def test_archive_checks_abi_dex_and_fixture_markers_including_chunk_boundary(self):
        with tempfile.TemporaryDirectory() as temporary:
            apk = Path(temporary) / "synthetic.apk"
            def write(abi="arm64-v8a", native=b"release", dex=b"release-dex"):
                with zipfile.ZipFile(apk, "w") as archive:
                    archive.writestr("lib/" + abi + "/libsynthetic.so", native)
                    archive.writestr("classes.dex", dex)
            write(); guard.verify_archive(apk)
            cases = [("x86_64", b"release", b"release-dex"), ("arm64-v8a", b"AegisNodeNativeTest", b"release-dex"),
                     ("arm64-v8a", b"release", b"Lcom/aegis/node/NodeNotificationFrameworkTest;"),
                     ("arm64-v8a", b"release", b"Lcom/aegis/node/NodeGeofenceReceiverFrameworkTest;"),
                     ("arm64-v8a", b"x" * (1024 * 1024 - 5) + b"http://127.0.0.1:18104", b"release-dex")]
            for abi, native, dex in cases:
                write(abi, native, dex)
                with self.assertRaises(guard.VerificationError): guard.verify_archive(apk)
            with zipfile.ZipFile(apk, "w") as archive: archive.writestr("lib/arm64-v8a/libsynthetic.so", b"release")
            with self.assertRaises(guard.VerificationError): guard.verify_archive(apk)

    def test_firebase_requires_expected_project_package_and_all_packaged_values(self):
        configuration = {"project_info": {"project_id": guard.PROJECT, "project_number": "123456789"}, "client": [{"client_info": {"android_client_info": {"package_name": guard.PACKAGE}, "mobilesdk_app_id": "synthetic-public-app-id"}, "api_key": [{"current_key": "synthetic-public-api-key"}]}]}
        values = guard.firebase_expected(configuration)
        guard.verify_firebase(configuration, values.__getitem__)
        with self.assertRaisesRegex(guard.VerificationError, "packaged_firebase_mismatch_google_app_id"):
            guard.verify_firebase(configuration, lambda name: "" if name == "google_app_id" else values[name])
        configuration["project_info"]["project_id"] = "other-project"
        with self.assertRaisesRegex(guard.VerificationError, "firebase_project_mismatch"): guard.firebase_expected(configuration)
        configuration["project_info"]["project_id"] = guard.PROJECT
        configuration["client"][0]["client_info"]["android_client_info"]["package_name"] = "other.app"
        with self.assertRaisesRegex(guard.VerificationError, "firebase_client_mismatch"): guard.firebase_expected(configuration)
        with self.assertRaises(guard.VerificationError): guard.firebase_expected({})

    def test_cli_verifies_all_inputs_and_never_prints_firebase_values_on_mismatch(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); apk = root / "app.apk"
            with zipfile.ZipFile(apk, "w") as archive:
                archive.writestr("lib/arm64-v8a/libsynthetic.so", b"release")
                archive.writestr("classes.dex", b"release")
            configuration = {"project_info": {"project_id": guard.PROJECT, "project_number": "123456789"}, "client": [{"client_info": {"android_client_info": {"package_name": guard.PACKAGE}, "mobilesdk_app_id": "synthetic-public-app-id"}, "api_key": [{"current_key": "synthetic-public-api-key"}]}]}
            (root / "package.json").write_text(json.dumps({"version": "0.7.0-unstable.12"}))
            (root / "tauri.json").write_text(json.dumps({"bundle": {"android": {"versionCode": 7000012}}}))
            (root / "firebase.json").write_text(json.dumps(configuration))
            (root / "BuildConfig.java").write_text("package com.aegis.node; public static final boolean AEGIS_FRAMEWORK_FIXTURE = false; public static final boolean DEBUG = false;")
            arguments = ["--apk", str(apk), "--apksigner", "apksigner", "--apkanalyzer", "apkanalyzer", "--package-json", str(root / "package.json"), "--tauri-config", str(root / "tauri.json"), "--firebase-config", str(root / "firebase.json"), "--build-config", str(root / "BuildConfig.java")]
            outputs = ["Number of signers: 1\nSigner #1 certificate SHA-256 digest: " + guard.CERTIFICATE, self.manifest()] + list(guard.firebase_expected(configuration).values())
            with patch.object(guard, "tool", side_effect=outputs), contextlib.redirect_stdout(io.StringIO()): self.assertEqual(0, guard.main(arguments))
            errors = io.StringIO()
            with patch.object(guard, "tool", side_effect=outputs[:-1] + ["other-key"]), contextlib.redirect_stderr(errors): self.assertEqual(1, guard.main(arguments))
            self.assertIn("packaged_firebase_mismatch_google_api_key", errors.getvalue())
            self.assertNotIn("synthetic-public-api-key", errors.getvalue()); self.assertNotIn("other-key", errors.getvalue())


if __name__ == "__main__":
    unittest.main()
