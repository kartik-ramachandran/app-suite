# App Suite

29 standalone apps — each a full stack of its own (React + Vite frontend,
.NET 8 backend) — plus **App Launcher**, a desktop app that starts and stops
them and lets you chat with the Claude CLI on your computer (or from your phone
on the same Wi-Fi).

Every app:

- runs on **Claude**, **Azure OpenAI** or **OpenAI** — bring your own key;
- has a **local mode** with no sign-in for running on your own machine
  (it only answers on that machine, enforced in code);
- is independent: nothing is shared between apps at runtime.

## Quick start

You need [Node.js](https://nodejs.org) (20+) and the
[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). For the
Claude chat in the launcher, also [Claude Code](https://docs.anthropic.com/en/docs/claude-code).

```bash
git clone https://github.com/kartik-ramachandran/app-suite.git
cd app-suite/launcher
npm install
npm start
```

In the launcher: **Settings** → add an AI key (Claude, Azure OpenAI or
OpenAI) → **Apps** → **Start** on any app → **Open**. The first start of each
app installs its packages, so give it a minute.

### Running one app by hand

```bash
cd plant-doctor/backend/PlantDoctor.Api
LocalMode__Enabled=true Anthropic__ApiKey=<your key> dotnet run
# second terminal
cd plant-doctor/frontend && npm install && npm run dev
```

Each app's README lists its ports, settings and pages.

## The apps

| App | Frontend | Backend | What it does |
|---|---|---|---|
| [Travel Manager](travel-manager/README.md) | 5273 | 5320 | Trip planning, flight prices, live travel intel — paired with your wardrobe for packing lists. |
| [Wardrobe Manager](wardrobe-manager/README.md) | 5274 | 5321 | Catalogue clothes from photos, daily outfits from live weather, packing lists. |
| [InkReel](inkreel/README.md) | 5275 | 5322 | Napkin-style visual documents + Video Studio: documents and their images into narrated MP4s. |
| [Kitchen Manager](kitchen-manager/README.md) | 5276 | 5323 | Your pantry, your recipes, and allergy-aware cooking in one place. |
| [Money Manager](money-manager/README.md) | 5277 | 5324 | Bills, receipts, expenses, and warranties — snapped once and tracked for you. |
| [Study Manager](study-manager/README.md) | 5278 | 5325 | Turn any topic or notes into 50 learning formats with spaced-repetition review. |
| [Life Manager](life-manager/README.md) | 5279 | 5326 | Check-ins, goals, decisions, relationships, voice journal, and your medicine cabinet. |
| [Plant Doctor](plant-doctor/README.md) | 5280 | 5330 | Snap a plant to identify it, diagnose problems, and get a care plan with watering reminders. |
| [Card Scanner](card-scanner/README.md) | 5281 | 5331 | Snap a business card and get a ready-to-save contact. |
| [Label Scanner](label-scanner/README.md) | 5282 | 5332 | Snap a food or product label and get a verdict against your diet and allergies. |
| [Price Checker](price-checker/README.md) | 5283 | 5333 | Snap a product and compare what it costs elsewhere. |
| [Room Redesign](room-redesign/README.md) | 5284 | 5334 | Photograph a room and get a redesign plan. |
| [Email Tasks](email-tasks/README.md) | 5285 | 5335 | Turn an email or screenshot into a clean task list. |
| [Whiteboard Digitizer](whiteboard/README.md) | 5286 | 5336 | Turn a photo of a whiteboard or handwritten page into clean notes and action items. |
| [Event Planner](event-planner/README.md) | 5287 | 5337 | Plan events, find gifts, and draft proposals with AI. |
| [Book Reader](book-reader/README.md) | 5288 | 5338 | Upload a book and hear it read in a cast of character voices. |
| [Meeting Manager](meeting-manager/README.md) | 5289 | 5339 | Calendar sync, live meeting capture, follow-ups, and availability. |
| [Interaction Intelligence](interaction-intelligence/README.md) | 5290 | 5340 | Turn any recorded call or consult into transcripts, summaries, and next steps. |
| [Prism](prism/README.md) | 5291 | 5341 | Upload a messy spreadsheet and let AI agents profile, map, and clean it. |
| [PDF Tools](pdf-tools/README.md) | 5292 | 5342 | Convert, merge, compare, fill, redact and archive PDFs. |
| [EMMD Studio](emmd-studio/README.md) | 5293 | 5343 | Write, seal, and verify Enhanced Markdown documents. |
| [Cosmos](cosmos/README.md) | 5294 | 5344 | Your knowledge cosmos: everything you capture, linked into one graph. |
| [Research Lab](research/README.md) | 5295 | 5345 | Deep research and agent swarms on any question. |
| [Agent Hub](agent-hub/README.md) | 5296 | 5346 | Build agents, chat with bots, orchestrate runs, and connect MCP tools. |
| [Prompt Studio](prompt-studio/README.md) | 5297 | 5347 | The prompt operating system: build, version, test, chain, and evaluate prompts. |
| [Media Studio](media-studio/README.md) | 5298 | 5348 | Generate images and media with AI. |
| [Content Studio](content-studio/README.md) | 5299 | 5349 | Turn one idea into a full pack of social posts. |
| [Kit Builder](kit-builder/README.md) | 5300 | 5350 | Assemble your company's AI kit and workspace. |
| [Colony](colony/README.md) | 5301 | 5351 | See every coding-agent session on this machine as a living crew. |

## Settings every app understands

| Setting (env var) | What it does |
|---|---|
| `Anthropic__ApiKey`, `Anthropic__Model` | Claude |
| `AzureOpenAI__Endpoint`, `AzureOpenAI__ApiKey`, `AzureOpenAI__DeploymentName` | Azure OpenAI |
| `OpenAI__ApiKey`, `OpenAI__DefaultModel` | OpenAI |
| `AI__PreferredProvider` | `Claude`, `Azure` or `OpenAI` — which one answers when more than one is set |
| `LocalMode__Enabled` | `true` = no sign-in, this machine only |

Copy `appsettings.example.json` to `appsettings.json` in an app's backend
folder to set these in a file instead. `appsettings.json` is git-ignored, so
your keys never get committed.

## Licence

MIT — see [LICENSE](LICENSE).
