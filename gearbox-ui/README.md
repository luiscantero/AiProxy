# Gearbox UI

A tiny desktop shifter for the AiProxy [gearbox middleware](../Pipeline/Middlewares/GearboxMiddleware.cs) —
the same H-pattern shifter the proxy serves at `/gearbox`, but as a native always-available window
instead of a browser tab.

It talks to the endpoints the proxy already exposes:

| Endpoint | Purpose |
| --- | --- |
| `GET  {proxy}/gearbox/state` | configured gears plus the engaged one |
| `POST {proxy}/gearbox/shift` | `{ "position": "3" }` engages a gear (`N` = Neutral) |

All HTTP happens in Rust, not in the webview, so the page runs under a strict `default-src 'self'`
CSP and the app needs no remote-origin or CORS permissions. The only non-core permission granted is
`core:window:allow-set-always-on-top`, for the **PIN** button.

## Using it

* Click a gear to route every request through that model; **N** returns to Neutral (the client's own
  model choice is honored).
* Number keys shift; `N` returns to Neutral.
* **PIN** keeps the window on top of the editor.
* **URL** sets the proxy address (default `http://localhost:11434`, matching `ListenUrl` in
  [appsettings.json](../appsettings.json)). It is saved to `settings.json` in the app config dir.
* The window re-polls every 5 seconds, so gears shifted from the browser UI show up here too.

The gearbox must be enabled in the proxy (`Gearbox.Enabled: true`); if it is not, the window says so.

## Prerequisites

| Requirement | Notes |
| --- | --- |
| Rust (stable, MSVC toolchain) | `winget install Rustlang.Rustup` |
| MSVC C++ build tools | in the Visual Studio installer's *Individual components*: **"MSVC Build Tools version 14.51"** (id `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`) — the standalone Build Tools installer works too |
| Windows SDK | comes with the component above |
| WebView2 runtime | preinstalled on Windows 11 |
| Tauri CLI | `cargo install tauri-cli --version "^2.0" --locked` |

Node is only needed to regenerate the icon, and only from the standard library — there are no npm
dependencies.

> `LNK1104: cannot open file 'msvcrt.lib'` means the C++ component above is missing: `rustc` found a
> `link.exe` (Visual Studio ships one for .NET native AOT) but no desktop CRT libraries. Check for
> `VC\Tools\MSVC\<version>\lib\x64` — if only `lib\onecore` is there, add the component. No developer
> prompt or `vcvars` script is needed once it is installed; `rustc` locates the toolchain itself.

## Build

```powershell
# 1. Icons (once, or after editing tools/make-icon.mjs).
#    tauri.conf.json expects src-tauri/icons/*, which this generates.
node tools/make-icon.mjs
cargo tauri icon icon-source.png

# 2. Run against a proxy started with `AiProxy proxy`.
cargo tauri dev

# 3. Installer + standalone exe in src-tauri/target/release/.
cargo tauri build
```

## Layout

| Path | What |
| --- | --- |
| `ui/` | the whole frontend: static HTML/CSS/JS, no bundler |
| `src-tauri/src/main.rs` | `fetch_state` / `shift` / settings commands |
| `src-tauri/tauri.conf.json` | window, CSP, bundle |
| `src-tauri/capabilities/default.json` | granted permissions |
| `tools/make-icon.mjs` | draws `icon-source.png` (raw PNG, no image deps) |
