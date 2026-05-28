use std::collections::{BTreeMap, BTreeSet};

use crate::models::{
    Channel, ClientState, Event, EventStatus, ServerProfile, SyncEnvelope, User, UserDevice,
};

const HANDLED_EVENT_DISPLAY_LIMIT: usize = 500;

#[derive(Debug, Clone)]
pub struct StateReducer {
    pub state: ClientState,
    known_pending_event_ids: BTreeSet<String>,
    has_loaded_pending_snapshot: bool,
}

impl StateReducer {
    pub fn new(
        servers: Vec<ServerProfile>,
        selected_server_id: Option<String>,
        notification_sound: String,
    ) -> Self {
        let is_registered = !servers.is_empty();
        Self {
            state: ClientState {
                servers,
                selected_server_id,
                current_user: None,
                devices: Vec::new(),
                channels: Vec::new(),
                pending_counts_by_channel: BTreeMap::new(),
                events: Vec::new(),
                selected_channel_id: None,
                selected_event_id: None,
                notification_sound,
                is_registered,
                is_sync_connected: false,
                last_error: None,
            },
            known_pending_event_ids: BTreeSet::new(),
            has_loaded_pending_snapshot: false,
        }
    }

    pub fn selected_server(&self) -> Option<&ServerProfile> {
        self.state
            .selected_server_id
            .as_deref()
            .and_then(|id| self.state.servers.iter().find(|server| server.id == id))
            .or_else(|| self.state.servers.first())
    }

    pub fn upsert_server(&mut self, server: ServerProfile) {
        if let Some(existing) = self
            .state
            .servers
            .iter_mut()
            .find(|existing| existing.id == server.id)
        {
            *existing = server;
        } else {
            self.state.servers.push(server);
        }
        self.state.is_registered = !self.state.servers.is_empty();
    }

    pub fn remove_server(&mut self, server_id: &str) {
        self.state.servers.retain(|server| server.id != server_id);
        if self.state.selected_server_id.as_deref() == Some(server_id) {
            self.state.selected_server_id =
                self.state.servers.first().map(|server| server.id.clone());
            self.clear_loaded_data();
        }
        self.state.is_registered = !self.state.servers.is_empty();
    }

    pub fn set_selected_server(&mut self, server_id: String) {
        self.state.selected_server_id = Some(server_id);
        self.clear_loaded_data();
    }

    pub fn apply_refresh(
        &mut self,
        current_user: Option<User>,
        devices: Vec<UserDevice>,
        channels: Vec<Channel>,
        events: Vec<Event>,
    ) -> Vec<Event> {
        self.state.current_user = current_user;
        self.state.devices = devices;
        self.state.channels = channels;
        let visible_channel_ids: BTreeSet<_> = self
            .state
            .channels
            .iter()
            .filter(|channel| channel.subscribed)
            .map(|channel| channel.id.as_str())
            .collect();
        if self
            .state
            .selected_channel_id
            .as_deref()
            .map(|id| !visible_channel_ids.contains(id))
            .unwrap_or(true)
        {
            self.state.selected_channel_id = self
                .state
                .channels
                .iter()
                .find(|channel| channel.subscribed)
                .map(|channel| channel.id.clone());
        }

        let pending_events: Vec<_> = events
            .iter()
            .filter(|event| event.status == EventStatus::Pending)
            .cloned()
            .collect();
        self.state.pending_counts_by_channel = count_pending_by_channel(&pending_events);
        let notification_candidates = if self.has_loaded_pending_snapshot {
            pending_events
                .iter()
                .filter(|event| !self.known_pending_event_ids.contains(&event.id))
                .cloned()
                .collect()
        } else {
            Vec::new()
        };
        self.known_pending_event_ids = pending_events
            .iter()
            .map(|event| event.id.clone())
            .collect();
        self.has_loaded_pending_snapshot = true;

        self.state.events = self.visible_events_for_selected_channel(events);
        self.ensure_selected_event();
        self.state.last_error = None;
        notification_candidates
    }

    pub fn apply_sync_envelope(&mut self, envelope: SyncEnvelope) -> Vec<Event> {
        self.state.is_sync_connected = true;
        let mut notification_candidates = Vec::new();
        if let Some(channel) = envelope.payload.channel {
            if let Some(existing) = self
                .state
                .channels
                .iter_mut()
                .find(|existing| existing.id == channel.id)
            {
                *existing = channel;
            } else {
                self.state.channels.push(channel);
            }
        }
        if let Some(event) = envelope.payload.event {
            let should_notify =
                envelope.kind == "created" && self.apply_event_update(event.clone());
            if should_notify {
                notification_candidates.push(event);
            }
        }
        notification_candidates
    }

    pub fn apply_event_update(&mut self, event: Event) -> bool {
        let is_new_pending = self.update_pending_tracking(&event);
        if self.state.selected_channel_id.as_deref() == Some(event.channel_id.as_str())
            || self.state.selected_channel_id.is_none()
        {
            self.upsert_visible_event(event);
        }
        is_new_pending
    }

    fn upsert_visible_event(&mut self, event: Event) {
        if let Some(existing) = self
            .state
            .events
            .iter_mut()
            .find(|existing| existing.id == event.id)
        {
            *existing = event;
        } else {
            self.state.events.insert(0, event);
        }
        self.state.events = visible_events(self.state.events.clone());
        self.ensure_selected_event();
    }

    fn update_pending_tracking(&mut self, event: &Event) -> bool {
        let existing = self
            .state
            .events
            .iter()
            .find(|existing| existing.id == event.id);
        let was_pending = self.known_pending_event_ids.contains(&event.id)
            || existing
                .map(|existing| existing.status == EventStatus::Pending)
                .unwrap_or(false);
        let previous_channel_id = existing
            .map(|existing| existing.channel_id.as_str())
            .unwrap_or(event.channel_id.as_str());
        let is_pending = event.status == EventStatus::Pending;

        match (was_pending, is_pending) {
            (false, true) => {
                *self
                    .state
                    .pending_counts_by_channel
                    .entry(event.channel_id.clone())
                    .or_insert(0) += 1;
                self.known_pending_event_ids.insert(event.id.clone());
                true
            }
            (true, false) => {
                if let Some(count) = self
                    .state
                    .pending_counts_by_channel
                    .get_mut(previous_channel_id)
                {
                    *count = count.saturating_sub(1);
                }
                self.state
                    .pending_counts_by_channel
                    .retain(|_, count| *count > 0);
                self.known_pending_event_ids.remove(&event.id);
                false
            }
            (true, true) => {
                self.known_pending_event_ids.insert(event.id.clone());
                false
            }
            (false, false) => false,
        }
    }

    pub fn mark_sync_connected(&mut self, connected: bool) {
        self.state.is_sync_connected = connected;
    }

    pub fn set_error(&mut self, error: impl Into<String>) {
        self.state.last_error = Some(error.into());
    }

    pub fn clear_loaded_data(&mut self) {
        self.state.current_user = None;
        self.state.devices.clear();
        self.state.channels.clear();
        self.state.pending_counts_by_channel.clear();
        self.state.events.clear();
        self.state.selected_channel_id = None;
        self.state.selected_event_id = None;
        self.known_pending_event_ids.clear();
        self.has_loaded_pending_snapshot = false;
    }

    fn visible_events_for_selected_channel(&self, events: Vec<Event>) -> Vec<Event> {
        if let Some(channel_id) = self.state.selected_channel_id.as_deref() {
            visible_events(
                events
                    .into_iter()
                    .filter(|event| event.channel_id == channel_id)
                    .collect(),
            )
        } else {
            Vec::new()
        }
    }

    fn ensure_selected_event(&mut self) {
        if self
            .state
            .selected_event_id
            .as_deref()
            .map(|id| self.state.events.iter().any(|event| event.id == id))
            .unwrap_or(false)
        {
            return;
        }
        self.state.selected_event_id = self.state.events.first().map(|event| event.id.clone());
    }
}

fn count_pending_by_channel(events: &[Event]) -> BTreeMap<String, usize> {
    let mut counts = BTreeMap::new();
    for event in events {
        *counts.entry(event.channel_id.clone()).or_insert(0) += 1;
    }
    counts
}

fn visible_events(mut events: Vec<Event>) -> Vec<Event> {
    events.sort_by(|lhs, rhs| {
        match (&lhs.status, &rhs.status) {
            (EventStatus::Pending, EventStatus::Pending)
            | (EventStatus::Resolved, EventStatus::Resolved)
            | (EventStatus::Expired, EventStatus::Expired) => {}
            (EventStatus::Pending, _) => return std::cmp::Ordering::Less,
            (_, EventStatus::Pending) => return std::cmp::Ordering::Greater,
            _ => {}
        }
        rhs.created_at
            .cmp(&lhs.created_at)
            .then_with(|| rhs.id.cmp(&lhs.id))
    });
    let mut handled = 0;
    events
        .into_iter()
        .filter(|event| {
            if event.status == EventStatus::Pending {
                return true;
            }
            handled += 1;
            handled <= HANDLED_EVENT_DISPLAY_LIMIT
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use chrono::{TimeZone, Utc};

    use super::*;
    use crate::models::{ActionResolution, Event};

    fn event(id: &str, channel_id: &str, status: EventStatus) -> Event {
        Event {
            id: id.to_string(),
            channel_id: channel_id.to_string(),
            recipients: Vec::new(),
            action_resolution: ActionResolution::Shared,
            title: id.to_string(),
            summary: String::new(),
            body_markdown: String::new(),
            fields: Vec::new(),
            links: Vec::new(),
            image_url: None,
            priority: 5,
            privacy: "private".to_string(),
            dedupe_key: None,
            expires_at: None,
            status,
            created_at: Utc.with_ymd_and_hms(2026, 5, 28, 12, 0, 0).unwrap(),
            updated_at: Utc.with_ymd_and_hms(2026, 5, 28, 12, 0, 0).unwrap(),
            resolved_at: None,
            result: None,
            user_results: Vec::new(),
            callback_url: None,
            actions: Vec::new(),
        }
    }

    #[test]
    fn refresh_suppresses_initial_notification_snapshot() {
        let mut reducer = StateReducer::new(Vec::new(), None, "default".to_string());
        reducer.state.selected_channel_id = Some("default".to_string());
        let candidates = reducer.apply_refresh(
            None,
            Vec::new(),
            Vec::new(),
            vec![event("a", "default", EventStatus::Pending)],
        );
        assert!(candidates.is_empty());
        let candidates = reducer.apply_refresh(
            None,
            Vec::new(),
            Vec::new(),
            vec![
                event("a", "default", EventStatus::Pending),
                event("b", "default", EventStatus::Pending),
            ],
        );
        assert_eq!(candidates.len(), 1);
        assert_eq!(candidates[0].id, "b");
    }

    #[test]
    fn event_update_reduces_pending_count_when_resolved() {
        let mut reducer = StateReducer::new(Vec::new(), None, "default".to_string());
        reducer.state.selected_channel_id = Some("default".to_string());
        let pending = event("a", "default", EventStatus::Pending);
        reducer.apply_refresh(None, Vec::new(), Vec::new(), vec![pending.clone()]);
        assert_eq!(
            reducer.state.pending_counts_by_channel.get("default"),
            Some(&1)
        );

        let mut resolved = pending;
        resolved.status = EventStatus::Resolved;
        reducer.apply_event_update(resolved);

        assert_eq!(reducer.state.pending_counts_by_channel.get("default"), None);
        assert_eq!(reducer.state.events[0].status, EventStatus::Resolved);
    }
}
