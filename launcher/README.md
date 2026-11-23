# App Launcher

A desktop app (Electron) that lists every standalone app in this repo, starts
and stops them, and lets you talk to the **Claude CLI** installed on this
computer — from the desktop, or from your phone on the same Wi-Fi.

```bash
cd launcher
npm install
npm start
```

## What it does

**Apps tab** — all 29 apps (listed in `launcher/apps.json`; ports are read
from each app's own config). For each one:

- **Start** runs its backend (`dotnet run`) and frontend (`npm run dev`) on its
  own ports. It is always a fresh start: anything already holding *that app's*
  two ports (an old dev server, a crashed run) is stopped first, and the log
  says what was stopped. No other port is touched. The first start installs
  the frontend's packages.
- **Restart** does the same while the app is running.
- **Open** opens it in your browser. Apps started here run in **local mode**:
  no sign-in, and they only answer on this computer (enforced in each app's
  `Middleware/LocalMode.cs`).
- **Logs** shows the last 400 lines of backend and frontend output.
- **Ask Claude** opens a Claude chat pointed at that app's folder.
- Each app keeps its own database under the launcher's data folder
  (`%APPDATA%/standalone-launcher/app-data/<app>/app.db`), separate from any
  database you made by hand.

**Claude tab** — chat with Claude Code (`claude -p`, streamed). Pick a folder
(the whole repo, or one app) and a mode:

| Mode | What Claude can do |
|---|---|
| Read-only (plan) — default | Read the code, explain, plan. Changes nothing. |
| Can edit files | Edit files in the chosen folder. |

The launcher never uses `bypassPermissions`, so Claude can't run commands
without asking. Follow-up messages continue the same Claude session.

**Settings tab**

- **AI keys for the apps:** Claude, Azure OpenAI and OpenAI keys, plus which
  provider is preferred. Stored on this computer only (the launcher's data
  folder, never the repo) and passed to each app as environment variables when
  it starts. The window never reads a saved key back; it only shows whether
  one is set.
- **Phone access:** see below.

## Using it from your phone

Settings → **Turn on phone access** shows a pairing code and a link. Open the
link on a phone on the same Wi-Fi; the same launcher appears, sized for the
phone.

Guardrails (all in `remote.js`):

- Every request needs the pairing token. The link swaps it for an
  HttpOnly cookie. Turning phone access off and on again makes a new token,
  so old links and paired phones stop working.
- Only devices on your own network (private IP ranges) are served; anything
  else gets 403, so a router port-forward can't expose it.
- From the phone, Claude is **read-only** unless you tick *Let Claude edit
  files when asked from the phone* on the computer.
- Settings and phone access can only be changed on the computer.
- The apps themselves stay localhost-only. From the phone you start, stop
  and watch them, and talk to Claude; you open them on the computer.

## Requirements

- Node.js and the .NET 8 SDK (to run the apps)
- Claude Code installed, with `claude` working in a terminal (for the
  Claude tab). The launcher finds it on your PATH.

## Files

| File | What it is |
|---|---|
| `main.js` | Electron main process; one API for the window and the phone |
| `apps.js` | Builds the app list from the repo |
| `processes.js` | Starts and stops apps; kills the whole process tree so ports are freed |
| `claude.js` | Bridge to the Claude CLI (stream-json) |
| `remote.js` | Phone access server |
| `preload.js` | The window's only bridge to Node (context isolation on, Node off) |
| `renderer/` | The UI; the same page runs on the desktop and the phone |
