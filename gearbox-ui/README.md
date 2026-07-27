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
| MSVC C++ build tools | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"` |
| WebView2 runtime | preinstalled on Windows 11 |
| Tauri CLI | `cargo install tauri-cli --version "^2.0" --locked` |

Node is only needed to regenerate the icon, and only from the standard library — there are no npm
dependencies.

> If linking fails with `LNK1104: cannot open file 'msvcrt.lib'`, `rustc` picked a `link.exe` from a
> Visual Studio install without the CRT/SDK libraries. Build from a *Developer PowerShell*, or load
> the environment into the current shell first:
>
> ```powershell
> $vcvars = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
> cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
>   if ($_ -match '^(.*?)=(.*)$') { Set-Item -Path "env:$($matches[1])" -Value $matches[2] }
> }
> ```

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
