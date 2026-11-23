// Starts and stops each app's backend (dotnet run) and frontend (vite).
//
// • Local mode is on for everything started here: no sign-in, and the app's own
//   code refuses anything but localhost (see each app's Middleware/LocalMode.cs).
// • Each backend gets its own data folder under the launcher's userData, so the
//   launcher never touches a database you made by hand.
// • Logs are kept in memory (last 400 lines per process) for the log viewer.
// • Stop kills the whole process tree. On Windows `npm`/`dotnet` spawn children
//   that outlive their parent, which then keep holding the port — taskkill /T
//   is what actually frees it.
// • Start is always a fresh start: it stops the app's previous run and anything
//   else listening on that app's own two ports, then starts new ones. It never
//   touches any other port.
const { spawn, execFile } = require('child_process')
const fs = require('fs')
const net = require('net')
const path = require('path')

const MAX_LOG = 400
const isWin = process.platform === 'win32'

class ProcessManager {
  constructor({ dataRoot, onChange }) {
    this.dataRoot = dataRoot
    this.onChange = onChange
    this.procs = new Map() // key `${slug}:be|fe` -> { child, log: [], state }
  }

  key(slug, part) { return `${slug}:${part}` }

  log(slug, part) {
    return this.procs.get(this.key(slug, part))?.log || []
  }

  state(slug, part) {
    return this.procs.get(this.key(slug, part))?.state || 'stopped'
  }

  push(entry, line) {
    for (const l of String(line).split(/\r?\n/)) {
      if (!l.trim()) continue
      entry.log.push(l)
      if (entry.log.length > MAX_LOG) entry.log.shift()
    }
  }

  spawnPart(app, part, env) {
    const k = this.key(app.slug, part)
    if (this.procs.get(k)?.child) return
    let cmd, args, cwd
    if (part === 'be') {
      const dataDir = path.join(this.dataRoot, app.slug)
      fs.mkdirSync(dataDir, { recursive: true })
      cmd = 'dotnet'
      args = ['run', '--no-launch-profile', '--project', app.backend.csproj]
      cwd = app.backend.dir
      env = {
        ...env,
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: `http://localhost:${app.bePort}`,
        LocalMode__Enabled: 'true',
        ConnectionStrings__DefaultConnection: `Data Source=${path.join(dataDir, 'app.db')}`,
      }
    } else {
      // Windows can't spawn npm.cmd directly; run it through cmd.exe with a fixed
      // command line (nothing user-supplied goes into it).
      cmd = isWin ? 'cmd.exe' : 'npm'
      args = isWin ? ['/d', '/s', '/c', 'npm run dev'] : ['run', 'dev']
      cwd = app.frontend
    }
    // Keep what came before this run (e.g. "freed port …") at the top of the log.
    const prior = this.procs.get(k)?.log || []
    const entry = { child: null, log: prior.slice(-50), state: 'starting' }
    this.procs.set(k, entry)
    this.push(entry, `$ ${cmd} ${args.join(' ')}`)
    const child = spawn(cmd, args, { cwd, env: { ...process.env, ...env }, windowsHide: true })
    entry.child = child
    child.stdout.on('data', d => { this.push(entry, d); this.onChange() })
    child.stderr.on('data', d => { this.push(entry, d); this.onChange() })
    child.on('error', err => { this.push(entry, `failed to start: ${err.message}`); entry.state = 'failed'; entry.child = null; this.onChange() })
    child.on('exit', code => {
      this.push(entry, `exited (${code})`)
      entry.state = code === 0 || entry.stopping ? 'stopped' : 'failed'
      entry.child = null
      this.onChange()
    })
    this.onChange()
  }

  async start(app, aiEnv) {
    if (!app.backend || !app.frontend) throw new Error(`${app.title} is missing its backend or frontend folder`)
    // Always a fresh start: stop our own copy, then free the app's two ports from
    // anything else still holding them (an old dev server, a crashed run).
    await this.stop(app.slug)
    for (const port of [app.bePort, app.fePort]) {
      const freed = await freePort(port)
      if (freed.length) {
        const entry = this.procs.get(this.key(app.slug, port === app.bePort ? 'be' : 'fe')) || { log: [] }
        this.procs.set(this.key(app.slug, port === app.bePort ? 'be' : 'fe'), { ...entry, child: null, state: 'stopped' })
        this.push(this.procs.get(this.key(app.slug, port === app.bePort ? 'be' : 'fe')),
          `freed port ${port}: stopped ${freed.map(p => `${p.name} (pid ${p.pid})`).join(', ')}`)
      }
      if (await portInUse(port)) throw new Error(`Port ${port} is still in use and could not be freed.`)
    }
    if (!app.installed) await this.npmInstall(app)
    this.spawnPart(app, 'be', aiEnv)
    this.spawnPart(app, 'fe', {})
  }

  async restart(app, aiEnv) {
    return this.start(app, aiEnv)
  }

  npmInstall(app) {
    return new Promise((resolve, reject) => {
      const entry = { child: null, log: [], state: 'installing' }
      this.procs.set(this.key(app.slug, 'fe'), entry)
      this.push(entry, '$ npm install (first run)')
      this.onChange()
      const child = isWin
        ? spawn('cmd.exe', ['/d', '/s', '/c', 'npm install --no-audit --no-fund'], { cwd: app.frontend, windowsHide: true })
        : spawn('npm', ['install', '--no-audit', '--no-fund'], { cwd: app.frontend })
      child.stdout.on('data', d => this.push(entry, d))
      child.stderr.on('data', d => this.push(entry, d))
      child.on('exit', code => {
        entry.state = 'stopped'
        if (code === 0) { app.installed = true; resolve() } else reject(new Error(`npm install failed (${code})`))
      })
    })
  }

  async stop(slug) {
    await Promise.all(['fe', 'be'].map(part => this.kill(this.key(slug, part))))
  }

  kill(k) {
    const entry = this.procs.get(k)
    if (!entry?.child) return Promise.resolve()
    entry.stopping = true
    const pid = entry.child.pid
    return new Promise(resolve => {
      if (isWin) execFile('taskkill', ['/PID', String(pid), '/T', '/F'], () => resolve())
      else { try { process.kill(-pid, 'SIGTERM') } catch { entry.child.kill('SIGTERM') } resolve() }
    })
  }

  async stopAll() {
    await Promise.all([...this.procs.keys()].map(k => this.kill(k)))
  }

  /** Mark parts "running" once their port answers. Called on a timer. */
  async refresh(apps) {
    let changed = false
    await Promise.all(apps.flatMap(app => [['be', app.bePort], ['fe', app.fePort]].map(async ([part, port]) => {
      const entry = this.procs.get(this.key(app.slug, part))
      if (!entry?.child) return
      const up = await portInUse(port)
      const next = up ? 'running' : 'starting'
      if (entry.state !== next) { entry.state = next; changed = true }
    })))
    if (changed) this.onChange()
  }
}

/** True when something is listening on localhost:port — IPv4 or IPv6
 *  (Vite binds "localhost", which is ::1 on current Node/Windows). */
async function portInUse(port) {
  const one = host => new Promise(resolve => {
    const sock = net.connect({ host, port })
    sock.setTimeout(600)
    sock.once('connect', () => { sock.destroy(); resolve(true) })
    sock.once('timeout', () => { sock.destroy(); resolve(false) })
    sock.once('error', () => resolve(false))
  })
  const [v4, v6] = await Promise.all([one('127.0.0.1'), one('::1')])
  return v4 || v6
}

/** Process ids listening on a local TCP port, with their names. */
function listeners(port) {
  return new Promise(resolve => {
    if (isWin) {
      execFile('powershell', ['-NoProfile', '-NonInteractive', '-Command',
        `Get-NetTCPConnection -LocalPort ${Number(port)} -State Listen -ErrorAction SilentlyContinue | ` +
        `Select-Object -ExpandProperty OwningProcess -Unique | ForEach-Object { $p = Get-Process -Id $_ -ErrorAction SilentlyContinue; "$_|$($p.ProcessName)" }`],
        { windowsHide: true, timeout: 15000 }, (err, out) => {
          resolve(String(out || '').split(/\r?\n/).map(l => l.trim()).filter(Boolean)
            .map(l => { const [pid, name] = l.split('|'); return { pid: Number(pid), name: name || 'unknown' } })
            .filter(p => p.pid > 4 && p.pid !== process.pid))
        })
    } else {
      execFile('lsof', ['-nP', `-iTCP:${Number(port)}`, '-sTCP:LISTEN', '-Fpc'], (err, out) => {
        const res = []; let cur = null
        for (const l of String(out || '').split('\n')) {
          if (l.startsWith('p')) { cur = { pid: Number(l.slice(1)), name: 'unknown' }; res.push(cur) }
          else if (l.startsWith('c') && cur) cur.name = l.slice(1)
        }
        resolve(res.filter(p => p.pid !== process.pid))
      })
    }
  })
}

/** Stop whatever listens on this port (with its child processes). Returns what was stopped. */
async function freePort(port) {
  const found = await listeners(port)
  await Promise.all(found.map(p => new Promise(resolve => {
    if (isWin) execFile('taskkill', ['/PID', String(p.pid), '/T', '/F'], { windowsHide: true }, () => resolve())
    else { try { process.kill(p.pid, 'SIGTERM') } catch { /* gone */ } resolve() }
  })))
  for (let i = 0; i < 20 && found.length && await portInUse(port); i++) await new Promise(r => setTimeout(r, 250))
  return found
}

module.exports = { ProcessManager, portInUse, freePort }
