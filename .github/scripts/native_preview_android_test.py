import base64
import importlib.util
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location("preview_android", Path(__file__).with_name("native-preview-android.py"))
preview = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preview)


class PreviewSigningTests(unittest.TestCase):
    def config(self):
        return {"project_info": {"project_id": preview.guard.PROJECT, "project_number": "123"}, "client": [{
            "client_info": {"android_client_info": {"package_name": preview.guard.PACKAGE}, "mobilesdk_app_id": "synthetic-app-id"},
            "api_key": [{"current_key": "synthetic-public-client-key"}]}]}

    def environment(self):
        return {"FIREBASE_CLIENT_B64": base64.b64encode(json.dumps(self.config()).encode()).decode(), "ANDROID_HOME": "/synthetic-sdk",
                "ANDROID_KEYSTORE_BASE64": base64.b64encode(b"synthetic-key").decode(), "ANDROID_KEYSTORE_PASSWORD": "synthetic-password",
                "ANDROID_KEY_ALIAS": "synthetic-alias", "ANDROID_KEY_PASSWORD": "synthetic-password"}

    def test_firebase_rejects_service_accounts_and_other_application(self):
        with patch.dict(os.environ, self.environment()):
            _, configuration = preview.firebase()
            self.assertEqual(configuration, self.config())
            for invalid in ({**self.config(), "private_key": "must-never-enter-candidate"},
                            {**self.config(), "type": "service_account"}, {"type": "service_account"}):
                os.environ["FIREBASE_CLIENT_B64"] = base64.b64encode(json.dumps(invalid).encode()).decode()
                with self.assertRaises(SystemExit):
                    preview.firebase()

    def signing_case(self, temporary, unsafe=False):
        directory = Path(temporary)
        candidate, signed = directory / "candidate", directory / "signed"
        candidate.mkdir()
        source = candidate / "Aegis-Android-arm64.apk"
        with zipfile.ZipFile(source, "w") as archive:
            archive.writestr("lib/arm64-v8a/libaegis.so", b"release")
            archive.writestr("classes.dex", b"AEGIS_LEGACY_NLP_FIXTURE_V1" if unsafe else b"release-dex")
        (candidate / "BuildConfig.java").write_text("package com.aegis.node; public static final boolean AEGIS_FRAMEWORK_FIXTURE = false; public static final boolean DEBUG = false;")
        plan = directory / "plan.json"
        plan.write_text(json.dumps({"version": "0.7.2-unstable.21", "versionCode": 7002021}))
        return candidate, signed, plan

    def tool(self, calls, certificate=None):
        def invoke(arguments):
            calls.append(arguments)
            if arguments[1:3] == ["manifest", "print"]:
                return '<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="com.aegis.node" android:versionName="0.7.2-unstable.21" android:versionCode="7002021"><application android:usesCleartextTraffic="false"/></manifest>'
            if arguments[1:3] == ["resources", "value"]:
                return preview.guard.firebase_expected(self.config())[arguments[-2]]
            if arguments[1] == "sign":
                key = Path(arguments[arguments.index("--ks") + 1])
                self.assertEqual(key.stat().st_mode & 0o777, 0o600)
                self.assertEqual(key.read_bytes(), b"synthetic-key")
                self.assertIn("env:ANDROID_KEYSTORE_PASSWORD", arguments)
                self.assertNotIn("synthetic-password", arguments)
                shutil.copyfile(arguments[-1], arguments[arguments.index("--out") + 1])
            if arguments[1] == "verify":
                return "Number of signers: 1\nSigner #1 certificate SHA-256 digest: " + (certificate or preview.guard.CERTIFICATE) + "\n"
            return ""
        return invoke

    def test_replaces_disposable_signature_and_verifies_official_certificate_and_cleans_key(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, self.environment()):
            arguments = self.signing_case(temporary)
            calls = []
            with patch.object(preview, "run", self.tool(calls)):
                preview.sign(*arguments)
            self.assertTrue((arguments[1] / "Aegis-Android-arm64.apk").is_file())
            signing = next(call for call in calls if call[1] == "sign")
            self.assertFalse(Path(signing[signing.index("--ks") + 1]).exists())

    def test_fixture_is_rejected_before_private_keystore_is_decoded(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, self.environment()):
            arguments = self.signing_case(temporary, unsafe=True)
            calls = []
            with patch.object(preview, "run", self.tool(calls)), self.assertRaises(preview.guard.VerificationError):
                preview.sign(*arguments)
            self.assertFalse(any(call[1] == "sign" for call in calls))

    def test_wrong_signing_certificate_is_rejected_and_private_file_removed(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, self.environment()):
            arguments = self.signing_case(temporary)
            calls = []
            with patch.object(preview, "run", self.tool(calls, certificate="a" * 64)), self.assertRaises(preview.guard.VerificationError):
                preview.sign(*arguments)
            signing = next(call for call in calls if call[1] == "sign")
            self.assertFalse(Path(signing[signing.index("--ks") + 1]).exists())

    def test_promotion_verifies_final_apk_without_any_private_signing_credential(self):
        environment = {name: value for name, value in self.environment().items()
                       if name in ("FIREBASE_CLIENT_B64", "ANDROID_HOME")}
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, environment, clear=True):
            candidate, _, plan = self.signing_case(temporary)
            calls = []
            with patch.object(preview, "run", self.tool(calls)):
                preview.verify(candidate, plan)
            self.assertTrue(any(call[1] == "verify" for call in calls))
            self.assertFalse(any(call[1] == "sign" for call in calls))
            with patch.object(preview, "run", self.tool([], certificate="b" * 64)), self.assertRaises(preview.guard.VerificationError):
                preview.verify(candidate, plan)


if __name__ == "__main__":
    unittest.main()
