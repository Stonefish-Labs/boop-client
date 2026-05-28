use std::{
    collections::BTreeMap,
    env,
    path::{Path, PathBuf},
};

use anyhow::{Context, Result};
use directories::ProjectDirs;
use serde::{Deserialize, Serialize};
use tokio::fs;

use crate::models::ServerProfile;

const KEYRING_SERVICE: &str = "boop-client-core";

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct PersistedConfig {
    #[serde(default)]
    pub servers: Vec<ServerProfile>,
    #[serde(default)]
    pub selected_server_id: Option<String>,
    #[serde(default = "default_notification_sound")]
    pub notification_sound: String,
    #[serde(default)]
    pub insecure_tokens: BTreeMap<String, String>,
}

#[derive(Debug, Clone)]
pub struct Store {
    path: PathBuf,
    allow_insecure_token_store: bool,
}

impl Store {
    pub fn new() -> Result<Self> {
        let state_dir = if let Ok(path) = env::var("BOOP_CLIENT_CORE_STATE_DIR") {
            PathBuf::from(path)
        } else {
            ProjectDirs::from("com", "Stonefish Labs", "Boop")
                .context("could not resolve user config directory")?
                .config_dir()
                .to_path_buf()
        };
        Ok(Self {
            path: state_dir.join("client-core.json"),
            allow_insecure_token_store: env::var("BOOP_CLIENT_CORE_INSECURE_TOKEN_STORE").is_ok(),
        })
    }

    pub async fn load(&self) -> Result<PersistedConfig> {
        if !self.path.exists() {
            return Ok(PersistedConfig::default());
        }
        let raw = fs::read(&self.path)
            .await
            .with_context(|| format!("read {}", self.path.display()))?;
        Ok(serde_json::from_slice(&raw)?)
    }

    pub async fn save(&self, mut config: PersistedConfig) -> Result<()> {
        if !self.allow_insecure_token_store {
            config.insecure_tokens.clear();
        }
        if let Some(parent) = self.path.parent() {
            fs::create_dir_all(parent).await?;
        }
        let raw = serde_json::to_vec_pretty(&config)?;
        fs::write(&self.path, raw)
            .await
            .with_context(|| format!("write {}", self.path.display()))?;
        Ok(())
    }

    pub async fn save_token(
        &self,
        config: &mut PersistedConfig,
        server_id: &str,
        token: &str,
    ) -> Result<()> {
        if self.allow_insecure_token_store {
            config
                .insecure_tokens
                .insert(server_id.to_string(), token.to_string());
            return Ok(());
        }
        let entry = keyring::Entry::new(KEYRING_SERVICE, &token_account(server_id))?;
        entry.set_password(token)?;
        Ok(())
    }

    pub fn load_token(&self, config: &PersistedConfig, server_id: &str) -> Option<String> {
        if self.allow_insecure_token_store {
            return config.insecure_tokens.get(server_id).cloned();
        }
        let entry = keyring::Entry::new(KEYRING_SERVICE, &token_account(server_id)).ok()?;
        entry.get_password().ok()
    }

    pub async fn delete_token(&self, config: &mut PersistedConfig, server_id: &str) -> Result<()> {
        config.insecure_tokens.remove(server_id);
        if self.allow_insecure_token_store {
            return Ok(());
        }
        if let Ok(entry) = keyring::Entry::new(KEYRING_SERVICE, &token_account(server_id)) {
            let _ = entry.delete_credential();
        }
        Ok(())
    }

    pub fn path(&self) -> &Path {
        &self.path
    }
}

fn token_account(server_id: &str) -> String {
    format!("serverToken.{server_id}")
}

fn default_notification_sound() -> String {
    "default".to_string()
}
