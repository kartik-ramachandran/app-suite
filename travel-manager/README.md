# Travel Manager

Standalone product extracted from GenPrompt: **Travel Intelligence + Wardrobe
Intelligence**, paired so a trip plan can be packed from the clothes you
actually own. Full-stack duplicate — nothing here is shared with the parent
app at runtime.

| Piece | Path | Port |
|---|---|---|
| Frontend | `frontend/` (React 19 + Vite 7) | **5273** (strictPort) |
| Backend | `backend/TravelManager.sln` (.NET 8) | **5320** |

The Vite dev server proxies `/api` → `http://localhost:5320`.
Ports deliberately differ from the parent app (5173/5220) so both stacks run
side by side.

## What's inside

**Travel** — AI travel guide per destination + dates: real weather from
Open-Meteo (forecast ≤16 days out, last-year seasonal proxy beyond), web-grounded
things-to-do and dietary-aware food picks, local dress guidance, saved plans
(last 5), best-months-to-fly via Travelpayouts, HTML guide download.

**Wardrobe** — photograph clothes and AI catalogues every garment in the shot;
outfit suggestions from what you own (live weather via browser location or
typed city); body-shape styling profile (photo analyzed, never stored); garment
age tracking; responsible-discard advice.

**The pairing** — `POST /api/wardrobe/packing-list`, rendered inside a generated
travel guide: a packing list naming the specific garments in your closet and
what you're missing for the forecast.

Carried over intact: email/password auth with sliding JWT refresh (expired
tokens accepted within a 14-day grace window), CSRF FrontendGuard, per-user
rate limiting (`ai-generation` bucket), SQLite dev / PostgreSQL prod switch.

Deliberately left behind: OAuth (Google/Microsoft), calendar sync, knowledge
weaver, module voice chat, the 3D gamefield, i18n, PDF export, electron.

## Running it

Backend (terminal 1):

```bash
cd backend/TravelManager.Api
ASPNETCORE_URLS=http://localhost:5320 dotnet run --no-launch-profile
```

First run creates `travel.db` from scratch — there is no migration chain and no
seed data; register an account through the UI. Without `Jwt__Key` set, dev mode
generates a per-run key (sessions die on restart).

Frontend (terminal 2):

```bash
cd frontend
npm install
npm run dev     # http://localhost:5273 — fails loudly if the port is taken
```

## Configuration (backend/TravelManager.Api/appsettings.json)

Copy `appsettings.example.json` → `appsettings.json`. Keys:

| Key | Needed for | Without it |
|---|---|---|
| `AzureOpenAI:*` or `OpenAI:ApiKey` | guide text, garment vision, outfits, packing list | AI endpoints return 503 |
| `BraveSearch:ApiKey` | web-grounded attractions/food | falls back to DuckDuckGo instant answers |
| `Travelpayouts:Token` | best-months-to-fly fares | feature reports "not configured", guide still works |
| `Jwt:Key` | stable sessions | dev: random per-run key; prod: refuses to start |

Open-Meteo (geocoding + weather) needs no key.

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
LocalMode__Enabled=true ASPNETCORE_URLS=http://localhost:5320 dotnet run --no-launch-profile
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
cd backend/TravelManager.Api && dotnet build   # compile check
cd frontend && npx tsc -b && npm run build     # type check + bundle
```

No unit tests, same as the parent repo.

## Schema notes

Only `Users` is an EF entity (`EnsureCreated` on boot — safe from empty, unlike
the parent's migration chain). Everything else (`UserTravelPrefs`,
`TravelGuides`, `WardrobeItems`, `WardrobeProfiles`, `WardrobeOutfits`) is raw
SQL `CREATE TABLE IF NOT EXISTS`, ensured at startup and again lazily inside the
controllers, so a wiped database heals itself on the next request.
