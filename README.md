# AiProxy

A small local proxy that exposes your GitHub Copilot subscription through an
OpenAI- and Ollama-shaped HTTP API, so you can point tools like VS Code,
editors, or scripts at `http://localhost:11434` and bring your own AI models.

## Commands

```text
AiProxy                       Start proxy mode (default).
AiProxy connect [provider]    Run the connect workflow (default: copilot).
AiProxy logout  [provider]    Remove stored auth state (default: copilot).
AiProxy help                  Show usage.
```

Typical first-run flow:

```pwsh
dotnet run -- connect      # GitHub device-flow login + pick the models you want
dotnet run                 # start the proxy on ListenUrl
```

To sign out and delete the stored token:

```pwsh
dotnet run -- logout
```

## Configuration

Edit [appsettings.json](appsettings.json) (or `appsettings.Development.json`):

- `ListenUrl` — address the proxy binds to. Default `http://localhost:11434`
  (the Ollama port, so Ollama-aware tools work out of the box).
- `ProxyApiKey` — optional. If set, clients must send it as a bearer token.
- `Copilot:ClientId` / `Copilot:UpstreamBaseUrl` — Copilot device-flow client
  and upstream API base. The defaults work for normal Copilot accounts.
- `Apis:Ollama` / `Apis:OpenAi` — toggle which API surfaces are exposed.

Auth state (GitHub OAuth token, Copilot bearer, selected models) is stored
locally and encrypted with Windows DPAPI — see [Storage/DpapiTokenStore.cs](Storage/DpapiTokenStore.cs).
