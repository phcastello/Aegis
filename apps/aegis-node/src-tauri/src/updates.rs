use serde::{Deserialize, Serialize};

pub const FEED: &str =
    "https://github.com/phcastello/Aegis/releases/download/node-preview/latest.json";
const RELEASE_ROOT: &str = "https://github.com/phcastello/Aegis/releases/download/node-v";

#[derive(Deserialize)]
struct Manifest {
    version: String,
    android: std::collections::HashMap<String, AndroidAsset>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct AndroidAsset {
    url: String,
    #[serde(default)]
    version_code: u32,
}
#[derive(Serialize, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AndroidUpdate {
    pub version: String,
    pub url: String,
}

fn version_code(version: &semver::Version) -> Result<u32, String> {
    let stage = if version.pre.is_empty() {
        999
    } else {
        version
            .pre
            .as_str()
            .strip_prefix("stage.")
            .and_then(|n| n.parse::<u32>().ok())
            .filter(|n| (1..999).contains(n))
            .ok_or("Unsupported preview version")?
    };
    if !version.build.is_empty() || version.major > 20 || version.minor > 99 || version.patch > 999
    {
        return Err("Version exceeds Android allocation".into());
    }
    Ok((version.major * 100000000 + version.minor * 1000000 + version.patch * 1000) as u32 + stage)
}

pub fn interpret(body: &str, current: &str) -> Result<Option<AndroidUpdate>, String> {
    let manifest: Manifest = serde_json::from_str(body).map_err(|_| "Invalid update metadata")?;
    let version =
        semver::Version::parse(&manifest.version).map_err(|_| "Invalid update version")?;
    let installed = semver::Version::parse(current).map_err(|_| "Invalid installed version")?;
    let next_code = version_code(&version)?;
    if version <= installed || next_code <= version_code(&installed)? {
        return Ok(None);
    }
    let asset = manifest
        .android
        .get("aarch64")
        .ok_or("Missing Android update")?;
    let expected_url = format!("{RELEASE_ROOT}{}/Aegis-Android-arm64.apk", manifest.version);
    if asset.url != expected_url || asset.version_code != next_code {
        return Err("Android asset URL or versionCode mismatch".into());
    }
    Ok(Some(AndroidUpdate {
        version: manifest.version,
        url: asset.url.clone(),
    }))
}

pub async fn check(current: &str) -> Result<Option<AndroidUpdate>, String> {
    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(10))
        .redirect(reqwest::redirect::Policy::custom(|attempt| {
            let host = attempt.url().host_str().unwrap_or("");
            if attempt.previous().len() > 5
                || attempt.url().scheme() != "https"
                || ![
                    "github.com",
                    "release-assets.githubusercontent.com",
                    "objects.githubusercontent.com",
                ]
                .contains(&host)
            {
                attempt.stop()
            } else {
                attempt.follow()
            }
        }))
        .build()
        .map_err(|_| "Cannot create update client")?;
    let mut response = client
        .get(FEED)
        .send()
        .await
        .map_err(|_| "Update source unavailable")?;
    // Before the first preview release there is intentionally no feed.
    if response.status() == reqwest::StatusCode::NOT_FOUND {
        return Ok(None);
    }
    if !response.status().is_success() {
        return Err("Update source unavailable".into());
    }
    let mut bytes = Vec::new();
    while let Some(chunk) = response.chunk().await.map_err(|_| "Cannot read metadata")? {
        if bytes.len() + chunk.len() > 65536 {
            return Err("Update metadata too large".into());
        }
        bytes.extend_from_slice(&chunk);
    }
    let body = std::str::from_utf8(&bytes).map_err(|_| "Invalid update metadata encoding")?;
    interpret(body, current)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn manifest(version: &str, code: u32, url: &str) -> String {
        serde_json::json!({"version":version,"android":{"aarch64":{"url":url,"versionCode":code}}})
            .to_string()
    }
    #[test]
    fn accepts_only_newer_correctly_versioned_official_apk() {
        let url = format!("{RELEASE_ROOT}0.7.0-stage.3/Aegis-Android-arm64.apk");
        assert!(
            interpret(&manifest("0.7.0-stage.3", 7000003, &url), "0.7.0-stage.2")
                .unwrap()
                .is_some()
        );
        assert!(
            interpret(&manifest("0.7.0-stage.3", 7000003, &url), "0.7.0-stage.3")
                .unwrap()
                .is_none()
        );
        assert!(
            interpret(&manifest("0.7.0-stage.3", 7000003, &url), "0.7.0")
                .unwrap()
                .is_none()
        );
        assert!(interpret(&manifest("0.7.0-stage.3", 7000004, &url), "0.7.0-stage.2").is_err());
        assert!(interpret(
            &manifest("0.7.0-stage.3", 7000003, "https://evil.example/app.apk"),
            "0.7.0-stage.2"
        )
        .is_err());
        assert!(interpret("not json", "0.7.0-stage.2").is_err());
    }
    #[test]
    fn rejects_unallocated_versions() {
        for value in [
            "0.7.0-stage.999",
            "0.7.0-beta.1",
            "0.100.0",
            "0.7.0+untrusted",
        ] {
            assert!(version_code(&semver::Version::parse(value).unwrap()).is_err());
        }
    }
}

#[cfg(all(test, windows, feature = "native-runtime"))]
mod desktop_tests {
    #[test]
    fn official_updater_accepts_manifest_with_separate_android_extension() {
        let manifest = serde_json::json!({
            "version": "0.7.0-stage.3", "pub_date": "2026-10-02T00:00:00Z",
            "platforms": { "windows-x86_64": {
                "url": "https://github.com/phcastello/Aegis/releases/download/node-v0.7.0-stage.3/Aegis-Windows-x86_64-Setup.exe",
                "signature": "fixture-signature-not-an-installation-test"
            } },
            "android": { "aarch64": {
                "url": "https://github.com/phcastello/Aegis/releases/download/node-v0.7.0-stage.3/Aegis-Android-arm64.apk",
                "versionCode": 7000003
            } }
        });
        let release: tauri_plugin_updater::RemoteRelease =
            serde_json::from_value(manifest).unwrap();
        assert_eq!(release.version.to_string(), "0.7.0-stage.3");
        assert_eq!(
            release.signature("windows-x86_64").unwrap(),
            "fixture-signature-not-an-installation-test"
        );
    }
}
