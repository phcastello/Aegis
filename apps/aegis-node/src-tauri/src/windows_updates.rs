// Preserve the official updater's signature/version verification, then bind the
// same downloaded bytes to the shared distribution hash before native install.
use crate::updates;
use crate::WindowsDownloadProgress;
use tauri::{ipc::Channel, Manager, Resource, ResourceId, Webview};
use tauri_plugin_updater::Update;

struct VerifiedBytes {
    update_rid: ResourceId,
    version: String,
    sha256: String,
    bytes: Vec<u8>,
}
impl Resource for VerifiedBytes {}

fn verified_metadata(update: &Update) -> Result<String, String> {
    let candidate = updates::interpret(
        &update.raw_json.to_string(),
        env!("CARGO_PKG_VERSION"),
        updates::installation_code(),
    )?
    .ok_or("update_asset_mismatch")?;
    let platform = &update.raw_json["platforms"]["windows-x86_64"];
    let expected = format!("https://github.com/phcastello/Aegis/releases/download/node-v{}/Aegis-Windows-x86_64-Setup.exe",candidate.version);
    if candidate.version != update.version
        || update.current_version != env!("CARGO_PKG_VERSION")
        || update.download_url.as_str() != expected
        || platform["url"].as_str() != Some(expected.as_str())
        || platform["signature"].as_str() != Some(update.signature.as_str())
        || update.signature.trim().is_empty()
    {
        return Err("update_asset_mismatch".into());
    }
    let hash = platform["sha256"]
        .as_str()
        .ok_or("update_metadata_invalid")?;
    if hash.len() != 64
        || !hash
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
    {
        return Err("update_metadata_invalid".into());
    }
    Ok(hash.to_owned())
}

pub async fn prepare(
    webview: Webview,
    update_rid: ResourceId,
    on_progress: Channel<WindowsDownloadProgress>,
) -> Result<ResourceId, String> {
    let update = webview
        .resources_table()
        .get::<Update>(update_rid)
        .map_err(|_| "update_asset_mismatch")?;
    let sha256 = verified_metadata(&update)?;
    let mut received = 0u64;
    let bytes = update
        .download(
            |chunk, total| {
                received = received.saturating_add(chunk as u64);
                let _ = on_progress.send(WindowsDownloadProgress { received, total });
            },
            || {},
        )
        .await
        .map_err(|error| match error {
            tauri_plugin_updater::Error::Minisign(_)
            | tauri_plugin_updater::Error::Base64(_)
            | tauri_plugin_updater::Error::SignatureUtf8(_)
            | tauri_plugin_updater::Error::MissingSignedVersion
            | tauri_plugin_updater::Error::SignedVersionMismatch { .. } => {
                "update_signature_mismatch"
            }
            _ => "update_download_failed",
        })?;
    updates::verify_installer_hash(&bytes, &sha256)?;
    Ok(webview.resources_table().add(VerifiedBytes {
        update_rid,
        version: update.version.clone(),
        sha256,
        bytes,
    }))
}

pub fn install(
    webview: Webview,
    update_rid: ResourceId,
    bytes_rid: ResourceId,
) -> Result<(), String> {
    let update = webview
        .resources_table()
        .get::<Update>(update_rid)
        .map_err(|_| "update_asset_mismatch")?;
    let bytes = webview
        .resources_table()
        .get::<VerifiedBytes>(bytes_rid)
        .map_err(|_| "update_archive_invalid")?;
    let hash = verified_metadata(&update)?;
    if bytes.update_rid != update_rid || bytes.version != update.version || bytes.sha256 != hash {
        return Err("update_asset_mismatch".into());
    }
    // VerifiedBytes is immutable and private to this WebView's resource table.
    let result = update
        .install(&bytes.bytes)
        .map_err(|_| "update_installer_failed".to_owned());
    if result.is_ok() {
        let _ = webview.resources_table().close(bytes_rid);
    }
    result
}
