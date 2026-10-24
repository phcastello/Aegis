use serde::{Deserialize, Serialize};

// A single recommended distribution, updated by an atomic Git ref move after verification.
pub const FEED: &str =
    "https://raw.githubusercontent.com/phcastello/Aegis/aegis-distribution/latest.json";
const RELEASE_ROOT: &str = "https://github.com/phcastello/Aegis/releases/download/node-v";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Manifest {
    schema: u32,
    version: String,
    version_code: u32,
    kind: String,
    source_commit: String,
    source_version: String,
    published_at: String,
    #[serde(rename = "pub_date")]
    pub_date: String,
    rollback_of: Option<String>,
    restores_version: Option<String>,
    compatibility: Compatibility,
    android: std::collections::HashMap<String, AndroidAsset>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Compatibility {
    api: u32,
    node_data: u32,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct AndroidAsset {
    url: String,
    version_code: u32,
    sha256: String,
}
#[derive(Serialize, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AndroidUpdate {
    pub version: String,
    pub url: String,
    pub version_code: u32,
    pub sha256: String,
    pub kind: String,
    pub source_commit: String,
    pub source_version: String,
    pub published_at: String,
    pub rollback_of: Option<String>,
    pub restores_version: Option<String>,
}

pub fn installation_code() -> u32 {
    env!("AEGIS_INSTALLATION_CODE")
        .parse()
        .expect("validated installation code")
}

fn positive(value: &str) -> bool {
    value.parse::<u32>().is_ok_and(|n| (1..=65535).contains(&n))
}

// The version identifies the distribution, never the Android installation counter.
pub fn supported_version(version: &semver::Version) -> bool {
    if !version.build.is_empty() || version.major > 20 || version.minor > 99 || version.patch > 999
    {
        return false;
    }
    let parts = version.pre.as_str().split('.').collect::<Vec<_>>();
    version.pre.is_empty()
        || match parts.as_slice() {
            ["unstable", n] | ["rollback", n] => positive(n),
            ["unstable", n, "rollback", recovery] => positive(n) && positive(recovery),
            _ => false,
        }
}

pub fn recommended_update_eligible(installed: &semver::Version, next: &semver::Version) -> bool {
    supported_version(next) && next > installed
}

pub fn verify_installer_hash(bytes: &[u8], expected: &str) -> Result<(), String> {
    use sha2::{Digest, Sha256};
    if !valid_hex(expected, 64) || format!("{:x}", Sha256::digest(bytes)) != expected {
        return Err("update_archive_invalid".into());
    }
    Ok(())
}

fn valid_hex(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
}

fn parsed(value: &str) -> Result<semver::Version, String> {
    semver::Version::parse(value).map_err(|_| "update_metadata_invalid".to_owned())
}

fn valid_rollback(version: &semver::Version, previous: &semver::Version) -> bool {
    let parts = previous.pre.as_str().split('.').collect::<Vec<_>>();
    let suffix = match parts.as_slice() {
        ["unstable", n] if positive(n) => format!("unstable.{n}.rollback.1"),
        ["unstable", n, "rollback", m] if positive(n) && positive(m) => {
            let Some(next) = m.parse::<u32>().ok().and_then(|n| n.checked_add(1)) else {
                return false;
            };
            format!("unstable.{n}.rollback.{next}")
        }
        ["rollback", m] if positive(m) => {
            let Some(next) = m.parse::<u32>().ok().and_then(|n| n.checked_add(1)) else {
                return false;
            };
            format!("rollback.{next}")
        }
        _ if previous.pre.is_empty() => {
            return version.major == previous.major
                && version.minor == previous.minor
                && previous.patch.checked_add(1) == Some(version.patch)
                && version.pre.as_str() == "rollback.1";
        }
        _ => return false,
    };
    version.major == previous.major
        && version.minor == previous.minor
        && version.patch == previous.patch
        && version.pre.as_str() == suffix
}

pub fn interpret(
    body: &str,
    current: &str,
    installed_code: u32,
) -> Result<Option<AndroidUpdate>, String> {
    let manifest: Manifest = serde_json::from_str(body).map_err(|_| "update_metadata_invalid")?;
    let version = parsed(&manifest.version)?;
    let installed = parsed(current)?;
    let source = parsed(&manifest.source_version)?;
    if manifest.schema != 2
        || !supported_version(&version)
        || !source.build.is_empty()
        || !valid_hex(&manifest.source_commit, 40)
        || manifest.version_code == 0
        || manifest.version_code > 2_100_000_000
        || manifest.pub_date != manifest.published_at
        || chrono::DateTime::parse_from_rfc3339(&manifest.published_at).is_err()
        || manifest.compatibility.api != 1
        || manifest.compatibility.node_data != 1
    {
        return Err("update_metadata_invalid".into());
    }
    let recovery = version.pre.as_str().contains("rollback");
    match manifest.kind.as_str() {
        "normal"
            if !recovery
                && manifest.rollback_of.is_none()
                && manifest.restores_version.is_none() => {}
        "rollback" if recovery => {
            let previous = parsed(
                manifest
                    .rollback_of
                    .as_deref()
                    .ok_or("update_metadata_invalid")?,
            )?;
            let restores = parsed(
                manifest
                    .restores_version
                    .as_deref()
                    .ok_or("update_metadata_invalid")?,
            )?;
            if previous >= version || restores >= previous || !valid_rollback(&version, &previous) {
                return Err("update_metadata_invalid".into());
            }
        }
        _ => return Err("update_metadata_invalid".into()),
    }
    let asset = manifest
        .android
        .get("aarch64")
        .ok_or("update_metadata_invalid")?;
    let expected_url = format!("{RELEASE_ROOT}{}/Aegis-Android-arm64.apk", manifest.version);
    if asset.url != expected_url
        || asset.version_code != manifest.version_code
        || !valid_hex(&asset.sha256, 64)
    {
        return Err("update_asset_mismatch".into());
    }
    if !recommended_update_eligible(&installed, &version) || manifest.version_code <= installed_code
    {
        return Ok(None);
    }
    Ok(Some(AndroidUpdate {
        version: manifest.version,
        url: asset.url.clone(),
        version_code: manifest.version_code,
        sha256: asset.sha256.clone(),
        kind: manifest.kind,
        source_commit: manifest.source_commit,
        source_version: manifest.source_version,
        published_at: manifest.published_at,
        rollback_of: manifest.rollback_of,
        restores_version: manifest.restores_version,
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
        // The raw feed is a fixed origin. APK redirects are checked separately by Android.
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|_| "update_feed_unavailable")?;
    let body = fetch_manifest(&client, FEED).await?;
    interpret(&body, current, installation_code())
}

async fn fetch_manifest(client: &reqwest::Client, url: &str) -> Result<String, String> {
    let mut response = client
        .get(url)
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
    String::from_utf8(bytes).map_err(|_| "update_metadata_invalid".into())
}

#[cfg(test)]
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
#[cfg(test)]
fn stable_update_eligible(installed: &semver::Version, next: &semver::Version) -> bool {
    next.pre.is_empty()
        && next.build.is_empty()
        && next > installed
        && matches!((version_code(installed), version_code(next)), (Ok(current), Ok(candidate)) if candidate > current)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn manifest(version: &str, code: u32) -> serde_json::Value {
        serde_json::json!({
            "schema":2,"version":version,"versionCode":code,"kind":"normal",
            "sourceCommit":"a".repeat(40),"sourceVersion":"0.7.2","publishedAt":"2026-10-23T12:00:00Z","pub_date":"2026-10-23T12:00:00Z",
            "compatibility":{"api":1,"nodeData":1},
            "android":{"aarch64":{"url":format!("{RELEASE_ROOT}{version}/Aegis-Android-arm64.apk"),"versionCode":code,"sha256":"b".repeat(64)}}
        })
    }
    fn recovery(version: &str, previous: &str, restores: &str, code: u32) -> serde_json::Value {
        let mut value = manifest(version, code);
        value["kind"] = "rollback".into();
        value["rollbackOf"] = previous.into();
        value["restoresVersion"] = restores.into();
        value
    }
    #[test]
    fn previews_stables_and_rollbacks_follow_the_same_flow() {
        for (next, current, code, installed) in [
            ("0.7.2-unstable.2", "0.7.1", 7002002, 7001999),
            ("0.7.2", "0.7.2-unstable.2", 7002999, 7002002),
            ("0.7.3-unstable.1", "0.7.3-rollback.1", 7003002, 7003001),
        ] {
            assert!(
                interpret(&manifest(next, code).to_string(), current, installed)
                    .unwrap()
                    .is_some()
            );
        }
        for value in [
            recovery(
                "0.7.2-unstable.5.rollback.1",
                "0.7.2-unstable.5",
                "0.7.2-unstable.4",
                7002006,
            ),
            recovery(
                "0.7.2-unstable.5.rollback.2",
                "0.7.2-unstable.5.rollback.1",
                "0.7.2-unstable.4",
                7002007,
            ),
            recovery("0.7.3-rollback.1", "0.7.2", "0.7.2-unstable.4", 7003000),
            recovery(
                "0.7.3-rollback.2",
                "0.7.3-rollback.1",
                "0.7.2-unstable.4",
                7003001,
            ),
        ] {
            let previous = value["rollbackOf"].as_str().unwrap();
            assert!(interpret(&value.to_string(), previous, 7002005)
                .unwrap()
                .is_some());
        }
    }
    #[test]
    fn installed_distribution_or_lower_semver_or_installation_code_is_never_offered() {
        for (version, code) in [
            ("0.7.2-unstable.4", 7002007),
            ("0.7.2-unstable.5", 7002007),
            ("0.7.2-unstable.6", 7002005),
        ] {
            assert!(interpret(
                &manifest(version, code).to_string(),
                "0.7.2-unstable.5",
                7002005
            )
            .unwrap()
            .is_none());
        }
    }
    #[test]
    fn independent_installation_code_is_preserved() {
        let value = recovery(
            "0.7.2-unstable.5.rollback.1",
            "0.7.2-unstable.5",
            "0.7.2-unstable.4",
            7002100,
        );
        assert_eq!(
            interpret(&value.to_string(), "0.7.2-unstable.5", 7002099)
                .unwrap()
                .unwrap()
                .version_code,
            7002100
        );
    }
    #[test]
    fn inconsistent_or_untrusted_metadata_is_rejected() {
        let original = manifest("0.7.2-unstable.2", 7002002);
        for (field, value) in [
            ("schema", serde_json::json!(1)),
            ("versionCode", serde_json::json!(7002003)),
            ("kind", serde_json::json!("rollback")),
            ("sourceCommit", serde_json::json!("short")),
            ("publishedAt", serde_json::json!("today")),
            ("sourceVersion", serde_json::json!("invalid")),
            ("compatibility", serde_json::json!({"api":2,"nodeData":1})),
        ] {
            let mut bad = original.clone();
            bad[field] = value;
            assert!(
                interpret(&bad.to_string(), "0.7.1", 7001999).is_err(),
                "{field}"
            );
        }
        for (field, value) in [("url", "https://evil.example/app.apk"), ("sha256", "bad")] {
            let mut bad = original.clone();
            bad["android"]["aarch64"][field] = value.into();
            assert!(interpret(&bad.to_string(), "0.7.1", 7001999).is_err());
        }
        for bad in ["{}", "not json"] {
            assert!(interpret(bad, "0.7.1", 7001999).is_err());
        }
        assert!(interpret(
            &recovery(
                "0.7.2-unstable.5.rollback.1",
                "0.7.2-unstable.6",
                "0.7.2-unstable.4",
                7002007
            )
            .to_string(),
            "0.7.1",
            7001999
        )
        .is_err());
        assert!(interpret(
            &recovery("0.7.3-rollback.1", "0.7.1", "0.7.0", 7003001).to_string(),
            "0.7.1",
            7001999
        )
        .is_err());
    }
    #[test]
    fn semantic_order_matches_the_recovery_contract() {
        for sequence in [
            vec![
                "0.7.2-unstable.4",
                "0.7.2-unstable.5",
                "0.7.2-unstable.5.rollback.1",
                "0.7.2-unstable.5.rollback.2",
                "0.7.2-unstable.6",
                "0.7.2",
            ],
            vec![
                "0.7.2",
                "0.7.3-rollback.1",
                "0.7.3-rollback.2",
                "0.7.3-unstable.1",
                "0.7.3",
            ],
        ] {
            for pair in sequence.windows(2) {
                assert!(recommended_update_eligible(
                    &parsed(pair[0]).unwrap(),
                    &parsed(pair[1]).unwrap()
                ));
            }
        }
        for invalid in [
            "0.7.2-unstable.0",
            "0.7.2-unstable.1.rollback.1.rollback.2",
            "0.7.2+rollback.1",
            "0.7.2-rollback.0",
        ] {
            assert!(!supported_version(&parsed(invalid).unwrap()));
        }
    }
    #[test]
    fn historical_allocations_and_legacy_feed_stay_stable_only() {
        for (version, code) in [
            ("0.7.0-stage.10", 7000010),
            ("0.7.0-unstable.19", 7000019),
            ("0.7.1", 7001999),
        ] {
            assert_eq!(version_code(&parsed(version).unwrap()).unwrap(), code);
        }
        assert!(!stable_update_eligible(
            &parsed("0.7.1").unwrap(),
            &parsed("0.7.2-unstable.2").unwrap()
        ));
        assert!(stable_update_eligible(
            &parsed("0.7.0-stage.10").unwrap(),
            &parsed("0.7.2").unwrap()
        ));
    }
    #[tokio::test]
    async fn failed_network_invalid_and_missing_manifests_are_safe_errors() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        async fn server(response: String, delay: std::time::Duration) -> String {
            let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
            let address = listener.local_addr().unwrap();
            tokio::spawn(async move {
                let (mut stream, _) = listener.accept().await.unwrap();
                let mut request = [0; 2048];
                let _ = stream.read(&mut request).await;
                tokio::time::sleep(delay).await;
                let _ = stream.write_all(response.as_bytes()).await;
            });
            format!("http://{address}/latest.json")
        }
        let client = reqwest::Client::builder()
            .timeout(std::time::Duration::from_millis(100))
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .unwrap();
        for (status, expected) in [
            (404, "update_feed_missing"),
            (503, "update_feed_unavailable"),
            (302, "update_feed_unavailable"),
        ] {
            let url = server(
                format!("HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
                std::time::Duration::ZERO,
            )
            .await;
            assert_eq!(fetch_manifest(&client, &url).await.unwrap_err(), expected);
        }
        let url = server(
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n".into(),
            std::time::Duration::from_secs(1),
        )
        .await;
        assert_eq!(
            fetch_manifest(&client, &url).await.unwrap_err(),
            "update_timeout"
        );
        let body = "x".repeat(65537);
        let url = server(
            format!(
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                body.len()
            ),
            std::time::Duration::ZERO,
        )
        .await;
        assert_eq!(
            fetch_manifest(&client, &url).await.unwrap_err(),
            "update_metadata_invalid"
        );
        let url = server(
            "HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nnot json".into(),
            std::time::Duration::ZERO,
        )
        .await;
        let invalid = fetch_manifest(&client, &url).await.unwrap();
        assert!(interpret(&invalid, "0.7.1", 7001999).is_err());
    }
    #[test]
    fn installer_hash_binds_the_exact_downloaded_signed_bytes() {
        let expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        assert!(verify_installer_hash(b"abc", expected).is_ok());
        assert!(verify_installer_hash(b"tampered", expected).is_err());
        assert!(verify_installer_hash(b"abc", &expected.to_uppercase()).is_err());
        assert!(verify_installer_hash(b"abc", "missing").is_err());
    }
    #[test]
    fn missing_and_failed_feed_are_not_current() {
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
}

#[cfg(all(test, windows, feature = "native-runtime"))]
mod desktop_tests {
    #[test]
    fn official_tauri_parser_preserves_rollback_semver_order() {
        for (current, next) in [
            ("0.7.1", "0.7.2-unstable.1"),
            ("0.7.2-unstable.5", "0.7.2-unstable.5.rollback.1"),
            ("0.7.2", "0.7.3-rollback.1"),
            ("0.7.3-rollback.1", "0.7.3-unstable.1"),
        ] {
            let release: tauri_plugin_updater::RemoteRelease = serde_json::from_value(serde_json::json!({"version":next,"platforms":{"windows-x86_64":{"url":format!("{}/Aegis-Windows-x86_64-Setup.exe",format!("{}{}",super::RELEASE_ROOT,next)),"signature":"fixture"}}})).unwrap();
            assert!(super::recommended_update_eligible(
                &semver::Version::parse(current).unwrap(),
                &release.version
            ));
        }
    }
}
