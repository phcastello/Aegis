//! Build support for existing foreground interactive audio, not permission or remote delivery.
use serde::{Deserialize, Serialize};
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct NodeCapability {
    pub name: String,
    pub version: u32,
}
pub(crate) struct CapabilityRegistry;
impl CapabilityRegistry {
    // Both supported Tauri builds embed shared push-to-talk/MediaRecorder and PCM/WebAudio.
    // Unknown/unsupported builds advertise nothing. No notification/location placeholders.
    pub fn current() -> Vec<NodeCapability> {
        match crate::platform::current() {
            crate::platform::Platform::Windows | crate::platform::Platform::Android => {
                let mut capabilities = Self::audio(true, true);
                if cfg!(feature = "native-runtime") {
                    capabilities.push(NodeCapability {
                        name: "notification.show".into(),
                        version: 1,
                    });
                }
                capabilities
            }
            crate::platform::Platform::Unsupported => Vec::new(),
        }
    }
    pub fn audio(input: bool, output: bool) -> Vec<NodeCapability> {
        [("audio.input", input), ("audio.output", output)]
            .into_iter()
            .filter(|(_, supported)| *supported)
            .map(|(name, _)| NodeCapability {
                name: name.into(),
                version: 1,
            })
            .collect()
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stable_real_audio_support_has_no_future_placeholders() {
        let expected = vec![
            NodeCapability {
                name: "audio.input".into(),
                version: 1,
            },
            NodeCapability {
                name: "audio.output".into(),
                version: 1,
            },
        ];
        assert_eq!(CapabilityRegistry::audio(true, true), expected);
        assert_eq!(
            CapabilityRegistry::audio(true, true),
            CapabilityRegistry::audio(true, true)
        );
        assert_eq!(CapabilityRegistry::audio(false, true), expected[1..]);
        assert!(CapabilityRegistry::audio(false, false).is_empty());
        assert!(!serde_json::to_string(&expected)
            .unwrap()
            .contains("credential"));
    }
    #[test]
    fn actual_build_provider_matches_supported_runtime() {
        let capabilities = CapabilityRegistry::current();
        if matches!(
            crate::platform::current(),
            crate::platform::Platform::Windows | crate::platform::Platform::Android
        ) {
            assert_eq!(&capabilities[..2], CapabilityRegistry::audio(true, true));
            assert_eq!(
                capabilities.len(),
                if cfg!(feature = "native-runtime") {
                    3
                } else {
                    2
                }
            );
        } else {
            assert!(capabilities.is_empty());
        }
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RequiredCapability {
    pub name: String,
    pub minimum_version: i32,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TargetRequest {
    pub required_capabilities: Vec<RequiredCapability>,
    pub preferred_node_id: Option<String>,
}
impl TargetRequest {
    pub fn validate(&self) -> Result<(), &'static str> {
        let mut names = std::collections::HashSet::new();
        if self.required_capabilities.is_empty()
            || self.required_capabilities.len() > 32
            || self.required_capabilities.iter().any(|r| {
                !matches!(
                    r.name.as_str(),
                    "audio.input" | "audio.output" | "notification.show"
                ) || r.minimum_version < 1
                    || !names.insert(&r.name)
            })
        {
            return Err("Requisitos de capabilities inválidos.");
        }
        if let Some(id) = &self.preferred_node_id {
            if uuid::Uuid::parse_str(id).map_or(true, |id| id.is_nil()) {
                return Err("Node preferido inválido.");
            }
        }
        Ok(())
    }
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TargetNode {
    pub id: String,
    pub name: String,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TargetResult {
    pub node: Option<TargetNode>,
    pub code: Option<String>,
    pub online_nodes: u32,
    pub capability_compatible_nodes: u32,
}
