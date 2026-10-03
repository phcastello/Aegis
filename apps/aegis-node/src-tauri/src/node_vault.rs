use super::node_identity::{Record, Vault};
// Names are part of the persisted identity contract; preserve them across releases.
const SERVICE: &str = "com.aegis.node.identity.v1";
const USER: &str = "installation";
pub(crate) struct OsVault;
impl OsVault {
    pub fn initialize() -> Result<Self, String> {
        #[cfg(windows)]
        keyring_core::set_default_store(
            windows_native_keyring_store::Store::new().map_err(|_| storage_error())?,
        );
        #[cfg(target_os = "android")]
        keyring_core::set_default_store(
            android_native_keyring_store::Store::new_with_configuration(
                &std::collections::HashMap::from([("name", "aegis-node-identity-v1")]),
            )
            .map_err(|_| storage_error())?,
        );
        #[cfg(not(any(windows, target_os = "android")))]
        return Err("Identidade nativa está disponível somente no Windows e Android.".into());
        #[cfg(any(windows, target_os = "android"))]
        Ok(Self)
    }
    #[cfg(any(windows, target_os = "android"))]
    fn entry() -> Result<keyring_core::Entry, String> {
        // Local persistence avoids roaming an installation's identity to another Windows PC.
        #[cfg(windows)]
        let modifiers = std::collections::HashMap::from([("persistence", "Local")]);
        #[cfg(target_os = "android")]
        let modifiers = std::collections::HashMap::new();
        keyring_core::Entry::new_with_modifiers(SERVICE, USER, &modifiers)
            .map_err(|_| storage_error())
    }
}
fn storage_error() -> String {
    "O armazenamento seguro do sistema não está disponível. Nenhuma credencial será salva em arquivo comum.".into()
}
impl Vault for OsVault {
    fn load(&self) -> Result<Option<Record>, String> {
        #[cfg(any(windows, target_os = "android"))]
        match Self::entry()?.get_password() {
            Ok(value) => serde_json::from_str(&value)
                .map(Some)
                .map_err(|_| storage_error()),
            Err(keyring_core::Error::NoEntry) => Ok(None),
            Err(_) => Err(storage_error()),
        }
        #[cfg(not(any(windows, target_os = "android")))]
        Err(storage_error())
    }
    fn save(&self, record: &Record) -> Result<(), String> {
        #[cfg(any(windows, target_os = "android"))]
        {
            let value = serde_json::to_string(record).map_err(|_| storage_error())?;
            Self::entry()?
                .set_password(&value)
                .map_err(|_| storage_error())
        }
        #[cfg(not(any(windows, target_os = "android")))]
        {
            let _ = record;
            Err(storage_error())
        }
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;
    // Real Credential Manager, three different processes; no production entry is touched.
    #[test]
    fn credential_manager_survives_process_restart_and_deletion() {
        if let Ok(mode) = std::env::var("AEGIS_VAULT_TEST_MODE") {
            OsVault::initialize().unwrap();
            let service = std::env::var("AEGIS_VAULT_TEST_SERVICE").unwrap();
            let entry = keyring_core::Entry::new_with_modifiers(
                &service,
                "test",
                &std::collections::HashMap::from([("persistence", "Local")]),
            )
            .unwrap();
            match mode.as_str() {
                "write" => entry.set_password("disposable-test-value").unwrap(),
                "read" => {
                    assert_eq!(entry.get_password().unwrap(), "disposable-test-value");
                    entry.delete_credential().unwrap();
                }
                "deleted" => assert!(matches!(
                    entry.get_password(),
                    Err(keyring_core::Error::NoEntry)
                )),
                _ => panic!("Unknown test phase"),
            }
            return;
        }
        let service = format!("aegis-ci-{}", uuid::Uuid::new_v4());
        for mode in ["write", "read", "deleted"] {
            let status = std::process::Command::new(std::env::current_exe().unwrap())
                .args([
                    "--exact",
                    "node_vault::tests::credential_manager_survives_process_restart_and_deletion",
                ])
                .env("AEGIS_VAULT_TEST_MODE", mode)
                .env("AEGIS_VAULT_TEST_SERVICE", &service)
                .status()
                .unwrap();
            assert!(status.success(), "Credential Manager subprocess failed");
        }
    }
}
