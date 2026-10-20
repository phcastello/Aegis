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
fn reminder_list_path(payload: &Value) -> Result<String, String> {
    let fields = payload.as_object().ok_or("Dados de página inválidos.")?;
    if fields
        .keys()
        .any(|key| !matches!(key.as_str(), "offset" | "seriesOffset" | "focusId"))
    {
        return Err("Dados de página inválidos.".into());
    }
    let mut query = url::form_urlencoded::Serializer::new(String::new());
    for field in ["offset", "seriesOffset"] {
        if let Some(value) = fields.get(field) {
            let offset = value
                .as_u64()
                .filter(|value| *value <= 1_000_000)
                .ok_or("Página inválida.")?;
            query.append_pair(field, &offset.to_string());
        }
    }
    if let Some(value) = fields.get("focusId") {
        let identifier = id(Some(
            value.as_str().ok_or("Identificador inválido.")?.into(),
        ))?;
        query.append_pair("focusId", &identifier);
    }
    let query = query.finish();
    Ok(if query.is_empty() {
        "/reminders".into()
    } else {
        format!("/reminders?{query}")
    })
}
pub(crate) fn route(
    domain: &str,
    operation: &str,
    identifier: Option<String>,
    payload: &mut Value,
) -> Result<(reqwest::Method, String), String> {
    use reqwest::Method;
    match (domain, operation) {
        ("reminders", "list") => return Ok((Method::GET, reminder_list_path(payload)?)),
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
            if payload
                .as_object()
                .is_none_or(|fields| fields.keys().any(|key| key != "query"))
            {
                return Err("Dados de busca inválidos.".into());
            }
            payload["query"]
                .as_str()
                .filter(|value| {
                    !value.trim().is_empty()
                        && value.encode_utf16().count() <= 200
                        && !value.chars().any(char::is_control)
                })
                .ok_or("Informe um endereço válido.")?;
            // Native authentication is unchanged; personal addresses belong in
            // the JSON body, rather than proxy access logs of request URLs.
            return Ok((Method::POST, "/places/search".into()));
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
    use reqwest::Method;
    use super::*;
    #[test]
    fn reminder_pages_preserve_the_legacy_route_and_encode_only_bounded_parameters() {
        assert_eq!(
            route("reminders", "list", None, &mut json!({})).unwrap(),
            (reqwest::Method::GET, "/reminders".into())
        );
        let identifier = uuid::Uuid::new_v4().to_string();
        let (_, path) = route(
            "reminders",
            "list",
            None,
            &mut json!({"offset":200,"seriesOffset":100,"focusId":identifier}),
        )
        .unwrap();
        assert_eq!(
            path,
            format!("/reminders?offset=200&seriesOffset=100&focusId={identifier}")
        );
        assert!(route("reminders", "list", None, &mut json!({"offset":1_000_000})).is_ok());
    }
    #[test]
    fn reminder_pages_reject_arbitrary_queries_and_invalid_offsets_or_focus_ids() {
        for payload in [
            json!({"offset":-1}),
            json!({"offset":1_000_001}),
            json!({"offset":1.5}),
            json!({"offset":"200&nodeId=other"}),
            json!({"seriesOffset":null}),
            json!({"url":"https://evil.invalid"}),
            json!({"focusId":"../admin"}),
            json!({"focusId":uuid::Uuid::nil()}),
            json!([]),
        ] {
            assert!(route("reminders", "list", None, &mut payload.clone()).is_err());
        }
    }
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
    fn search_keeps_the_exact_address_in_the_body_and_out_of_the_url() {
        let mut payload = json!({"query":"São Paulo & x=y / número 12"});
        let original = payload.clone();
        let (method, path) = route("places", "search", None, &mut payload).unwrap();
        assert_eq!(method, Method::POST);
        assert_eq!(path, "/places/search");
        assert_eq!(payload, original);
        assert!(!path.contains('?'));
    }
    #[test]
    fn search_has_a_closed_bounded_body_including_unicode() {
        for mut payload in [
            json!({}),
            json!({"query":"  "}),
            json!({"query":"private\naddress"}),
            json!({"query":"a".repeat(201)}),
            json!({"query":"😀".repeat(101)}),
            json!({"query":"Rua sintética", "url":"https://evil.invalid"}),
            json!([]),
        ] {
            assert!(route("places", "search", None, &mut payload).is_err());
        }
        for query in ["界".repeat(200), "😀".repeat(100)] {
            assert!(route("places", "search", None, &mut json!({"query":query})).is_ok());
        }
    }
    #[test]
    fn binds_revisions_to_the_known_action_route() {
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
