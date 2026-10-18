#!/usr/bin/env python3
"""Verify the distributable APK using public identity and official Android tools.

Provider configuration is compared in memory; no Firebase value is printed.
The framework fixture flag is checked in generated source because R8 can inline
and remove BuildConfig from the minified APK.
"""
import argparse
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ANDROID = "{http://schemas.android.com/apk/res/android}"
PACKAGE = "com.aegis.node"
PROJECT = "aegis-1a20c"
CERTIFICATE = "df5286c556657ddcee6ce79c510b967678bac8ced18d082db0e318bc47debb31"
FIXTURE_MARKERS = (
    b"AegisNodeNativeTest",
    b"http://127.0.0.1:18104",
    b"Lcom/aegis/node/NodeNotificationFrameworkTest;",
    b"Lcom/aegis/node/NodeGeofenceReceiverFrameworkTest;",
    b"Lcom/aegis/node/NodeGeofenceFrameworkEvidenceKt;",
    b"Lcom/aegis/node/NodeNotificationLiveTestKt;",
)


class VerificationError(Exception):
    pass


def require(condition, code):
    if not condition:
        raise VerificationError(code)


def tool(arguments):
    result = subprocess.run(arguments, capture_output=True, text=True, check=False)
    require(result.returncode == 0, "android_tool_failed")
    return result.stdout


def verify_certificate(output):
    lines = output.splitlines()
    counts = [match[1] for line in lines if (match := re.fullmatch(r"Number of signers: ([0-9]+)", line))]
    require(counts == ["1"], "signing_identity_mismatch")
    # Build-Tools 37 names the verified scheme; older tools number the signer.
    # Rotated/multiple application signers remain unsupported and fail closed.
    certificates = [line for line in lines if " certificate SHA-256 digest:" in line and not line.startswith("Source Stamp Signer certificate SHA-256 digest:")]
    require(len(certificates) == 1, "signing_identity_mismatch")
    match = re.fullmatch(r"(?:Signer #1|(?:V1|V2|V3\.0) Signer:) certificate SHA-256 digest: ([0-9a-fA-F]{64})", certificates[0])
    require(match is not None and match[1].lower() == CERTIFICATE, "signing_identity_mismatch")


def verify_manifest(source, version, version_code):
    try:
        root = ET.fromstring(source)
    except ET.ParseError:
        raise VerificationError("invalid_packaged_manifest") from None
    require(root.attrib.get("package") == PACKAGE, "package_mismatch")
    require(root.attrib.get(ANDROID + "versionName") == version, "version_name_mismatch")
    require(root.attrib.get(ANDROID + "versionCode") == str(version_code), "version_code_mismatch")
    app = root.find("application")
    require(app is not None, "missing_application")
    for name in ("debuggable", "testOnly"):
        require(app.attrib.get(ANDROID + name, "false") == "false", "unsafe_application_" + name)
    require(app.attrib.get(ANDROID + "usesCleartextTraffic") == "false", "unsafe_cleartext_policy")
    require(not any(element.tag.split("}")[-1] == "instrumentation" for element in root.iter()), "instrumentation_in_distribution")


def verify_build_config(source):
    require(re.search(r"\bpackage\s+com\.aegis\.node\s*;", source) is not None, "build_config_package_mismatch")
    for name in ("AEGIS_FRAMEWORK_FIXTURE", "DEBUG"):
        values = [value.strip() for value in re.findall(r"\bpublic\s+static\s+final\s+boolean\s+" + name + r"\s*=\s*([^;]+);", source)]
        require(values == ["false"], "unsafe_build_config_" + name.lower())


def verify_archive(apk):
    try:
        with zipfile.ZipFile(apk) as archive:
            entries = archive.infolist()
            require(len({item.filename for item in entries}) == len(entries), "duplicate_apk_entry")
            libraries = [item for item in entries if item.filename.startswith("lib/") and item.filename.endswith(".so")]
            require(libraries and {item.filename.split("/")[1] for item in libraries} == {"arm64-v8a"}, "abi_mismatch")
            dex = [item for item in entries if re.fullmatch(r"classes\d*\.dex", item.filename)]
            require(dex, "missing_dex")
            for item in libraries + dex:
                require(item.file_size <= 256 * 1024 * 1024, "excessive_apk_entry")
                with archive.open(item) as stream:
                    carry = b""
                    while chunk := stream.read(1024 * 1024):
                        data = carry + chunk
                        require(not any(marker in data for marker in FIXTURE_MARKERS), "fixture_marker_in_distribution")
                        carry = data[-max(map(len, FIXTURE_MARKERS)):]
    except (zipfile.BadZipFile, OSError):
        raise VerificationError("invalid_apk_archive") from None


def firebase_expected(configuration):
    try:
        project = configuration["project_info"]
        require(project["project_id"] == PROJECT, "firebase_project_mismatch")
        clients = [client for client in configuration["client"] if client["client_info"]["android_client_info"]["package_name"] == PACKAGE]
        require(len(clients) == 1, "firebase_client_mismatch")
        client = clients[0]
        values = {"project_id": project["project_id"], "gcm_defaultSenderId": project["project_number"],
                  "google_app_id": client["client_info"]["mobilesdk_app_id"], "google_api_key": client["api_key"][0]["current_key"]}
        require(all(isinstance(value, str) and value.strip() for value in values.values()), "firebase_configuration_incomplete")
        return values
    except (KeyError, TypeError, IndexError):
        raise VerificationError("firebase_configuration_invalid") from None


def verify_firebase(configuration, resource):
    for name, expected in firebase_expected(configuration).items():
        require(resource(name).strip() == expected, "packaged_firebase_mismatch_" + name)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", required=True, type=Path)
    parser.add_argument("--apksigner", required=True)
    parser.add_argument("--apkanalyzer", required=True)
    parser.add_argument("--package-json", type=Path, default=Path("package.json"))
    parser.add_argument("--tauri-config", type=Path, default=Path("src-tauri/tauri.conf.json"))
    parser.add_argument("--firebase-config", type=Path, default=Path("src-tauri/gen/android/app/google-services.json"))
    parser.add_argument("--build-config", type=Path, default=Path("src-tauri/gen/android/app/build/generated/source/buildConfig/universal/release/com/aegis/node/BuildConfig.java"))
    args = parser.parse_args(argv)
    try:
        version = json.loads(args.package_json.read_text())["version"]
        version_code = json.loads(args.tauri_config.read_text())["bundle"]["android"]["versionCode"]
        configuration = json.loads(args.firebase_config.read_text())
        firebase_expected(configuration)
        verify_build_config(args.build_config.read_text())
        verify_certificate(tool([args.apksigner, "verify", "--verbose", "--print-certs", str(args.apk)]))
        verify_manifest(tool([args.apkanalyzer, "manifest", "print", str(args.apk)]), version, version_code)
        verify_archive(args.apk)
        verify_firebase(configuration, lambda name: tool([args.apkanalyzer, "resources", "value", "--config", "default", "--type", "string", "--name", name, str(args.apk)]))
        print("Android distribution identity, version, ABI, release flags, fixture exclusion and Firebase configuration PASS.")
        return 0
    except VerificationError as error:
        print("Android distribution rejected: " + str(error), file=sys.stderr)
    except (OSError, ValueError, KeyError, TypeError):
        print("Android distribution rejected: missing_or_invalid_verification_input", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
