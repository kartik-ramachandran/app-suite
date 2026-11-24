# Event Planner

Plan events, find gifts, and draft proposals with AI.

A standalone full-stack app: React + Vite frontend, .NET 8 backend. Part of this app suite —
see the [suite README](../README.md) and the [App Launcher](../launcher/README.md).

| Piece | Path | Port |
|---|---|---|
| Frontend | `frontend/` (React 19 + Vite 7) | **5287** (strictPort) |
| Backend | `backend/EventPlanner.Api.sln` (.NET 8) | **5337** |

## Pages

| Route | Nav label | Source |
|---|---|---|
| `/event-planner` | Event Planner | `EventPlanner` |
| `/gift-finder` | Gift Finder | `GiftFinder` |
| `/proposal` | Proposal Generator | `ProposalGenerator` |

## Backend

- Controllers: PdfController, PdfTemplateController, PlannerController
- EF entities (1): User
- Raw-SQL tables bootstrapped at startup: none (controllers self-ensure)
- Background services: none
- 21 source files copied from the parent

## Running it

```bash
cd backend/EventPlanner.Api
cp appsettings.example.json appsettings.json   # then fill in keys
ASPNETCORE_URLS=http://localhost:5337 dotnet run --no-launch-profile
```

```bash
cd frontend
npm install
npm run dev     # http://localhost:5287
```

Fresh SQLite database (`eventplanner.db`) on first run — register an account through the UI.

## Differences from the parent app

- KnowledgeWeaverService (the cross-module knowledge graph) is removed: items are no longer mirrored into a Cosmos graph.
- Auth is email/password only (no Google/Microsoft OAuth, referrals, or password-reset email).
- Links to modules that live in other apps redirect to this app's home page.
- Every API prefix the frontend calls is served by this backend.

## AI providers

Every AI feature runs on **Claude**, **Azure OpenAI**, **OpenAI**, or a **local
model through Ollama** (no API key, nothing leaves your computer). Fill in any one
(or more) in `appsettings.json`, or set them as environment variables:

| Provider | Settings | Environment variables |
|---|---|---|
| Claude | `Anthropic:ApiKey`, `Anthropic:Model` (default `claude-sonnet-5-5`) | `Anthropic__ApiKey`, `Anthropic__Model` |
| Azure OpenAI | `AzureOpenAI:Endpoint`, `AzureOpenAI:ApiKey`, `AzureOpenAI:DeploymentName` | `AzureOpenAI__Endpoint`, `AzureOpenAI__ApiKey`, `AzureOpenAI__DeploymentName` |
| OpenAI | `OpenAI:ApiKey`, `OpenAI:DefaultModel` (default `gpt-4o`) | `OpenAI__ApiKey`, `OpenAI__DefaultModel` |
| Ollama (local) | `Ollama:Enabled` = `true`, `Ollama:BaseUrl` (default `http://localhost:11434`), `Ollama:Model` (default `llama3.2`) | `Ollama__Enabled`, `Ollama__BaseUrl`, `Ollama__Model` |

`AI:PreferredProvider` (`Claude`, `Azure`, `OpenAI` or `Ollama`) picks which one
answers when more than one is configured; otherwise the first configured of
Claude, Azure, OpenAI, Ollama is used. The switch lives in `Services/AiChatClients.cs`;
Claude calls go through `Services/ClaudeChatClient.cs` (Anthropic Messages API,
text + images + PDFs + tools + streaming).

**Running free with Ollama:** install [Ollama](https://ollama.com), run
`ollama pull llama3.2`, then set `Ollama:Enabled=true` and
`AI:PreferredProvider=Ollama`. Features that read photos need a vision model
(for example `ollama pull llama3.2-vision`, then `Ollama:Model=llama3.2-vision`).
Small local models are slower and less accurate than the cloud ones, so expect
simpler answers. Voice transcription (Whisper) still needs an OpenAI key.

## Local mode (no sign-in)

For running this app on your own machine with your own AI keys:

```bash
LocalMode__Enabled=true ASPNETCORE_URLS=http://localhost:5337 dotnet run --no-launch-profile
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
cd backend/EventPlanner.Api && dotnet build
cd frontend && npx tsc -b && npm run build
```

No unit tests, same as the parent repo.
