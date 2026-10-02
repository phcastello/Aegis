#[derive(Debug, PartialEq, serde::Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Platform {
    Android,
    Windows,
    Unsupported,
}

pub fn current() -> Platform {
    if cfg!(target_os = "android") {
        Platform::Android
    } else if cfg!(target_os = "windows") {
        Platform::Windows
    } else {
        Platform::Unsupported
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn platform_contract_uses_lowercase_native_identifiers() {
        assert_eq!(
            serde_json::to_string(&Platform::Android).unwrap(),
            "\"android\""
        );
        assert_eq!(
            serde_json::to_string(&Platform::Windows).unwrap(),
            "\"windows\""
        );
    }

    #[test]
    fn current_platform_matches_compilation_target() {
        let expected = if cfg!(target_os = "android") {
            Platform::Android
        } else if cfg!(target_os = "windows") {
            Platform::Windows
        } else {
            Platform::Unsupported
        };
        assert_eq!(current(), expected);
    }
}
