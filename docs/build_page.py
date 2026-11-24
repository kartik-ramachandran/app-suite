"""Builds docs/index.html from launcher/apps.json + the three hand-built apps.

Run from the repo root:  python docs/build_page.py
"""
import html
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
REPO = "https://github.com/kartik-ramachandran/app-suite"

HAND_BUILT = [
    {"slug": "travel-manager", "title": "Travel Manager", "icon": "bi-airplane",
     "palette": ["#0284c7"], "fePort": 5273, "bePort": 5320,
     "blurb": "Trip planning, flight prices and live travel intel, with packing lists built from the clothes you own."},
    {"slug": "wardrobe-manager", "title": "Wardrobe Manager", "icon": "bi-handbag",
     "palette": ["#9333ea"], "fePort": 5274, "bePort": 5321,
     "blurb": "Catalogue your clothes from photos and get daily outfits matched to the live weather."},
    {"slug": "inkreel", "title": "InkReel", "icon": "bi-film",
     "palette": ["#e11d48"], "fePort": 5275, "bePort": 5322,
     "blurb": "Napkin-style visual documents, plus Video Studio: turn a document and its images into a narrated video."},
]

GROUPS = [
    ("Everyday life", ["travel-manager", "wardrobe-manager", "kitchen-manager", "money-manager",
                       "life-manager", "plant-doctor", "event-planner", "room-redesign"]),
    ("Snap it", ["card-scanner", "label-scanner", "price-checker", "email-tasks", "whiteboard"]),
    ("Learn & read", ["study-manager", "book-reader", "research", "cosmos"]),
    ("Create", ["inkreel", "media-studio", "content-studio", "prompt-studio", "emmd-studio"]),
    ("Work & data", ["meeting-manager", "interaction-intelligence", "prism",
                     "agent-hub", "kit-builder", "colony"]),
]


def load_apps():
    generated = json.loads((ROOT / "launcher" / "apps.json").read_text(encoding="utf-8"))["apps"]
    apps = {a["slug"]: a for a in HAND_BUILT + generated}
    grouped = [s for _, slugs in GROUPS for s in slugs]
    missing = set(apps) - set(grouped)
    unknown = set(grouped) - set(apps)
    if missing or unknown:
        raise SystemExit(f"group mismatch: missing={missing} unknown={unknown}")
    return apps


def card(a):
    e = html.escape
    colour = a["palette"][0]
    return f"""
      <a class="card" href="{REPO}/tree/main/{e(a['slug'])}" style="--c:{e(colour)}">
        <span class="ico"><i class="bi {e(a['icon'])}"></i></span>
        <h3>{e(a['title'])}</h3>
        <p>{e(a['blurb'])}</p>
        <span class="meta">:{a['fePort']} · :{a['bePort']} <b>View code →</b></span>
      </a>"""


def main():
    apps = load_apps()
    sections = []
    for name, slugs in GROUPS:
        cards = "".join(card(apps[s]) for s in slugs)
        sections.append(f"""
    <section class="group">
      <h2>{html.escape(name)} <small>{len(slugs)}</small></h2>
      <div class="grid">{cards}
      </div>
    </section>""")

    page = TEMPLATE.replace("{{COUNT}}", str(len(apps))) \
                   .replace("{{REPO}}", REPO) \
                   .replace("{{SECTIONS}}", "".join(sections))
    (ROOT / "docs" / "index.html").write_text(page, encoding="utf-8")
    print(f"wrote docs/index.html with {len(apps)} apps")


TEMPLATE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>App Suite — {{COUNT}} free, open-source AI apps</title>
<meta name="description" content="{{COUNT}} standalone AI apps plus a desktop launcher. Run them on your own machine with Claude, Azure OpenAI or OpenAI. Free and open source.">
<meta property="og:title" content="App Suite — {{COUNT}} free, open-source AI apps">
<meta property="og:description" content="Run {{COUNT}} AI apps on your own machine, with your own key. No sign-in, no cloud account.">
<link rel="icon" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect width='100' height='100' rx='24' fill='%23ff4d2e'/><text x='50' y='68' font-size='56' text-anchor='middle' fill='white' font-family='Arial' font-weight='900'>A</text></svg>">
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css">
<style>
  :root {
    --ink: #14110f; --paper: #fffaf3; --muted: #5b524a;
    --hot: #ff4d2e; --sun: #ffc531; --sky: #2f6bff; --mint: #12b886; --line: #14110f;
  }
  * { box-sizing: border-box; }
  html { scroll-behavior: smooth; }
  body { margin: 0; background: var(--paper); color: var(--ink);
         font: 17px/1.55 "Segoe UI", system-ui, -apple-system, Helvetica, Arial, sans-serif; }
  a { color: inherit; }
  .wrap { max-width: 1180px; margin: 0 auto; padding: 0 24px; }

  nav { position: sticky; top: 0; z-index: 10; background: var(--paper); border-bottom: 3px solid var(--line); }
  nav .wrap { display: flex; align-items: center; gap: 22px; height: 64px; }
  .logo { font-weight: 900; font-size: 22px; text-decoration: none; display: flex; gap: 10px; align-items: center; }
  .logo span { background: var(--hot); color: #fff; width: 34px; height: 34px; border-radius: 10px;
               display: grid; place-items: center; border: 3px solid var(--line); }
  nav .links { margin-left: auto; display: flex; gap: 20px; font-weight: 700; }
  nav .links a { text-decoration: none; }
  nav .links a:hover { color: var(--hot); }

  .btn { display: inline-flex; align-items: center; gap: 10px; padding: 14px 24px; font-weight: 800;
         text-decoration: none; border: 3px solid var(--line); border-radius: 14px; box-shadow: 5px 5px 0 var(--line);
         transition: transform .12s, box-shadow .12s; }
  .btn:hover { transform: translate(-2px, -2px); box-shadow: 7px 7px 0 var(--line); }
  .btn.primary { background: var(--hot); color: #fff; }
  .btn.alt { background: #fff; }

  header.hero { padding: 76px 0 64px; background:
      radial-gradient(circle at 88% 18%, var(--sun) 0 120px, transparent 121px),
      radial-gradient(circle at 6% 92%, #ffd9cf 0 150px, transparent 151px); border-bottom: 3px solid var(--line); }
  .badge { display: inline-block; background: var(--mint); color: #fff; font-weight: 800; padding: 6px 14px;
           border: 3px solid var(--line); border-radius: 999px; transform: rotate(-2deg); }
  h1 { font-size: clamp(44px, 8vw, 92px); line-height: .95; letter-spacing: -2px; margin: 22px 0 20px; font-weight: 900; }
  h1 em { font-style: normal; color: var(--hot); }
  h1 u { text-decoration-color: var(--sky); text-decoration-thickness: 10px; text-underline-offset: 6px; }
  .lede { font-size: 21px; max-width: 720px; color: var(--muted); margin: 0 0 32px; }
  .cta { display: flex; flex-wrap: wrap; gap: 16px; }

  .stats { display: grid; grid-template-columns: repeat(4, 1fr); gap: 0; border-bottom: 3px solid var(--line); }
  .stat { padding: 26px 24px; border-right: 3px solid var(--line); }
  .stat:last-child { border-right: 0; }
  .stat b { display: block; font-size: 40px; font-weight: 900; line-height: 1; }
  .stat:nth-child(1) { background: var(--sun); } .stat:nth-child(2) { background: #dfe8ff; }
  .stat:nth-child(3) { background: #d3f5e9; } .stat:nth-child(4) { background: #ffe1d9; }

  section { padding: 70px 0 10px; }
  h2 { font-size: 38px; font-weight: 900; letter-spacing: -1px; margin: 0 0 24px; }
  h2 small { font-size: 18px; background: var(--ink); color: #fff; padding: 3px 10px; border-radius: 999px; vertical-align: middle; }
  .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 20px; }
  .card { display: flex; flex-direction: column; text-decoration: none; background: #fff; padding: 22px;
          border: 3px solid var(--line); border-radius: 18px; box-shadow: 6px 6px 0 var(--c);
          transition: transform .12s, box-shadow .12s; }
  .card:hover { transform: translate(-3px, -3px); box-shadow: 9px 9px 0 var(--c); }
  .ico { width: 52px; height: 52px; border-radius: 14px; display: grid; place-items: center; font-size: 25px;
         color: #fff; background: var(--c); border: 3px solid var(--line); }
  .card h3 { margin: 16px 0 6px; font-size: 21px; font-weight: 900; }
  .card p { margin: 0 0 16px; color: var(--muted); font-size: 15.5px; flex: 1; }
  .meta { font-size: 13px; color: var(--muted); font-family: Consolas, monospace; display: flex; justify-content: space-between; }
  .meta b { font-family: "Segoe UI", system-ui, sans-serif; color: var(--c); }

  .features { display: grid; grid-template-columns: repeat(3, 1fr); gap: 20px; }
  .feature { padding: 26px; border: 3px solid var(--line); border-radius: 18px; background: #fff; }
  .feature i { font-size: 30px; color: var(--hot); }
  .feature h3 { margin: 12px 0 6px; font-size: 21px; font-weight: 900; }
  .feature p { margin: 0; color: var(--muted); }

  .steps { counter-reset: s; display: grid; gap: 16px; padding: 0; list-style: none; max-width: 860px; }
  .steps li { counter-increment: s; display: grid; grid-template-columns: 54px 1fr; gap: 16px; align-items: start;
              background: #fff; border: 3px solid var(--line); border-radius: 16px; padding: 18px; }
  .steps li::before { content: counter(s); width: 46px; height: 46px; display: grid; place-items: center; font-weight: 900;
                      font-size: 22px; background: var(--sun); border: 3px solid var(--line); border-radius: 12px; }
  .steps h4 { margin: 0 0 4px; font-size: 19px; }
  .steps p { margin: 0; color: var(--muted); }
  pre { background: var(--ink); color: #fffaf3; padding: 16px 18px; border-radius: 12px; overflow-x: auto;
        font: 14.5px/1.6 Consolas, monospace; margin: 10px 0 0; }
  code { font-family: Consolas, monospace; background: #f1e9dd; padding: 1px 6px; border-radius: 6px; }
  pre code { background: none; padding: 0; }

  .note { margin-top: 28px; padding: 20px 22px; border: 3px dashed var(--line); border-radius: 16px; background: #fff7d6; max-width: 860px; }

  footer { margin-top: 80px; padding: 34px 0; background: var(--ink); color: #fffaf3; }
  footer .wrap { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 12px; }
  footer a { color: var(--sun); font-weight: 700; }

  @media (max-width: 820px) {
    .stats { grid-template-columns: repeat(2, 1fr); }
    .stat:nth-child(2) { border-right: 0; }
    .stat:nth-child(-n+2) { border-bottom: 3px solid var(--line); }
    .features { grid-template-columns: 1fr; }
    nav .links a:not(.gh) { display: none; }
  }
</style>
</head>
<body>

<nav>
  <div class="wrap">
    <a class="logo" href="#"><span>A</span> App Suite</a>
    <div class="links">
      <a href="#apps">Apps</a>
      <a href="#launcher">Launcher</a>
      <a href="#start">Get started</a>
      <a class="gh" href="{{REPO}}"><i class="bi bi-github"></i> GitHub</a>
    </div>
  </div>
</nav>

<header class="hero">
  <div class="wrap">
    <span class="badge">Free &amp; open source · MIT</span>
    <h1><em>{{COUNT}}</em> AI apps.<br><u>Your</u> machine. <u>Your</u> key.</h1>
    <p class="lede">Plant care, wardrobe, travel, money, study, PDFs, video and more. Each one is a small,
      complete app you run on your own computer with Claude, Azure OpenAI or OpenAI.
      A desktop launcher starts them with one click, and lets you chat with Claude Code from your phone.</p>
    <div class="cta">
      <a class="btn primary" href="#start"><i class="bi bi-rocket-takeoff"></i> Get started</a>
      <a class="btn alt" href="{{REPO}}"><i class="bi bi-github"></i> View on GitHub</a>
    </div>
  </div>
</header>

<div class="stats">
  <div class="stat"><b>{{COUNT}}</b>standalone apps</div>
  <div class="stat"><b>3</b>AI providers</div>
  <div class="stat"><b>0</b>sign-ins needed locally</div>
  <div class="stat"><b>1</b>launcher for all</div>
</div>

<main class="wrap">

  <section>
    <h2>Why it's different</h2>
    <div class="features">
      <div class="feature"><i class="bi bi-key"></i><h3>Bring your own key</h3>
        <p>Pick Claude, Azure OpenAI or OpenAI in Settings. Your key stays on your machine, and you only pay for what you use.</p></div>
      <div class="feature"><i class="bi bi-shield-lock"></i><h3>No sign-in, safely</h3>
        <p>Local mode skips the sign-in screen, and only answers requests from your own computer. The app refuses to start if it's exposed publicly.</p></div>
      <div class="feature"><i class="bi bi-box"></i><h3>Every app stands alone</h3>
        <p>Each app has its own frontend, backend and database. Nothing is shared, so you can use one, or all {{COUNT}}.</p></div>
    </div>
  </section>

  <section id="apps">
    <h2>The apps <small>{{COUNT}}</small></h2>
    <p class="lede" style="margin-bottom:0">Click any app to see its code and its own README.</p>
  </section>
{{SECTIONS}}

  <section id="launcher">
    <h2>The launcher</h2>
    <div class="features">
      <div class="feature"><i class="bi bi-grid-3x3-gap"></i><h3>One window, every app</h3>
        <p>Start, stop, restart and open any of the {{COUNT}} apps. It frees each app's ports before starting, so a stuck app never blocks you.</p></div>
      <div class="feature"><i class="bi bi-terminal"></i><h3>Talks to Claude Code</h3>
        <p>If Claude Code is installed on your computer, chat with it right inside the launcher.</p></div>
      <div class="feature"><i class="bi bi-phone"></i><h3>From your phone</h3>
        <p>Turn on phone access, enter the pairing code shown in the launcher, and use it from any phone on the same Wi-Fi. It's off by default.</p></div>
    </div>
  </section>

  <section id="start">
    <h2>Get started</h2>
    <ol class="steps">
      <li><div><h4>Install the tools</h4>
        <p>You need <a href="https://nodejs.org">Node.js 20+</a> and the <a href="https://dotnet.microsoft.com/download/dotnet/8.0">.NET 8 SDK</a>.
          For the Claude chat, also <a href="https://docs.anthropic.com/en/docs/claude-code">Claude Code</a> (optional).</p></div></li>
      <li><div><h4>Download and open the launcher</h4>
<pre><code>git clone {{REPO}}.git
cd app-suite/launcher
npm install
npm start</code></pre></div></li>
      <li><div><h4>Add your AI key</h4>
        <p>Open <b>Settings</b> in the launcher and paste a Claude, Azure OpenAI or OpenAI key.</p></div></li>
      <li><div><h4>Start an app</h4>
        <p>Go to <b>Apps</b>, press <b>Start</b>, then <b>Open</b>. The first start of each app installs its packages, so give it a minute.</p></div></li>
    </ol>
    <div class="note"><b>Prefer one app on its own?</b> Every app folder has a README with its ports, settings and how to run it by hand,
      for example: <code>LocalMode__Enabled=true Anthropic__ApiKey=&lt;your key&gt; dotnet run</code>.</div>
  </section>

</main>

<footer>
  <div class="wrap">
    <span>App Suite · MIT licence · Free to use, change and share.</span>
    <a href="{{REPO}}"><i class="bi bi-github"></i> kartik-ramachandran/app-suite</a>
  </div>
</footer>

</body>
</html>
"""

if __name__ == "__main__":
    main()
