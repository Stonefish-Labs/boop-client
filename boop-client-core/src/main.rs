mod api;
mod models;
mod state;
mod store;

use std::{sync::Arc, time::Duration};

use anyhow::{anyhow, Result};
use futures_util::StreamExt;
use models::{ClientState, Event, EventStatus, ServerProfile, SyncEnvelope};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use state::StateReducer;
use store::{PersistedConfig, Store};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    sync::{mpsc, Mutex},
    task::JoinHandle,
};
use tokio_tungstenite::connect_async;

use crate::api::{display_name_for, normalize_base_url, profile_id_for, BoopApi};

#[derive(Debug, Deserialize)]
struct RpcRequest {
    #[serde(default)]
    id: Value,
    method: String,
    #[serde(default)]
    params: Value,
}

#[derive(Debug, Serialize)]
struct RpcResponse {
    id: Value,
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    result: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

type Outbound = Value;
type Outbox = mpsc::Sender<Outbound>;

#[tokio::main]
async fn main() -> Result<()> {
    let (tx, mut rx) = mpsc::channel::<Outbound>(128);
    tokio::spawn(async move {
        let mut stdout = tokio::io::stdout();
        while let Some(message) = rx.recv().await {
            let Ok(raw) = serde_json::to_vec(&message) else {
                continue;
            };
            if stdout.write_all(&raw).await.is_err()
                || stdout.write_all(b"\n").await.is_err()
                || stdout.flush().await.is_err()
            {
                break;
            }
        }
    });

    let mut runtime = Runtime::new(tx.clone()).await?;
    runtime
        .emit_event("ready", json!({ "state_path": runtime.store.path() }))
        .await;
    runtime.emit_state().await;

    let stdin = BufReader::new(tokio::io::stdin());
    let mut lines = stdin.lines();
    while let Some(line) = lines.next_line().await? {
        if line.trim().is_empty() {
            continue;
        }
        let response = match serde_json::from_str::<RpcRequest>(&line) {
            Ok(request) => runtime.handle(request).await,
            Err(error) => RpcResponse {
                id: Value::Null,
                ok: false,
                result: None,
                error: Some(error.to_string()),
            },
        };
        let _ = tx.send(json!(response)).await;
    }
    runtime.disconnect_sync().await;
    Ok(())
}

struct Runtime {
    store: Store,
    persisted: Arc<Mutex<PersistedConfig>>,
    reducer: Arc<Mutex<StateReducer>>,
    tx: Outbox,
    sync_task: Option<JoinHandle<()>>,
}

impl Runtime {
    async fn new(tx: Outbox) -> Result<Self> {
        let store = Store::new()?;
        let mut persisted = store.load().await?;
        if persisted.notification_sound.trim().is_empty() {
            persisted.notification_sound = "default".to_string();
        }
        let selected_server_id = persisted
            .selected_server_id
            .clone()
            .filter(|id| persisted.servers.iter().any(|server| server.id == *id))
            .or_else(|| persisted.servers.first().map(|server| server.id.clone()));
        persisted.selected_server_id = selected_server_id.clone();
        let reducer = StateReducer::new(
            persisted.servers.clone(),
            selected_server_id,
            persisted.notification_sound.clone(),
        );
        Ok(Self {
            store,
            persisted: Arc::new(Mutex::new(persisted)),
            reducer: Arc::new(Mutex::new(reducer)),
            tx,
            sync_task: None,
        })
    }

    async fn handle(&mut self, request: RpcRequest) -> RpcResponse {
        match self.handle_method(&request.method, request.params).await {
            Ok(result) => RpcResponse {
                id: request.id,
                ok: true,
                result: Some(result),
                error: None,
            },
            Err(error) => {
                let message = error.to_string();
                self.reducer.lock().await.set_error(message.clone());
                self.emit_state().await;
                RpcResponse {
                    id: request.id,
                    ok: false,
                    result: None,
                    error: Some(message),
                }
            }
        }
    }

    async fn handle_method(&mut self, method: &str, params: Value) -> Result<Value> {
        match method {
            "state" => Ok(json!(self.state().await)),
            "enroll" => {
                let params: EnrollParams = serde_json::from_value(params)?;
                self.enroll(params).await
            }
            "select_server" => {
                let params: SelectServerParams = serde_json::from_value(params)?;
                self.select_server(params.server_id).await
            }
            "forget_server" => {
                let params: SelectServerParams = serde_json::from_value(params)?;
                self.forget_server(&params.server_id).await
            }
            "refresh" => self.refresh().await,
            "submit_action" => {
                let params: SubmitActionParams = serde_json::from_value(params)?;
                self.submit_action(params).await
            }
            "clear_channel" => {
                let params: ChannelParams = serde_json::from_value(params)?;
                self.api().await?.clear_channel(&params.channel_id).await?;
                self.refresh().await
            }
            "set_subscription" => {
                let params: SetSubscriptionParams = serde_json::from_value(params)?;
                self.api()
                    .await?
                    .set_subscription(&params.channel_id, params.subscribed)
                    .await?;
                self.refresh().await
            }
            "set_notification_preference" => {
                let params: NotificationPreferenceParams = serde_json::from_value(params)?;
                self.set_notification_preference(&params.notification_sound)
                    .await
            }
            "list_devices" => {
                let devices = self.api().await?.devices().await?;
                self.reducer.lock().await.state.devices = devices.clone();
                self.emit_state().await;
                Ok(json!({ "devices": devices }))
            }
            "rename_device" => {
                let params: RenameDeviceParams = serde_json::from_value(params)?;
                let device = self
                    .api()
                    .await?
                    .rename_device(&params.device_id, &params.name)
                    .await?;
                let mut reducer = self.reducer.lock().await;
                if let Some(existing) = reducer
                    .state
                    .devices
                    .iter_mut()
                    .find(|existing| existing.id == device.id)
                {
                    *existing = device.clone();
                }
                drop(reducer);
                self.emit_state().await;
                Ok(json!({ "device": device }))
            }
            "revoke_device" => {
                let params: RevokeDeviceParams = serde_json::from_value(params)?;
                self.api().await?.revoke_device(&params.device_id).await?;
                self.refresh_or_forget_current(&params.device_id).await
            }
            "connect_sync" => {
                self.connect_sync().await?;
                Ok(json!({ "connected": true }))
            }
            "disconnect_sync" => {
                self.disconnect_sync().await;
                Ok(json!({ "connected": false }))
            }
            "select_channel" => {
                let params: ChannelParams = serde_json::from_value(params)?;
                self.reducer.lock().await.state.selected_channel_id = Some(params.channel_id);
                self.refresh().await
            }
            "select_event" => {
                let params: SelectEventParams = serde_json::from_value(params)?;
                self.reducer.lock().await.state.selected_event_id = Some(params.event_id);
                self.emit_state().await;
                Ok(json!(self.state().await))
            }
            _ => Err(anyhow!("unknown method: {method}")),
        }
    }

    async fn enroll(&mut self, params: EnrollParams) -> Result<Value> {
        let normalized_url = normalize_base_url(&params.base_url);
        let api = BoopApi::new(&normalized_url, None)?;
        let response = api
            .enroll(
                &params.code.trim().to_ascii_uppercase(),
                params.device_name.trim(),
            )
            .await?;
        let profile_id = profile_id_for(&normalized_url);
        let profile = ServerProfile {
            id: profile_id.clone(),
            name: display_name_for(&normalized_url),
            base_url_string: normalized_url,
            device_name: params.device_name.trim().to_string(),
            device_id: Some(response.device_id.clone()),
            user_id: Some(response.user_id.clone()),
            user_name: Some(response.user_name.clone()),
        };

        {
            let mut persisted = self.persisted.lock().await;
            self.store
                .save_token(&mut persisted, &profile_id, &response.token)
                .await?;
            persisted.selected_server_id = Some(profile_id.clone());
            persisted.notification_sound = params
                .notification_sound
                .unwrap_or_else(|| persisted.notification_sound.clone());
            upsert_profile(&mut persisted.servers, profile.clone());
            self.store.save(persisted.clone()).await?;
        }

        {
            let mut reducer = self.reducer.lock().await;
            reducer.upsert_server(profile);
            reducer.set_selected_server(profile_id);
            reducer.state.channels = response.channels;
            reducer.state.devices = response.devices;
            reducer.state.is_registered = true;
        }
        self.refresh().await
    }

    async fn select_server(&mut self, server_id: String) -> Result<Value> {
        {
            let mut persisted = self.persisted.lock().await;
            if !persisted
                .servers
                .iter()
                .any(|server| server.id == server_id)
            {
                return Err(anyhow!("unknown server: {server_id}"));
            }
            persisted.selected_server_id = Some(server_id.clone());
            self.store.save(persisted.clone()).await?;
        }
        self.disconnect_sync().await;
        self.reducer.lock().await.set_selected_server(server_id);
        self.refresh().await?;
        self.connect_sync().await?;
        Ok(json!(self.state().await))
    }

    async fn forget_server(&mut self, server_id: &str) -> Result<Value> {
        self.disconnect_sync().await;
        {
            let mut persisted = self.persisted.lock().await;
            persisted.servers.retain(|server| server.id != server_id);
            self.store.delete_token(&mut persisted, server_id).await?;
            if persisted.selected_server_id.as_deref() == Some(server_id) {
                persisted.selected_server_id =
                    persisted.servers.first().map(|server| server.id.clone());
            }
            self.store.save(persisted.clone()).await?;
        }
        self.reducer.lock().await.remove_server(server_id);
        self.emit_state().await;
        Ok(json!(self.state().await))
    }

    async fn refresh(&mut self) -> Result<Value> {
        let api = self.api().await?;
        let current_user = api.current_user().await?;
        let mut devices = api.devices().await?;
        if !devices
            .iter()
            .any(|device| device.id == current_user.current_device.id)
        {
            devices.insert(0, current_user.current_device.clone());
        }
        let channels = api.channels().await?;
        let events = api.events(None, Some(500)).await?;
        let user = Some(current_user.user);
        let candidates = self
            .reducer
            .lock()
            .await
            .apply_refresh(user, devices, channels, events);
        self.emit_notifications(candidates).await;
        self.emit_state().await;
        Ok(json!(self.state().await))
    }

    async fn submit_action(&mut self, params: SubmitActionParams) -> Result<Value> {
        let event = self
            .api()
            .await?
            .submit_action(
                &params.event_id,
                &params.action_id,
                params
                    .text
                    .as_deref()
                    .filter(|text| !text.trim().is_empty()),
            )
            .await?;
        self.reducer.lock().await.apply_event_update(event.clone());
        self.emit_state().await;
        Ok(json!({ "event": event }))
    }

    async fn set_notification_preference(&mut self, notification_sound: &str) -> Result<Value> {
        self.api()
            .await?
            .set_notification_sound(notification_sound)
            .await?;
        {
            let mut persisted = self.persisted.lock().await;
            persisted.notification_sound = notification_sound.to_string();
            self.store.save(persisted.clone()).await?;
        }
        self.reducer.lock().await.state.notification_sound = notification_sound.to_string();
        self.emit_state().await;
        Ok(json!(self.state().await))
    }

    async fn refresh_or_forget_current(&mut self, revoked_device_id: &str) -> Result<Value> {
        let current_was_revoked = self
            .reducer
            .lock()
            .await
            .selected_server()
            .and_then(|server| server.device_id.as_deref())
            == Some(revoked_device_id);
        if current_was_revoked {
            let server_id = {
                self.reducer
                    .lock()
                    .await
                    .selected_server()
                    .map(|server| server.id.clone())
            };
            if let Some(server_id) = server_id {
                return self.forget_server(&server_id).await;
            }
        }
        self.refresh().await
    }

    async fn connect_sync(&mut self) -> Result<()> {
        self.disconnect_sync().await;
        let api = self.api().await?;
        let url = api.websocket_url()?;
        let reducer = self.reducer.clone();
        let tx = self.tx.clone();
        self.sync_task = Some(tokio::spawn(async move {
            loop {
                match connect_async(url.as_str()).await {
                    Ok((mut socket, _)) => {
                        {
                            let mut reducer = reducer.lock().await;
                            reducer.mark_sync_connected(true);
                            emit_to(&tx, "sync_status", json!({ "connected": true })).await;
                            emit_to(&tx, "state", json!(reducer.state.clone())).await;
                        }
                        while let Some(message) = socket.next().await {
                            let Ok(message) = message else {
                                break;
                            };
                            if !message.is_text() && !message.is_binary() {
                                continue;
                            }
                            let raw = match message.into_data() {
                                data if !data.is_empty() => data,
                                _ => continue,
                            };
                            let Ok(envelope) = serde_json::from_slice::<SyncEnvelope>(&raw) else {
                                continue;
                            };
                            let auth_revoked = is_current_device_revoked(&reducer, &envelope).await;
                            let should_resync = should_resync_after(&envelope);
                            let candidates = {
                                let mut reducer = reducer.lock().await;
                                reducer.apply_sync_envelope(envelope)
                            };
                            for event in candidates {
                                emit_to(&tx, "notification_candidate", json!({ "event": event }))
                                    .await;
                            }
                            let state = reducer.lock().await.state.clone();
                            emit_to(&tx, "state", json!(state)).await;
                            if auth_revoked {
                                emit_to(&tx, "auth_revoked", json!({})).await;
                            }
                            if should_resync {
                                emit_to(&tx, "resync_required", json!({})).await;
                            }
                        }
                    }
                    Err(error) => {
                        emit_to(
                            &tx,
                            "transient_error",
                            json!({ "message": error.to_string() }),
                        )
                        .await;
                    }
                }
                {
                    let mut reducer = reducer.lock().await;
                    reducer.mark_sync_connected(false);
                    emit_to(&tx, "sync_status", json!({ "connected": false })).await;
                    emit_to(&tx, "state", json!(reducer.state.clone())).await;
                }
                tokio::time::sleep(Duration::from_secs(2)).await;
            }
        }));
        Ok(())
    }

    async fn disconnect_sync(&mut self) {
        if let Some(task) = self.sync_task.take() {
            task.abort();
        }
        self.reducer.lock().await.mark_sync_connected(false);
        self.emit_event("sync_status", json!({ "connected": false }))
            .await;
        self.emit_state().await;
    }

    async fn api(&self) -> Result<BoopApi> {
        let reducer = self.reducer.lock().await;
        let server = reducer
            .selected_server()
            .cloned()
            .ok_or_else(|| anyhow!("no selected server"))?;
        drop(reducer);
        let persisted = self.persisted.lock().await;
        let token = self.store.load_token(&persisted, &server.id);
        drop(persisted);
        BoopApi::new(&server.base_url_string, token)
    }

    async fn state(&self) -> ClientState {
        self.reducer.lock().await.state.clone()
    }

    async fn emit_state(&self) {
        self.emit_event("state", json!(self.state().await)).await;
    }

    async fn emit_notifications(&self, events: Vec<Event>) {
        for event in events {
            if event.status == EventStatus::Pending {
                self.emit_event("notification_candidate", json!({ "event": event }))
                    .await;
            }
        }
    }

    async fn emit_event(&self, event: &str, payload: Value) {
        emit_to(&self.tx, event, payload).await;
    }
}

#[derive(Debug, Deserialize)]
struct EnrollParams {
    base_url: String,
    device_name: String,
    code: String,
    #[serde(default)]
    notification_sound: Option<String>,
}

#[derive(Debug, Deserialize)]
struct SelectServerParams {
    server_id: String,
}

#[derive(Debug, Deserialize)]
struct SubmitActionParams {
    event_id: String,
    action_id: String,
    #[serde(default)]
    text: Option<String>,
}

#[derive(Debug, Deserialize)]
struct ChannelParams {
    channel_id: String,
}

#[derive(Debug, Deserialize)]
struct SetSubscriptionParams {
    channel_id: String,
    subscribed: bool,
}

#[derive(Debug, Deserialize)]
struct NotificationPreferenceParams {
    notification_sound: String,
}

#[derive(Debug, Deserialize)]
struct RenameDeviceParams {
    device_id: String,
    name: String,
}

#[derive(Debug, Deserialize)]
struct RevokeDeviceParams {
    device_id: String,
}

#[derive(Debug, Deserialize)]
struct SelectEventParams {
    event_id: String,
}

async fn emit_to(tx: &Outbox, event: &str, payload: Value) {
    let _ = tx
        .send(json!({
            "event": event,
            "payload": payload,
        }))
        .await;
}

async fn is_current_device_revoked(
    reducer: &Arc<Mutex<StateReducer>>,
    envelope: &SyncEnvelope,
) -> bool {
    if envelope.kind != "device_revoked" {
        return false;
    }
    let Some(device_id) = envelope
        .payload
        .extra
        .get("device_id")
        .and_then(|value| value.as_str())
    else {
        return false;
    };
    reducer
        .lock()
        .await
        .selected_server()
        .and_then(|server| server.device_id.as_deref())
        == Some(device_id)
}

fn should_resync_after(envelope: &SyncEnvelope) -> bool {
    matches!(
        envelope.kind.as_str(),
        "cleared" | "subscription_updated" | "resync_required"
    ) || envelope.kind.starts_with("device_")
}

fn upsert_profile(profiles: &mut Vec<ServerProfile>, profile: ServerProfile) {
    if let Some(existing) = profiles
        .iter_mut()
        .find(|existing| existing.id == profile.id)
    {
        *existing = profile;
    } else {
        profiles.push(profile);
    }
}
