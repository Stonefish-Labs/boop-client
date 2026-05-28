use anyhow::{anyhow, Result};
use reqwest::{Client, StatusCode};
use serde::{de::DeserializeOwned, Deserialize, Serialize};
use url::Url;

use crate::models::{
    Channel, ChannelsResponse, CurrentUserResponse, EnrollDeviceResponse, Event, EventResponse,
    EventsResponse, UserDevice, UserDeviceResponse, UserDevicesResponse,
};

#[derive(Debug, Clone)]
pub struct BoopApi {
    base_url: Url,
    token: Option<String>,
    client: Client,
}

impl BoopApi {
    pub fn new(base_url: &str, token: Option<String>) -> Result<Self> {
        Ok(Self {
            base_url: Url::parse(&normalize_base_url(base_url))?,
            token,
            client: Client::new(),
        })
    }

    pub fn websocket_url(&self) -> Result<Url> {
        let token = self
            .token
            .as_deref()
            .ok_or_else(|| anyhow!("device token is missing"))?;
        let mut url = self.base_url.clone();
        url.set_scheme(if url.scheme() == "https" { "wss" } else { "ws" })
            .map_err(|_| anyhow!("could not set websocket scheme"))?;
        url.set_path(&join_path(self.base_url.path(), "/api/v1/sync"));
        url.query_pairs_mut().clear().append_pair("token", token);
        Ok(url)
    }

    pub async fn enroll(&self, code: &str, device_name: &str) -> Result<EnrollDeviceResponse> {
        #[derive(Serialize)]
        struct Body<'a> {
            code: &'a str,
            device_name: &'a str,
            platform: &'static str,
        }

        let body = Body {
            code,
            device_name,
            platform: "windows",
        };
        match self
            .request("POST", "/api/v1/enroll", Some(&body), false)
            .await
        {
            Ok(value) => Ok(value),
            Err(ApiStatusError {
                status: StatusCode::NOT_FOUND,
                ..
            }) => {
                let pair: PairResponse = self
                    .request("POST", "/api/v1/pair", Some(&body), false)
                    .await?;
                Ok(EnrollDeviceResponse {
                    device_id: pair.device_id,
                    token: pair.token,
                    channels: pair.channels,
                    user_id: "default".to_string(),
                    user_name: "Boop".to_string(),
                    devices: Vec::new(),
                })
            }
            Err(err) => Err(err.into()),
        }
    }

    pub async fn current_user(&self) -> Result<CurrentUserResponse> {
        self.request::<(), _>("GET", "/api/v1/users/me", None, true)
            .await
            .map_err(Into::into)
    }

    pub async fn devices(&self) -> Result<Vec<UserDevice>> {
        let response: UserDevicesResponse = self
            .request::<(), _>("GET", "/api/v1/users/me/devices", None, true)
            .await?;
        Ok(response.devices)
    }

    pub async fn rename_device(&self, id: &str, name: &str) -> Result<UserDevice> {
        #[derive(Serialize)]
        struct Body<'a> {
            name: &'a str,
        }
        let response: UserDeviceResponse = self
            .request(
                "PUT",
                &format!("/api/v1/users/me/devices/{id}"),
                Some(&Body { name }),
                true,
            )
            .await?;
        Ok(response.device)
    }

    pub async fn revoke_device(&self, id: &str) -> Result<()> {
        let _: serde_json::Value = self
            .request::<(), _>(
                "DELETE",
                &format!("/api/v1/users/me/devices/{id}"),
                None,
                true,
            )
            .await?;
        Ok(())
    }

    pub async fn channels(&self) -> Result<Vec<Channel>> {
        let response: ChannelsResponse = self
            .request::<(), _>("GET", "/api/v1/channels", None, true)
            .await?;
        Ok(response.channels)
    }

    pub async fn events(
        &self,
        channel_id: Option<&str>,
        limit: Option<usize>,
    ) -> Result<Vec<Event>> {
        let mut path = "/api/v1/events?include_cleared=false".to_string();
        if let Some(channel_id) = channel_id {
            path.push_str("&channel_id=");
            path.push_str(&urlencoding(channel_id));
        }
        if let Some(limit) = limit {
            path.push_str("&limit=");
            path.push_str(&limit.to_string());
        }
        let response: EventsResponse = self.request::<(), _>("GET", &path, None, true).await?;
        Ok(response.events)
    }

    pub async fn submit_action(
        &self,
        event_id: &str,
        action_id: &str,
        text: Option<&str>,
    ) -> Result<Event> {
        #[derive(Serialize)]
        struct Body<'a> {
            text: Option<&'a str>,
        }
        let response: EventResponse = self
            .request(
                "POST",
                &format!("/api/v1/events/{event_id}/actions/{action_id}"),
                Some(&Body { text }),
                true,
            )
            .await?;
        Ok(response.event)
    }

    pub async fn clear_channel(&self, channel_id: &str) -> Result<()> {
        let _: serde_json::Value = self
            .request::<(), _>(
                "POST",
                &format!("/api/v1/devices/me/channels/{channel_id}/clear"),
                None,
                true,
            )
            .await?;
        Ok(())
    }

    pub async fn set_subscription(&self, channel_id: &str, subscribed: bool) -> Result<()> {
        #[derive(Serialize)]
        struct Body {
            subscribed: bool,
        }
        let _: serde_json::Value = self
            .request(
                "PUT",
                &format!("/api/v1/devices/me/subscriptions/{channel_id}"),
                Some(&Body { subscribed }),
                true,
            )
            .await?;
        Ok(())
    }

    pub async fn set_notification_sound(&self, notification_sound: &str) -> Result<()> {
        #[derive(Serialize)]
        struct Body<'a> {
            notification_sound: &'a str,
        }
        let _: serde_json::Value = self
            .request(
                "PUT",
                "/api/v1/devices/me/preferences",
                Some(&Body { notification_sound }),
                true,
            )
            .await?;
        Ok(())
    }

    async fn request<Body, Response>(
        &self,
        method: &str,
        path: &str,
        body: Option<&Body>,
        requires_token: bool,
    ) -> Result<Response, ApiStatusError>
    where
        Body: Serialize + ?Sized,
        Response: DeserializeOwned,
    {
        let url = self.url(path).map_err(ApiStatusError::other)?;
        let mut request = self.client.request(method.parse().unwrap(), url);
        if let Some(body) = body {
            request = request.json(body);
        }
        if requires_token {
            let token = self
                .token
                .as_deref()
                .ok_or_else(|| ApiStatusError::other(anyhow!("device token is missing")))?;
            request = request.bearer_auth(token);
        }
        let response = request
            .header("Accept", "application/json")
            .send()
            .await
            .map_err(ApiStatusError::other)?;
        let status = response.status();
        let bytes = response.bytes().await.map_err(ApiStatusError::other)?;
        if !status.is_success() {
            return Err(ApiStatusError {
                status,
                body: String::from_utf8_lossy(&bytes).to_string(),
            });
        }
        if bytes.is_empty() {
            return serde_json::from_value(serde_json::Value::Object(Default::default()))
                .map_err(ApiStatusError::other);
        }
        serde_json::from_slice(&bytes).map_err(ApiStatusError::other)
    }

    fn url(&self, path: &str) -> Result<Url> {
        let mut url = self.base_url.clone();
        let (path_part, query_part) = path.split_once('?').unwrap_or((path, ""));
        url.set_path(&join_path(self.base_url.path(), path_part));
        url.set_query(if query_part.is_empty() {
            None
        } else {
            Some(query_part)
        });
        Ok(url)
    }
}

#[derive(Debug, Deserialize)]
struct PairResponse {
    device_id: String,
    token: String,
    channels: Vec<Channel>,
}

#[derive(Debug, thiserror::Error)]
#[error("Boop request failed with {status}: {body}")]
pub struct ApiStatusError {
    pub status: StatusCode,
    pub body: String,
}

impl ApiStatusError {
    fn other(error: impl std::fmt::Display) -> Self {
        Self {
            status: StatusCode::INTERNAL_SERVER_ERROR,
            body: error.to_string(),
        }
    }
}

pub fn normalize_base_url(value: &str) -> String {
    let trimmed = value.trim().trim_end_matches('/');
    if trimmed.contains("://") {
        return trimmed.to_string();
    }
    if trimmed == "localhost"
        || trimmed.starts_with("localhost:")
        || trimmed.starts_with("127.")
        || trimmed.starts_with("192.168.")
    {
        format!("http://{trimmed}")
    } else {
        format!("https://{trimmed}")
    }
}

pub fn profile_id_for(base_url: &str) -> String {
    let normalized = normalize_base_url(base_url).to_ascii_lowercase();
    let mapped: String = normalized
        .chars()
        .map(|ch| if ch.is_ascii_alphanumeric() { ch } else { '-' })
        .collect();
    let compact = mapped
        .split('-')
        .filter(|part| !part.is_empty())
        .collect::<Vec<_>>()
        .join("-");
    if compact.is_empty() {
        uuid::Uuid::new_v4().to_string()
    } else {
        compact.chars().take(80).collect()
    }
}

pub fn display_name_for(base_url: &str) -> String {
    let normalized = normalize_base_url(base_url);
    let Ok(url) = Url::parse(&normalized) else {
        return "Boop Server".to_string();
    };
    let mut name = url.host_str().unwrap_or("Boop Server").to_string();
    let path = url.path().trim_matches('/');
    if !path.is_empty() {
        name.push('/');
        name.push_str(path);
    }
    name
}

fn join_path(base: &str, path: &str) -> String {
    let base = base.trim_end_matches('/');
    let path = path.trim_start_matches('/');
    match (base.is_empty(), path.is_empty()) {
        (true, true) => "/".to_string(),
        (true, false) => format!("/{path}"),
        (false, true) => base.to_string(),
        (false, false) => format!("{base}/{path}"),
    }
}

fn urlencoding(value: &str) -> String {
    url::form_urlencoded::byte_serialize(value.as_bytes()).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_local_and_remote_urls() {
        assert_eq!(
            normalize_base_url("localhost:8767"),
            "http://localhost:8767"
        );
        assert_eq!(
            normalize_base_url("boop.example.com/api/"),
            "https://boop.example.com/api"
        );
    }

    #[test]
    fn builds_profile_ids_from_urls() {
        assert_eq!(
            profile_id_for("https://boop.example.com/api"),
            "https-boop-example-com-api"
        );
    }
}
