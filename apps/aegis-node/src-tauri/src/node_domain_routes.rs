//! Closed domain routes shared by portable tests and native IPC.
use serde_json::{json, Value};
fn id(value: Option<String>) -> Result<String, String> {
    let value = value.ok_or("Identificador necessário.")?;
    let uuid = uuid::Uuid::parse_str(&value).map_err(|_| "Identificador inválido.")?;
    if uuid.is_nil() {
        return Err("Identificador inválido.".into());
    }
    Ok(uuid.to_string())
}
fn revision(payload: &Value) -> Result<u64, String> {
    payload["expectedRevision"]
        .as_u64()
        .filter(|v| *v > 0)
        .ok_or_else(|| "Atualize os dados antes de alterar.".into())
}
pub(crate) fn route(
    domain: &str,
    operation: &str,
    identifier: Option<String>,
    payload: &mut Value,
) -> Result<(reqwest::Method, String), String> {
    use reqwest::Method;
    match (domain, operation) {
        ("reminders", "list") => return Ok((Method::GET, "/reminders".into())),
        ("reminders", "create") => return Ok((Method::POST, "/reminders".into())),
        ("reminders", "update") => {
            return Ok((Method::PATCH, format!("/reminders/{}", id(identifier)?)))
        }
        ("reminders", "cancel") => {
            return Ok((
                Method::DELETE,
                format!(
                    "/reminders/{}?expectedRevision={}",
                    id(identifier)?,
                    revision(payload)?
                ),
            ))
        }
        ("reminders", "action") => {
            if payload.get("actionId").is_none() {
                payload["actionId"] = json!(uuid::Uuid::new_v4());
            }
            return Ok((
                Method::POST,
                format!("/reminders/{}/actions", id(identifier)?),
            ));
        }
        ("reminders", "seriesCreate") => return Ok((Method::POST, "/reminder-series".into())),
        ("reminders", "seriesUpdate") => {
            return Ok((
                Method::PATCH,
                format!("/reminder-series/{}", id(identifier)?),
            ))
        }
        ("reminders", "seriesCancel") => {
            return Ok((
                Method::DELETE,
                format!(
                    "/reminder-series/{}?expectedRevision={}",
                    id(identifier)?,
                    revision(payload)?
                ),
            ))
        }
        ("reminders", "seriesPause" | "seriesResume" | "seriesSkip") => {
            let action = match operation {
                "seriesPause" => "pause",
                "seriesResume" => "resume",
                _ => "skip",
            };
            return Ok((
                Method::POST,
                format!("/reminder-series/{}/{action}", id(identifier)?),
            ));
        }
        ("places", "list") => return Ok((Method::GET, "/places".into())),
        ("places", "create") => return Ok((Method::POST, "/places".into())),
        ("places", "update") => return Ok((Method::PUT, format!("/places/{}", id(identifier)?))),
        ("places", "remove") => {
            return Ok((
                Method::DELETE,
                format!(
                    "/places/{}?expectedRevision={}",
                    id(identifier)?,
                    revision(payload)?
                ),
            ))
        }
        ("places", "config") => return Ok((Method::GET, "/places/config".into())),
        ("places", "search") => {
            let query = payload["query"]
                .as_str()
                .filter(|v| !v.trim().is_empty() && v.len() <= 300)
                .ok_or("Informe um endereço válido.")?;
            let query = url::form_urlencoded::Serializer::new(String::new())
                .append_pair("query", query)
                .finish();
            return Ok((Method::GET, format!("/places/search?{query}")));
        }
        ("location", "settings") => return Ok((Method::GET, "/location/settings".into())),
        ("location", "configureNode" | "selectSource") => {
            return Ok((Method::PUT, "/location/settings".into()))
        }
        ("location", "current") => return Ok((Method::POST, "/location/current".into())),
        ("geofences", "sync") => return Ok((Method::POST, "/geofences/sync-request".into())),
        ("geofences", "list") => return Ok((Method::GET, "/geofences".into())),
        ("geofences", "create") => return Ok((Method::POST, "/geofences".into())),
        ("geofences", "update") => {
            return Ok((Method::PUT, format!("/geofences/{}", id(identifier)?)))
        }
        ("geofences", "pause" | "resume" | "cancel") => {
            payload["action"] = json!(operation);
            return Ok((
                Method::POST,
                format!("/geofences/{}/actions", id(identifier)?),
            ));
        }
        _ => return Err("Operação não disponível.".into()),
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rejects_arbitrary_routes_and_identifiers() {
        for operation in ["/api/admin", "https://evil.invalid", "deleteAll"] {
            assert!(route("places", operation, None, &mut json!({})).is_err());
        }
        assert!(route("places", "update", Some("../nodes".into()), &mut json!({})).is_err());
        assert!(route(
            "places",
            "remove",
            Some(uuid::Uuid::new_v4().to_string()),
            &mut json!({})
        )
        .is_err());
    }
    #[test]
    fn encodes_search_and_binds_revisions() {
        let (_, path) = route(
            "places",
            "search",
            None,
            &mut json!({"query":"São Paulo & x=y"}),
        )
        .unwrap();
        assert!(path.contains("%26"));
        assert!(!path.contains("&x="));
        let (_, path) = route(
            "reminders",
            "seriesCancel",
            Some(uuid::Uuid::new_v4().to_string()),
            &mut json!({"expectedRevision":3}),
        )
        .unwrap();
        assert!(path.ends_with("?expectedRevision=3"));
    }
}
