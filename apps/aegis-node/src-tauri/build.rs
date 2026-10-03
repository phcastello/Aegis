fn main() {
    const KEY: &str = "AEGIS_NODE_API_BASE_URL";
    println!("cargo:rerun-if-env-changed={KEY}");
    println!("cargo:rerun-if-changed=../.env.local");

    // Shell/CI configuration wins. Only this app's .env.local is read.
    let local = std::path::Path::new("../.env.local");
    let value = std::env::var(KEY).ok().or_else(|| {
        if !local.exists() {
            return None;
        }
        dotenvy::from_path_iter(local)
            .expect("cannot read apps/aegis-node/.env.local")
            .map(|entry| entry.expect("invalid apps/aegis-node/.env.local"))
            .find_map(|(key, value)| (key == KEY).then_some(value))
    });
    let value = value.unwrap_or_else(|| "https://aegis.phcastello.com".to_owned());
    assert!(
        !value.contains(['\r', '\n']),
        "API base URL must be a single line"
    );
    println!("cargo:rustc-env={KEY}={value}");

    #[cfg(feature = "native-runtime")]
    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(
        tauri_build::AppManifest::new().commands(&[
            "runtime_info",
            "check_backend",
            "check_android_update",
            "node_status",
            "node_transport_status",
            "node_transport_reconnect",
            "node_pair",
            "node_list",
            "node_rename",
            "node_set_enabled",
            "node_set_target_priority",
            "node_resolve_target",
            "node_revoke",
            "node_create_pairing_code",
        ]),
    ))
    .expect("failed to build Tauri application manifest");
}
