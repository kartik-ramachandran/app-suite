// App Launcher — Electron main process.
//
// One API (`call`) serves both the desktop window (over IPC) and phones (over
// remote.js). Anything that arrives from a phone is marked remote and gets the
// stricter rules: Claude stays read-only unless edits from the phone are
// allowed on the desktop, and only the desktop can turn phone access on/off.
const { app, BrowserWindow, ipcMain, shell, Menu } = require('electron')
const path = require('path')
const fs = require('fs')
const { loadApps, REPO } = require('./apps')
const { ProcessManager } = require('./processes')
const { ClaudeBridge, PERMISSION_MODES } = require('./claude')
const { RemoteServer } = require('./remote')

let win = null
let apps = []
const listeners = new Set()
const emit = msg => {
  for (const fn of listeners) { try { fn(msg) } catch { /* a closed phone stream */ } }
  if (win && !win.isDestroyed()) win.webContents.send('launcher:event', msg)
}

// ── Settings (AI keys for the apps, phone options) ─────────────────────────
// Stored in the user's own app-data folder, never in the repo. AI keys are
// handed to each app's backend as environment variables when it starts.
const settingsFile = () => path.join(app.getPath('userData'), 'settings.json')
const DEFAULT_SETTINGS = {
  ai: { preferred: 'Claude', anthropicKey: '', anthropicModel: '', azureEndpoint: '', azureKey: '', azureDeployment: '', openaiKey: '', openaiModel: '' },
  phone: { allowEdits: false },
}
function loadSettings() {
  try { return { ...DEFAULT_SETTINGS, ...JSON.parse(fs.readFileSync(settingsFile(), 'utf8')) } } catch { return structuredClone(DEFAULT_SETTINGS) }
}
function saveSettings(s) {
  fs.mkdirSync(path.dirname(settingsFile()), { recursive: true })
  fs.writeFileSync(settingsFile(), JSON.stringify(s, null, 2), { mode: 0o600 })
}
let settings = null

function aiEnv() {
  const a = settings.ai, env = {}
  const set = (k, v) => { if (v && String(v).trim()) env[k] = String(v).trim() }
  set('AI__PreferredProvider', a.preferred)
  set('Anthropic__ApiKey', a.anthropicKey); set('Anthropic__Model', a.anthropicModel)
  set('AzureOpenAI__Endpoint', a.azureEndpoint); set('AzureOpenAI__ApiKey', a.azureKey); set('AzureOpenAI__DeploymentName', a.azureDeployment)
  set('OpenAI__ApiKey', a.openaiKey); set('OpenAI__DefaultModel', a.openaiModel)
  return env
}

// Never send keys back to the UI — only whether each one is set.
function publicSettings() {
  const a = settings.ai
  return {
    ai: {
      preferred: a.preferred,
      anthropicModel: a.anthropicModel, azureEndpoint: a.azureEndpoint, azureDeployment: a.azureDeployment, openaiModel: a.openaiModel,
      hasAnthropicKey: !!a.anthropicKey, hasAzureKey: !!a.azureKey, hasOpenaiKey: !!a.openaiKey,
    },
    phone: settings.phone,
  }
}

// ── Services ────────────────────────────────────────────────────────────────
let procs, claude, remote

function appView(a) {
  return {
    slug: a.slug, title: a.title, blurb: a.blurb, icon: a.icon, fePort: a.fePort, bePort: a.bePort,
    palette: a.palette, exists: a.exists, ready: !!(a.backend && a.frontend), installed: a.installed,
    url: `http://localhost:${a.fePort}${a.homePath || '/'}`,
    backend: procs.state(a.slug, 'be'), frontend: procs.state(a.slug, 'fe'),
  }
}

const find = slug => {
  const a = apps.find(x => x.slug === slug)
  if (!a) throw new Error('Unknown app')
  return a
}

// Claude may only work inside one app folder or the repo root.
function claudeFolder(slug) {
  if (!slug || slug === '__repo__') return REPO
  return find(slug).dir
}

async function call(method, args, { remote: fromPhone = false } = {}) {
  switch (method) {
    case 'apps': return apps.map(appView)
    case 'start': {
      const a = find(args[0])
      await procs.start(a, aiEnv())
      return appView(a)
    }
    case 'restart': {
      const a = find(args[0])
      await procs.restart(a, aiEnv())
      return appView(a)
    }
    case 'stop': await procs.stop(args[0]); return appView(find(args[0]))
    case 'stopAll': await procs.stopAll(); return true
    case 'logs': return { be: procs.log(args[0], 'be'), fe: procs.log(args[0], 'fe') }
    case 'open': {
      if (fromPhone) throw new Error('Open apps on the computer.')
      const a = find(args[0])
      await shell.openExternal(`http://localhost:${a.fePort}${a.homePath || '/'}`)
      return true
    }
    case 'reloadApps': apps = loadApps(); return apps.map(appView)

    case 'claudeStatus': return { ...(await claude.status()), phoneAllowEdits: settings.phone.allowEdits }
    case 'claudeNew': return claude.newConversation()
    case 'claudeSend': {
      const [id, opts] = args
      let mode = PERMISSION_MODES.includes(opts?.permissionMode) ? opts.permissionMode : 'plan'
      if (fromPhone && !settings.phone.allowEdits) mode = 'plan'
      await claude.send(id, { message: opts?.message, cwd: claudeFolder(opts?.app), permissionMode: mode, model: opts?.model })
      return { mode }
    }
    case 'claudeStop': claude.stop(args[0]); return true
    case 'claudeReset': claude.reset(args[0]); return true

    case 'settings': return publicSettings()
    case 'saveSettings': {
      if (fromPhone) throw new Error('Change settings on the computer.')
      const next = args[0] || {}
      const a = settings.ai
      const keep = (cur, v) => (v === undefined ? cur : String(v))
      // Blank key fields mean "leave as is"; the "clear" flags remove a key.
      for (const [field, clear] of [['anthropicKey', 'clearAnthropicKey'], ['azureKey', 'clearAzureKey'], ['openaiKey', 'clearOpenaiKey']]) {
        if (next.ai?.[clear]) a[field] = ''
        else if (next.ai?.[field]) a[field] = String(next.ai[field]).trim()
      }
      for (const f of ['preferred', 'anthropicModel', 'azureEndpoint', 'azureDeployment', 'openaiModel']) a[f] = keep(a[f], next.ai?.[f])
      if (!['Claude', 'Azure', 'OpenAI'].includes(a.preferred)) a.preferred = 'Claude'
      if (next.phone && typeof next.phone.allowEdits === 'boolean') settings.phone.allowEdits = next.phone.allowEdits
      saveSettings(settings)
      return publicSettings()
    }

    case 'phoneStatus': return remote.status()
    case 'phoneStart': if (fromPhone) throw new Error('Only the computer can do that.'); return remote.start()
    case 'phoneStop': if (fromPhone) throw new Error('Only the computer can do that.'); return remote.stop()

    default: throw new Error(`Unknown action: ${method}`)
  }
}

// ── Window ─────────────────────────────────────────────────────────────────
function createWindow() {
  win = new BrowserWindow({
    width: 1320, height: 880, minWidth: 900, minHeight: 600,
    title: 'App Launcher', backgroundColor: '#ffffff',
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, nodeIntegration: false, sandbox: true },
  })
  // External links open in the browser; the window never navigates away.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:\/\//.test(url)) shell.openExternal(url)
    return { action: 'deny' }
  })
  win.webContents.on('will-navigate', e => e.preventDefault())
  win.loadFile(path.join(__dirname, 'renderer', 'index.html'))
}

app.whenReady().then(() => {
  settings = loadSettings()
  procs = new ProcessManager({ dataRoot: path.join(app.getPath('userData'), 'app-data'), onChange: () => emit({ type: 'apps' }) })
  claude = new ClaudeBridge({ onEvent: (id, ev) => emit({ type: 'claude', id, ev }) })
  remote = new RemoteServer({
    api: { call, subscribe: fn => { listeners.add(fn); return () => listeners.delete(fn) } },
  })
  apps = loadApps()

  ipcMain.handle('launcher:call', async (_e, method, args) => {
    try { return { ok: true, result: await call(method, args || [], { remote: false }) } }
    catch (err) { return { ok: false, error: err.message } }
  })

  setInterval(() => { procs.refresh(apps).catch(() => {}) }, 1500)
  Menu.setApplicationMenu(null)
  createWindow()
})

let quitting = false
app.on('before-quit', async e => {
  if (quitting) return
  e.preventDefault()
  quitting = true
  claude?.stopAll()
  await Promise.allSettled([procs?.stopAll(), remote?.stop()])
  app.quit()
})
app.on('window-all-closed', () => app.quit())
