#[cfg(feature = "native-runtime")]
#[path = "hands_free_acl.rs"]
mod hands_free_acl;

fn main() {
    println!("cargo:rerun-if-changed=tauri.conf.json");
    let config: serde_json::Value = serde_json::from_str(
        &std::fs::read_to_string("tauri.conf.json").expect("Tauri configuration"),
    )
    .expect("valid Tauri configuration");
    let code = config["bundle"]["android"]["versionCode"]
        .as_u64()
        .filter(|value| (1..=2_100_000_000).contains(value))
        .expect("Android installation code must be within the supported range");
    println!("cargo:rustc-env=AEGIS_INSTALLATION_CODE={code}");
    for key in ["AEGIS_SOURCE_COMMIT", "AEGIS_SOURCE_VERSION"] {
        println!("cargo:rerun-if-env-changed={key}");
        if let Ok(value) = std::env::var(key) {
            assert!(
                !value.contains(['\r', '\n']),
                "Source provenance must be a single line"
            );
            println!("cargo:rustc-env={key}={value}");
        }
    }

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
    if std::env::var_os("CARGO_FEATURE_ANDROID_FRAMEWORK_FIXTURE").is_some() {
        assert_eq!(
            std::env::var("TARGET").as_deref(),
            Ok("x86_64-linux-android"),
            "Framework fixture is restricted to Android x86_64 emulator APKs"
        );
        assert_eq!(
            value, "http://127.0.0.1:18104",
            "Framework fixture must use its isolated forwarded listener"
        );
    }
    assert!(
        !value.contains(['\r', '\n']),
        "API base URL must be a single line"
    );
    println!("cargo:rustc-env={KEY}={value}");

    #[cfg(feature = "native-runtime")]
    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(
        tauri_build::AppManifest::new().commands(&[
            "runtime_info",
            "node_hands_free",
            "check_backend",
            "check_android_update",
            "prepare_windows_update",
            "install_windows_update",
            "node_status",
            "node_test_notification",
            "node_exit",
            "node_notification_settings",
            "node_request_notification_permission",
            "node_set_autostart",
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
            "node_set_reminders",
            "node_remove",
            "node_reminders",
            "node_places",
            "node_location",
            "node_geofences",
            "node_settings_open",
            "node_hide_keyboard",
            "node_read_visibility",
            "node_consume_reminder_open",
            "install_android_update",
        ]),
    ))
    .expect("failed to build Tauri application manifest");

    #[cfg(feature = "native-runtime")]
    {
        let root = std::env::current_dir().expect("build root");
        let target =
            tauri_utils::platform::Target::from_triple(&std::env::var("TARGET").expect("target"));
        let (mut config, _) =
            tauri_utils::config::parse::read_from(target, &root).expect("effective Tauri config");
        if let Ok(overlay) = std::env::var("TAURI_CONFIG") {
            hands_free_acl::merge_config(
                &mut config,
                &serde_json::from_str(&overlay).expect("Tauri config overlay"),
            );
        }
        let config = serde_json::from_value(config).expect("typed Tauri config");
        let out = std::path::PathBuf::from(std::env::var_os("OUT_DIR").expect("build output"));
        let manifests = serde_json::from_str(
            &std::fs::read_to_string(out.join("acl-manifests.json"))
                .expect("generated ACL manifests"),
        )
        .expect("ACL manifest JSON");
        let capabilities = serde_json::from_str(
            &std::fs::read_to_string(out.join("capabilities.json"))
                .expect("generated capabilities"),
        )
        .expect("capability JSON");
        hands_free_acl::verify(&config, &manifests, capabilities, target)
            .expect("hands-free effective ACL contract");
        // These are resolved permissions, not merely the discovered capability files.
        let evidence = serde_json::json!({"schema":1,"target":target,"command":"node_hands_free","window":"main","localOnly":true,"allowed":target==tauri_utils::platform::Target::Android,"resolver":"tauri_utils::acl::get_capabilities + Resolved::resolve"});
        std::fs::write(out.join("hands-free-acl.json"), evidence.to_string())
            .expect("ACL evidence");
    }
}
