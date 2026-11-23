// Phone access: the same launcher UI, served over your local Wi-Fi.
//
// Off by default. When you turn it on, it listens on your LAN address and
// shows a pairing code + link. Guardrails, all enforced in this file:
//   • Every request needs the pairing token (in the link, then a cookie).
//     Wrong or missing token → 401. The token is 32 random bytes, regenerated
//     every time phone access is turned on, compared in constant time.
//   • Only private-network clients (10.x, 172.16–31.x, 192.168.x, 169.254.x,
//     fc00::/7, fe80::/10, loopback) are served. A public IP gets 403, so a
//     router port-forward can't expose it to the internet.
//   • Only the launcher's own actions are exposed (list/start/stop apps, view
//     logs, talk to Claude). The apps themselves stay localhost-only.
//   • Claude from the phone is read-only ("plan" mode) unless you allow edits
//     from the phone on the desktop.
const http = require('http')
const os = require('os')
const fs = require('fs')
const path = require('path')
const crypto = require('crypto')

const RENDERER = path.join(__dirname, 'renderer')
const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml' }

function isPrivate(addr) {
  if (!addr) return false
  let a = addr.replace(/^::ffff:/, '')
  if (a === '127.0.0.1' || a === '::1') return true
  const m = a.match(/^(\d+)\.(\d+)\.(\d+)\.(\d+)$/)
  if (m) {
    const [o1, o2] = [Number(m[1]), Number(m[2])]
    return o1 === 10 || (o1 === 172 && o2 >= 16 && o2 <= 31) || (o1 === 192 && o2 === 168) || (o1 === 169 && o2 === 254)
  }
  a = a.toLowerCase()
  return a.startsWith('fc') || a.startsWith('fd') || a.startsWith('fe8') || a.startsWith('fe9') || a.startsWith('fea') || a.startsWith('feb')
}

function lanAddresses() {
  const out = []
  for (const list of Object.values(os.networkInterfaces())) {
    for (const i of list || []) {
      if (i.family === 'IPv4' && !i.internal && isPrivate(i.address)) out.push(i.address)
    }
  }
  return out
}

function safeEqual(a, b) {
  const x = Buffer.from(String(a)), y = Buffer.from(String(b))
  return x.length === y.length && crypto.timingSafeEqual(x, y)
}

class RemoteServer {
  /** api: { call(method, args, { remote: true }) -> Promise<any>, subscribe(fn) -> unsubscribe } */
  constructor({ api, port = 5399 }) {
    this.api = api
    this.port = port
    this.server = null
    this.token = null
    this.clients = new Set()
  }

  status() {
    const addrs = lanAddresses()
    return {
      on: !!this.server,
      port: this.port,
      addresses: addrs,
      links: this.server ? addrs.map(a => `http://${a}:${this.port}/?pair=${this.token}`) : [],
      code: this.server ? this.token.slice(0, 8).toUpperCase() : null,
    }
  }

  start() {
    if (this.server) return Promise.resolve(this.status())
    this.token = crypto.randomBytes(32).toString('hex')
    this.server = http.createServer((req, res) => this.handle(req, res).catch(err => {
      if (!res.headersSent) res.writeHead(500, { 'Content-Type': 'application/json' })
      res.end(JSON.stringify({ error: err.message }))
    }))
    this.unsub = this.api.subscribe(msg => {
      const data = `data: ${JSON.stringify(msg)}\n\n`
      for (const c of this.clients) c.write(data)
    })
    return new Promise((resolve, reject) => {
      this.server.once('error', err => { this.server = null; reject(err) })
      // 0.0.0.0 so phones on the LAN can reach it; the private-IP check below keeps it off the internet.
      this.server.listen(this.port, '0.0.0.0', () => resolve(this.status()))
    })
  }

  stop() {
    this.unsub?.()
    for (const c of this.clients) c.end()
    this.clients.clear()
    return new Promise(resolve => {
      if (!this.server) return resolve(this.status())
      this.server.close(() => { this.server = null; this.token = null; resolve(this.status()) })
      this.server.closeAllConnections?.()
    })
  }

  authorised(req) {
    const url = new URL(req.url, 'http://x')
    const fromQuery = url.searchParams.get('pair')
    const cookie = (req.headers.cookie || '').split(';').map(s => s.trim()).find(s => s.startsWith('launcher_pair='))
    const fromCookie = cookie ? decodeURIComponent(cookie.slice('launcher_pair='.length)) : null
    const fromHeader = req.headers['x-launcher-pair']
    const presented = fromQuery || fromHeader || fromCookie
    return { ok: !!presented && safeEqual(presented, this.token), fromQuery: !!fromQuery && safeEqual(fromQuery, this.token) }
  }

  async handle(req, res) {
    if (!isPrivate(req.socket.remoteAddress)) {
      res.writeHead(403, { 'Content-Type': 'text/plain' })
      return res.end('Phone access only works from your own network.')
    }
    const auth = this.authorised(req)
    if (!auth.ok) {
      res.writeHead(401, { 'Content-Type': 'text/html; charset=utf-8' })
      return res.end('<!doctype html><meta name=viewport content="width=device-width"><body style="font-family:system-ui;padding:32px"><h2>Pair this phone</h2><p>Open the pairing link shown in the launcher on your computer (Phone access → Show link).</p></body>')
    }
    const headers = { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer' }
    if (auth.fromQuery) {
      // Swap the link token for an HttpOnly cookie and drop it from the address bar.
      headers['Set-Cookie'] = `launcher_pair=${encodeURIComponent(this.token)}; HttpOnly; SameSite=Strict; Path=/; Max-Age=2592000`
      res.writeHead(302, { ...headers, Location: '/' })
      return res.end()
    }
    const url = new URL(req.url, 'http://x')

    if (url.pathname === '/events') {
      res.writeHead(200, { ...headers, 'Content-Type': 'text/event-stream', Connection: 'keep-alive' })
      res.write(': connected\n\n')
      this.clients.add(res)
      req.on('close', () => this.clients.delete(res))
      return
    }
    if (url.pathname === '/rpc' && req.method === 'POST') {
      // Same-origin only: a page elsewhere on the network can't drive the launcher.
      const origin = req.headers.origin
      if (origin && origin !== `http://${req.headers.host}`) {
        res.writeHead(403, headers); return res.end()
      }
      const body = await readBody(req, 256 * 1024)
      let parsed
      try { parsed = JSON.parse(body) } catch { res.writeHead(400, headers); return res.end() }
      const result = await this.api.call(parsed.method, parsed.args || [], { remote: true })
      res.writeHead(200, { ...headers, 'Content-Type': 'application/json' })
      return res.end(JSON.stringify({ result }))
    }
    // Static renderer files.
    const rel = url.pathname === '/' ? 'index.html' : url.pathname.replace(/^\/+/, '')
    const file = path.normalize(path.join(RENDERER, rel))
    if (!file.startsWith(RENDERER + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
      res.writeHead(404, headers); return res.end()
    }
    res.writeHead(200, { ...headers, 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream' })
    fs.createReadStream(file).pipe(res)
  }
}

function readBody(req, limit) {
  return new Promise((resolve, reject) => {
    let size = 0, chunks = []
    req.on('data', c => {
      size += c.length
      if (size > limit) { reject(new Error('Request too large')); req.destroy(); return }
      chunks.push(c)
    })
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')))
    req.on('error', reject)
  })
}

module.exports = { RemoteServer, isPrivate, lanAddresses }
