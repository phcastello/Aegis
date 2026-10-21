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
    pub version_code: u32,
}

fn version_code(version: &semver::Version) -> Result<u32, String> {
    let preview = if version.pre.is_empty() {
        999
    } else {
        version
            .pre
            .as_str()
            .strip_prefix("unstable.")
            .or_else(|| version.pre.as_str().strip_prefix("stage."))
            .and_then(|n| n.parse::<u32>().ok())
            .filter(|n| (1..999).contains(n))
            .ok_or("update_metadata_invalid")?
    };
    if !version.build.is_empty() || version.major > 20 || version.minor > 99 || version.patch > 999
    {
        return Err("update_metadata_invalid".into());
    }
    Ok(
        (version.major * 100000000 + version.minor * 1000000 + version.patch * 1000) as u32
            + preview,
    )
}

// Both platforms use the historical compatibility feed as their stable channel.
// Filtering here defends new clients; the publisher must also protect old clients.
pub fn stable_update_eligible(installed: &semver::Version, next: &semver::Version) -> bool {
    next.pre.is_empty()
        && next.build.is_empty()
        && next > installed
        && matches!((version_code(installed), version_code(next)), (Ok(current), Ok(candidate)) if candidate > current)
}

pub fn interpret(body: &str, current: &str) -> Result<Option<AndroidUpdate>, String> {
    let manifest: Manifest = serde_json::from_str(body).map_err(|_| "update_metadata_invalid")?;
    let version =
        semver::Version::parse(&manifest.version).map_err(|_| "update_metadata_invalid")?;
    let installed = semver::Version::parse(current).map_err(|_| "update_metadata_invalid")?;
    let next_code = version_code(&version)?;
    version_code(&installed)?;
    if !stable_update_eligible(&installed, &version) {
        return Ok(None);
    }
    let asset = manifest
        .android
        .get("aarch64")
        .ok_or("update_metadata_invalid")?;
    let expected_url = format!("{RELEASE_ROOT}{}/Aegis-Android-arm64.apk", manifest.version);
    if asset.url != expected_url || asset.version_code != next_code {
        return Err("update_asset_mismatch".into());
    }
    Ok(Some(AndroidUpdate {
        version: manifest.version,
        url: asset.url.clone(),
        version_code: next_code,
    }))
}

fn validate_feed_status(status: reqwest::StatusCode) -> Result<(), String> {
    if status == reqwest::StatusCode::NOT_FOUND {
        return Err("update_feed_missing".into());
    }
    if !status.is_success() {
        return Err("update_feed_unavailable".into());
    }
    Ok(())
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
        .map_err(|_| "update_feed_unavailable")?;
    let mut response = client
        .get(FEED)
        .header(reqwest::header::CACHE_CONTROL, "no-cache")
        .send()
        .await
        .map_err(|error| {
            if error.is_timeout() {
                "update_timeout"
            } else {
                "update_feed_unavailable"
            }
        })?;
    // A missing published channel must never be presented as "up to date".
    validate_feed_status(response.status())?;
    let mut bytes = Vec::new();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|_| "update_feed_unavailable")?
    {
        if bytes.len() + chunk.len() > 65536 {
            return Err("update_metadata_invalid".into());
        }
        bytes.extend_from_slice(&chunk);
    }
    let body = std::str::from_utf8(&bytes).map_err(|_| "update_metadata_invalid")?;
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
    fn missing_or_failed_channel_is_never_reported_as_up_to_date() {
        assert_eq!(
            validate_feed_status(reqwest::StatusCode::NOT_FOUND).unwrap_err(),
            "update_feed_missing"
        );
        assert_eq!(
            validate_feed_status(reqwest::StatusCode::SERVICE_UNAVAILABLE).unwrap_err(),
            "update_feed_unavailable"
        );
        assert!(validate_feed_status(reqwest::StatusCode::OK).is_ok());
    }
    #[test]
    fn accepts_only_newer_correctly_versioned_official_stable_apk() {
        let url = format!("{RELEASE_ROOT}0.7.2/Aegis-Android-arm64.apk");
        let stable = manifest("0.7.2", 7002999, &url);
        assert!(interpret(&stable, "0.7.1").unwrap().is_some());
        assert!(interpret(&stable, "0.7.2-unstable.1").unwrap().is_some());
        for current in ["0.7.2", "0.7.3", "0.8.0-unstable.1"] {
            assert!(interpret(&stable, current).unwrap().is_none());
        }
        assert!(interpret(&manifest("0.7.2", 7002001, &url), "0.7.1").is_err());
        assert!(interpret(
            &manifest("0.7.2", 7002999, "https://evil.example/app.apk"),
            "0.7.1"
        )
        .is_err());
        assert!(interpret("not json", "0.7.1").is_err());
    }
    #[test]
    fn stable_feed_never_offers_previews_even_to_installed_previews() {
        for version in ["0.7.2-unstable.1", "0.7.2-stage.1"] {
            let parsed = semver::Version::parse(version).unwrap();
            let url = format!("{RELEASE_ROOT}{version}/Aegis-Android-arm64.apk");
            let preview = manifest(version, version_code(&parsed).unwrap(), &url);
            for current in ["0.7.1", "0.7.2-unstable.1", "0.7.0-stage.10"] {
                assert!(interpret(&preview, current).unwrap().is_none());
            }
        }
    }
    #[test]
    fn legacy_preview_allocations_still_parse_for_migration_to_stable() {
        let versions = [
            "0.7.0-stage.10",
            "0.7.0-unstable.11",
            "0.7.0-unstable.19",
            "0.7.0",
        ];
        for pair in versions.windows(2) {
            let installed = semver::Version::parse(pair[0]).unwrap();
            let next = semver::Version::parse(pair[1]).unwrap();
            assert!(next > installed);
            assert!(version_code(&next).unwrap() > version_code(&installed).unwrap());
        }
    }
    #[test]
    fn stable_upgrade_from_preview19_and_installed_stable_is_current() {
        let url = format!("{RELEASE_ROOT}0.7.0/Aegis-Android-arm64.apk");
        let stable = manifest("0.7.0", 7000999, &url);
        let update = interpret(&stable, "0.7.0-unstable.19").unwrap().unwrap();
        assert_eq!(update.version_code, 7000999);
        assert_eq!(update.version, "0.7.0");
        assert!(interpret(&stable, "0.7.0").unwrap().is_none());
    }
    #[test]
    fn rejects_unallocated_versions() {
        for value in [
            "0.7.0-stage.999",
            "0.7.0-beta.1",
            "0.7.0-unstable.999",
            "0.7.0-unstable.11.extra",
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
    fn configured_windows_comparator_rejects_preview_and_accepts_stable_promotion() {
        for (current, next, eligible) in [
            ("0.7.1", "0.7.2-unstable.1", false),
            ("0.7.2-unstable.1", "0.7.2", true),
            ("0.7.1", "0.7.2", true),
            ("0.7.2", "0.7.2", false),
            ("0.7.2", "0.7.1", false),
        ] {
            let release: tauri_plugin_updater::RemoteRelease = serde_json::from_value(serde_json::json!({
                "version": next,
                "platforms": { "windows-x86_64": { "url": format!("https://github.com/phcastello/Aegis/releases/download/node-v{next}/Aegis-Windows-x86_64-Setup.exe"), "signature": "fixture" } }
            })).unwrap();
            assert_eq!(
                super::stable_update_eligible(
                    &semver::Version::parse(current).unwrap(),
                    &release.version
                ),
                eligible
            );
        }
    }
    #[test]
    fn official_desktop_updater_orders_unstable_above_published_stage10() {
        let release: tauri_plugin_updater::RemoteRelease = serde_json::from_value(serde_json::json!({
            "version": "0.7.0-unstable.11",
            "platforms": { "windows-x86_64": { "url": "https://github.com/phcastello/Aegis/releases/download/node-v0.7.0-unstable.11/Aegis-Windows-x86_64-Setup.exe", "signature": "fixture" } }
        })).unwrap();
        assert!(release.version > semver::Version::parse("0.7.0-stage.10").unwrap());
    }
    #[test]
    fn official_desktop_updater_accepts_stable_after_preview19_and_rejects_downgrade() {
        let release: tauri_plugin_updater::RemoteRelease = serde_json::from_value(serde_json::json!({
            "version": "0.7.0",
            "platforms": { "windows-x86_64": { "url": "https://github.com/phcastello/Aegis/releases/download/node-v0.7.0/Aegis-Windows-x86_64-Setup.exe", "signature": "fixture" } }
        })).unwrap();
        let preview = semver::Version::parse("0.7.0-unstable.19").unwrap();
        let installed = semver::Version::parse("0.7.0").unwrap();
        assert!(release.version > preview);
        assert_eq!(release.version, installed);
        assert!(preview < installed);
    }
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
