# Wardrobe Manager

Standalone product extracted from GenPrompt: **Wardrobe Intelligence** as its
own full-stack app. Photograph your clothes and AI catalogues every garment;
outfits are styled from what you actually own. Full-stack duplicate — nothing
here is shared with the parent app at runtime.

| Piece | Path | Port |
|---|---|---|
| Frontend | `frontend/` (React 19 + Vite 7) | **5274** (strictPort) |
| Backend | `backend/WardrobeManager.sln` (.NET 8) | **5321** |

The Vite dev server proxies `/api` → `http://localhost:5321`.
Ports deliberately differ from the parent app (5173/5220) and its sibling
extractions so all stacks run side by side.

## What's inside

**Catalogue** — photograph clothes (camera or upload) and AI catalogues every
garment in the shot: name, category, colours, pattern, style, seasons,
formality, condition, and a care tip. Several pieces in one photo are each
catalogued separately.

**Outfits** — "what should I wear" suggestions built only from your own
wardrobe, matched to the live weather where you are (browser location or a
typed city — Open-Meteo, no key) and the occasion. The last 5 looks are saved.
No clothes catalogued yet? Capsule mode styles from your preferences instead.

**Styling profile** — one full-body photo is analyzed in memory for fit,
colour, and proportion notes, then immediately discarded; only the notes are
kept. Style preferences (presentation, skin tone, colours you love/avoid)
shape every suggestion.

**Age tracking & responsible discard** — garments carry a purchase date; tired
pieces (3+ years) are flagged, and the discard flow suggests repair, resale,
donation, or textile recycling tailored to the item before marking it gone.

**Pack for a trip** — `POST /api/wardrobe/packing-list` surfaced as a card on
the wardrobe page: type a destination, nights, and travel month, and get a
packing list naming the specific garments in your closet plus what you're
missing for the season.

Carried over intact: email/password auth with sliding JWT refresh (expired
tokens accepted within a 14-day grace window), CSRF FrontendGuard, per-user
rate limiting (`ai-generation` bucket), SQLite dev / PostgreSQL prod switch.

Deliberately left behind: OAuth (Google/Microsoft), knowledge weaver, module
voice chat, the 3D gamefield, i18n, the travel-guide module (its packing-list
pairing survives here with typed trip details), web search.

## Running it

Backend (terminal 1):

```bash
cd backend/WardrobeManager.Api
ASPNETCORE_URLS=http://localhost:5321 dotnet run --no-launch-profile
```

First run creates `wardrobe.db` from scratch — there is no migration chain and
no seed data; register an account through the UI. Without `Jwt__Key` set, dev
mode generates a per-run key (sessions die on restart).

Frontend (terminal 2):

```bash
cd frontend
npm install
npm run dev     # http://localhost:5274 — fails loudly if the port is taken
```

## Configuration (backend/WardrobeManager.Api/appsettings.json)

Copy `appsettings.example.json` → `appsettings.json`. Keys:

| Key | Needed for | Without it |
|---|---|---|
| `AzureOpenAI:*` or `OpenAI:ApiKey` | garment vision, outfits, discard advice, packing list | AI endpoints return 503 |
| `Jwt:Key` | stable sessions | dev: random per-run key; prod: refuses to start |

Open-Meteo (geocoding + live weather for outfit suggestions) needs no key.

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
LocalMode__Enabled=true ASPNETCORE_URLS=http://localhost:5321 dotnet run --no-launch-profile
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
cd backend/WardrobeManager.Api && dotnet build   # compile check
cd frontend && npx tsc -b && npm run build       # type check + bundle
```

No unit tests, same as the parent repo.

## Schema notes

Only `Users` is an EF entity (`EnsureCreated` on boot — safe from empty, unlike
the parent's migration chain). Everything else (`WardrobeItems`,
`WardrobeProfiles`, `WardrobeOutfits`) is raw SQL `CREATE TABLE IF NOT EXISTS`,
ensured lazily inside the controller, so a wiped database heals itself on the
next request.
