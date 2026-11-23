# Colony

See every coding-agent session on this machine as a living crew.

A standalone full-stack app: React + Vite frontend, .NET 8 backend. Part of this app suite —
see the [suite README](../README.md) and the [App Launcher](../launcher/README.md).

| Piece | Path | Port |
|---|---|---|
| Frontend | `frontend/` (React 19 + Vite 7) | **5301** (strictPort) |
| Backend | `backend/Colony.Api.sln` (.NET 8) | **5351** |

## Pages

| Route | Nav label | Source |
|---|---|---|
| `/colony` | Colony | `Colony` |

## Backend

- Controllers: ColonyController
- EF entities (1): User
- Raw-SQL tables bootstrapped at startup: none (controllers self-ensure)
- Background services: none
- 4 source files copied from the parent

## Running it

```bash
cd backend/Colony.Api
cp appsettings.example.json appsettings.json   # then fill in keys
ASPNETCORE_URLS=http://localhost:5351 dotnet run --no-launch-profile
```

```bash
cd frontend
npm install
npm run dev     # http://localhost:5301
```

Fresh SQLite database (`colony.db`) on first run — register an account through the UI.

## Differences from the parent app

- KnowledgeWeaverService (the cross-module knowledge graph) is removed: items are no longer mirrored into a Cosmos graph.
- Auth is email/password only (no Google/Microsoft OAuth, referrals, or password-reset email).
- Links to modules that live in other apps redirect to this app's home page.
- Every API prefix the frontend calls is served by this backend.

## AI providers

Every AI feature runs on **Claude**, **Azure OpenAI** or **OpenAI** — fill in any
one (or more) in `appsettings.json`, or set them as environment variables:

| Provider | Settings | Environment variables |
|---|---|---|
| Claude | `Anthropic:ApiKey`, `Anthropic:Model` (default `claude-sonnet-5-5`) | `Anthropic__ApiKey`, `Anthropic__Model` |
| Azure OpenAI | `AzureOpenAI:Endpoint`, `AzureOpenAI:ApiKey`, `AzureOpenAI:DeploymentName` | `AzureOpenAI__Endpoint`, `AzureOpenAI__ApiKey`, `AzureOpenAI__DeploymentName` |
| OpenAI | `OpenAI:ApiKey`, `OpenAI:DefaultModel` (default `gpt-4o`) | `OpenAI__ApiKey`, `OpenAI__DefaultModel` |

`AI:PreferredProvider` (`Claude`, `Azure` or `OpenAI`) picks which one answers
when more than one is configured; otherwise the first configured of Claude,
Azure, OpenAI is used. The switch lives in `Services/AiChatClients.cs`; Claude
calls go through `Services/ClaudeChatClient.cs` (Anthropic Messages API, text +
images + PDFs + tools + streaming).

## Local mode (no sign-in)

For running this app on your own machine with your own AI keys:

```bash
LocalMode__Enabled=true ASPNETCORE_URLS=http://localhost:5351 dotnet run --no-launch-profile
```

The app opens straight in — no account, no password. One owner account is
created on first use (`LocalMode:OwnerName` sets its display name).

Local mode only works on this machine, and that is enforced in code
(`Middleware/LocalMode.cs`), not by a setting you could forget:

- the app **refuses to start** if it is listening on anything except
  localhost / 127.0.0.1 / ::1;
- any request that doesn't come from this machine gets **403**, so a proxy,
  tunnel or port-forward still can't put a no-sign-in app on the internet.

Turn it off (the default) and sign-in works as normal. The owner account has
no password, so it can't be signed in to once local mode is off.

## Build / verify

```bash
cd backend/Colony.Api && dotnet build
cd frontend && npx tsc -b && npm run build
```

No unit tests, same as the parent repo.
