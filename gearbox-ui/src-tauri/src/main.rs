#![cfg_attr(all(not(debug_assertions), target_os = "windows"), windows_subsystem = "windows")]

//! Tiny desktop shifter for the AiProxy gearbox middleware.
//!
//! The proxy already exposes the gearbox as a small HTTP surface:
//!
//! * `GET  {base}/gearbox/state` — configured gears plus the engaged one
//! * `POST {base}/gearbox/shift` — `{ "position": "3" }` engages a gear ("N" = Neutral)
//!
//! This app is a thin, always-available client for that surface. All HTTP happens in Rust
//! (not in the webview) so the page stays under a strict `default-src 'self'` CSP and no
//! CORS/remote-origin permissions are needed.

use std::fs;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::Duration;

use serde::{Deserialize, Serialize};
use tauri::{Manager, State};

/// Matches `ListenUrl` in the proxy's appsettings.json.
const DEFAULT_BASE_URL: &str = "http://localhost:11434";

// ----------------------------------------------------------------------
// Wire types (mirror GearboxEndpoint.BuildState)
// ----------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Gear {
    position: String,
    #[serde(default)]
    label: Option<String>,
    #[serde(default)]
    model: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct GearboxState {
    enabled: bool,
    selected: String,
    #[serde(default = "neutral")]
    neutral: String,
    #[serde(default)]
    gears: Vec<Gear>,
}

fn neutral() -> String {
    "N".to_string()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Settings {
    base_url: String,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            base_url: DEFAULT_BASE_URL.to_string(),
        }
    }
}

/// Error body returned by the proxy's gearbox endpoints on 4xx.
#[derive(Debug, Deserialize)]
struct ApiError {
    error: String,
}

// ----------------------------------------------------------------------
// App state
// ----------------------------------------------------------------------

struct AppState {
    http: reqwest::Client,
    settings: Mutex<Settings>,
    settings_path: PathBuf,
}

impl AppState {
    /// The configured proxy root, without a trailing slash.
    fn base_url(&self) -> String {
        let settings = self.settings.lock().expect("settings mutex poisoned");
        settings.base_url.trim_end_matches('/').to_string()
    }
}

// ----------------------------------------------------------------------
// Commands
// ----------------------------------------------------------------------

#[tauri::command]
fn load_settings(state: State<'_, AppState>) -> Settings {
    state.settings.lock().expect("settings mutex poisoned").clone()
}

#[tauri::command]
fn save_settings(base_url: String, state: State<'_, AppState>) -> Result<Settings, String> {
    let trimmed = base_url.trim();
    let url = if trimmed.is_empty() {
        DEFAULT_BASE_URL.to_string()
    } else {
        trimmed.trim_end_matches('/').to_string()
    };

    // Reject anything the HTTP client could not use, so a typo surfaces here rather than as a
    // confusing "could not reach the proxy" on every later call.
    reqwest::Url::parse(&url).map_err(|e| format!("Invalid proxy URL: {e}"))?;

    let settings = Settings { base_url: url };
    {
        let mut guard = state.settings.lock().expect("settings mutex poisoned");
        *guard = settings.clone();
    }

    let json = serde_json::to_string_pretty(&settings).map_err(|e| e.to_string())?;
    fs::write(&state.settings_path, json)
        .map_err(|e| format!("Could not save settings: {e}"))?;

    Ok(settings)
}

#[tauri::command]
async fn fetch_state(state: State<'_, AppState>) -> Result<GearboxState, String> {
    let client = state.http.clone();
    let url = format!("{}/gearbox/state", state.base_url());

    let response = client
        .get(&url)
        .send()
        .await
        .map_err(|e| unreachable_message(&url, &e))?;

    read_state(response).await
}

#[tauri::command]
async fn shift(position: String, state: State<'_, AppState>) -> Result<GearboxState, String> {
    let client = state.http.clone();
    let url = format!("{}/gearbox/shift", state.base_url());

    let response = client
        .post(&url)
        .json(&serde_json::json!({ "position": position }))
        .send()
        .await
        .map_err(|e| unreachable_message(&url, &e))?;

    read_state(response).await
}

/// Turns a response into either the gearbox state or the proxy's own error text.
async fn read_state(response: reqwest::Response) -> Result<GearboxState, String> {
    let status = response.status();
    let body = response
        .text()
        .await
        .map_err(|e| format!("Could not read the proxy response: {e}"))?;

    if !status.is_success() {
        let detail = serde_json::from_str::<ApiError>(&body)
            .map(|e| e.error)
            .unwrap_or_else(|_| status.to_string());
        return Err(detail);
    }

    serde_json::from_str::<GearboxState>(&body)
        .map_err(|e| format!("Unexpected response from the proxy: {e}"))
}

fn unreachable_message(url: &str, error: &reqwest::Error) -> String {
    if error.is_timeout() {
        format!("The proxy at {url} did not respond in time.")
    } else {
        format!("Could not reach the proxy at {url}. Is it running?")
    }
}

// ----------------------------------------------------------------------
// Entry point
// ----------------------------------------------------------------------

fn main() {
    tauri::Builder::default()
        .setup(|app| {
            let dir = app.path().app_config_dir()?;
            fs::create_dir_all(&dir)?;
            let settings_path = dir.join("settings.json");

            // A missing or corrupt settings file is not worth failing startup over: fall back to
            // the proxy's own default listen URL, which the user can correct in the UI.
            let settings = fs::read_to_string(&settings_path)
                .ok()
                .and_then(|raw| serde_json::from_str::<Settings>(&raw).ok())
                .unwrap_or_default();

            let http = reqwest::Client::builder()
                .timeout(Duration::from_secs(10))
                .build()
                .unwrap_or_default();

            app.manage(AppState {
                http,
                settings: Mutex::new(settings),
                settings_path,
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            load_settings,
            save_settings,
            fetch_state,
            shift
        ])
        .run(tauri::generate_context!())
        .expect("failed to start the gearbox UI");
}
