use url::{Host, Url};

pub struct Endpoint(Url);

impl Endpoint {
    pub fn parse(raw: &str, allow_http: bool, android: bool) -> Result<Self, &'static str> {
        let raw = raw.trim();
        if raw.is_empty() {
            return Err("Set AEGIS_NODE_API_BASE_URL and rebuild the app.");
        }
        let url = Url::parse(raw).map_err(|_| "Invalid API base URL.")?;
        if !matches!(url.scheme(), "http" | "https") || url.host().is_none() {
            return Err("API base URL must be an absolute HTTP or HTTPS origin.");
        }
        if !url.username().is_empty() || url.password().is_some() {
            return Err("API base URL must not contain credentials.");
        }
        if url.path() != "/" || url.query().is_some() || url.fragment().is_some() {
            return Err(
                "API base URL must contain only the origin, without /api, query or fragment.",
            );
        }
        let host = url.host().expect("validated URL host");
        let (loopback, unspecified) = match host {
            Host::Ipv4(ip) => (ip.is_loopback(), ip.is_unspecified()),
            Host::Ipv6(ip) => (ip.is_loopback(), ip.is_unspecified()),
            Host::Domain(name) => (
                name.eq_ignore_ascii_case("localhost") || name.ends_with(".localhost"),
                false,
            ),
        };
        if unspecified {
            return Err("Use a reachable backend hostname, not a wildcard bind address.");
        }
        if android && loopback {
            return Err(
                "Android localhost points to the device. Configure a reachable backend origin.",
            );
        }
        if !allow_http && url.scheme() == "http" {
            return Err("Release builds require an HTTPS backend origin.");
        }
        Ok(Self(url))
    }

    pub fn base_url(&self) -> String {
        self.0.as_str().trim_end_matches('/').to_owned()
    }

    pub fn health_url(&self) -> Url {
        let mut url = self.0.clone();
        url.set_path("/api/health");
        url
    }
}

#[cfg(feature = "native-runtime")]
pub fn configured_endpoint() -> Result<Endpoint, &'static str> {
    Endpoint::parse(
        env!("AEGIS_NODE_API_BASE_URL"),
        cfg!(debug_assertions),
        cfg!(target_os = "android"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_origin_and_uses_existing_health_route() {
        let endpoint = Endpoint::parse("  HTTPS://backend.example:8443/  ", false, true).unwrap();
        assert_eq!(endpoint.base_url(), "https://backend.example:8443");
        assert_eq!(
            endpoint.health_url().as_str(),
            "https://backend.example:8443/api/health"
        );
    }

    #[test]
    fn rejects_missing_invalid_or_non_origin_configuration() {
        for raw in [
            "",
            "  ",
            "backend.example",
            "/api",
            "file:///tmp/api",
            "ftp://backend.example",
            "https://backend.example/api",
            "https://backend.example/?token=x",
            "https://backend.example/#fragment",
            "https://user:secret@backend.example",
            "https://user@backend.example",
            "http://0.0.0.0:8090",
            "http://[::]:8090",
        ] {
            assert!(Endpoint::parse(raw, true, false).is_err(), "accepted {raw}");
        }
    }

    #[test]
    fn android_rejects_loopback_without_rewriting_it() {
        for raw in [
            "http://localhost:8090",
            "http://127.0.0.1:8090",
            "http://127.1:8090",
            "http://[::1]:8090",
            "https://api.localhost",
        ] {
            assert!(Endpoint::parse(raw, true, true).is_err(), "accepted {raw}");
        }
        assert!(Endpoint::parse("http://backend.lan:8090", true, true).is_ok());
        assert!(Endpoint::parse("http://localhost:8090", true, false).is_ok());
    }

    #[test]
    fn http_is_debug_only() {
        assert!(Endpoint::parse("http://backend.lan:8090", false, true).is_err());
        assert!(Endpoint::parse("https://backend.example", false, true).is_ok());
    }
}
