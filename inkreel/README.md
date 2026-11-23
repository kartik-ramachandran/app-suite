# InkReel

Napkin-style visual documents **plus the video enhancement**: turn a document —
and the images inside it — into a narrated video. Standalone full stack,
extracted from GenPrompt's Inkling with nothing shared at runtime.

| Piece | Path | Port |
|---|---|---|
| Frontend | `frontend/` (React 19 + Vite 7) | **5275** (strictPort) |
| Backend | `backend/InkReel.sln` (.NET 8) | **5322** |

## Features

**Documents (`/ink`)** — research a topic or paste text into a Markdown
document; highlight any passage and AI ranks 6 visual formats for it (flowchart,
mind map, cycle, timeline, SWOT, …); the chosen visual embeds inline. Restyle,
re-tone, flip variants, PNG per visual, Word / MMD / HTML / print-to-PDF export,
deck mode with PPTX export, up to 100 saved documents per user.

**Video Studio (`/video`)** — the enhancement:

| Setting | Options |
|---|---|
| Source | Upload PDF/DOCX/MD/TXT (with page range), a saved InkReel document, or a written description |
| Length | 30s, 1, 1.5, 2, 3, 4 min — drives scene count and narration word budget (~150 wpm) |
| Slide style | White on Navy, Clean Light, Hand-Drawn Education, Bauhaus Poster, Earthy Organic, Duotone |
| Brand colours | 8 preset palettes + custom primary/accent |
| Logo | PNG/WebP/JPEG ≤2 MB, top-left of every slide |
| Voice | 8 named voices with a vibe (Friendly, Gravelly, Firm, Informative, …) → Azure neural voices |
| Narration style | Explainer, Storyteller, Energetic, Calm |

**Images from the document:** PDF page images (PdfPig) and DOCX image parts are
extracted; the script writer is told how many figures exist and assigns them to
the scenes that discuss them; those slides render the figure on the right half.

### Pipeline

```
upload → DocumentExtractor (text + figures, page range)
       → VideoScriptService  (AI: N scenes, title/bullets/narration/imageIndex)
       → SpeechService       (Azure TTS per scene → MP3)
       → SlideRenderService  (SkiaSharp 1920×1080 PNG per scene)
       → ffmpeg              (per-scene MP4 timed to its narration, then concat)
       → videos/{jobId}/video.mp4
```

Runs in a `BackgroundService` off a bounded in-process queue; the page polls
job status. ffmpeg is called with an argument list, never a shell string.

### Cost guardrails (enforced server-side)

- Signed-in users only — no anonymous video generation
- One render in flight per user (409 otherwise)
- 20 videos per user per 24 hours (429 otherwise)
- Uploads ≤40 MB, logos ≤2 MB, source text capped at 24k chars into the script

## Running it

Requires **ffmpeg + ffprobe** on PATH (or set `Video:FfmpegPath` / `Video:FfprobePath`).

```bash
cd backend/InkReel.Api
ASPNETCORE_URLS=http://localhost:5322 dotnet run --no-launch-profile
```

```bash
cd frontend
npm install
npm run dev     # http://localhost:5275
```

## Configuration (`backend/InkReel.Api/appsettings.json`)

Copy `appsettings.example.json` → `appsettings.json`.

| Key | Needed for | Without it |
|---|---|---|
| `AzureOpenAI:*` or `OpenAI:ApiKey` | documents, visuals, video scripts | AI endpoints return 503 |
| `AzureSpeech:SubscriptionKey` + `Region` | video narration | videos render as timed silent slides; UI says so |
| `BraveSearch:ApiKey` | web-grounded research | falls back to DuckDuckGo instant answers |
| `Video:OutputDir` | where MP4s are written | defaults to `backend/InkReel.Api/videos/` |
| `Jwt:Key` | stable sessions | dev: random per-run key; prod: refuses to start |

## What phones home

Azure OpenAI / OpenAI (completions), Azure Speech (TTS), Brave Search or
DuckDuckGo (research), Google Fonts (handwriting faces in the browser). No
telemetry, analytics, or ads.

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
LocalMode__Enabled=true ASPNETCORE_URLS=http://localhost:5322 dotnet run --no-launch-profile
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
cd backend/InkReel.Api && dotnet build
cd frontend && npx tsc -b && npm run build
```

No unit tests, same as the parent repo. Not yet run end-to-end.
