// Bridge to the Claude Code CLI installed on this machine (`claude`).
//
// Each message runs `claude -p` with stream-json output, so replies stream in
// token by token and the session id comes back for follow-ups (`--resume`).
//
// Safety, enforced here rather than left to the prompt:
//   • The working directory is one app folder (or the repo root) chosen in the
//     UI — Claude can't be pointed at an arbitrary path from the phone.
//   • Permission mode defaults to "plan" (read-only: Claude explains and plans,
//     changes nothing). "acceptEdits" lets it edit files in that folder; it is
//     never given "bypassPermissions", so it cannot run shell commands unasked.
//   • One run at a time per conversation; runs are killed with their whole
//     process tree when stopped or when the launcher quits.
const { spawn, execFile } = require('child_process')
const { randomUUID } = require('crypto')
const fs = require('fs')

const isWin = process.platform === 'win32'
const PERMISSION_MODES = ['plan', 'acceptEdits']

/** Where `claude` lives, or null. Uses the same PATH a terminal would. */
function findClaude() {
  return new Promise(resolve => {
    execFile(isWin ? 'where' : 'which', ['claude'], { windowsHide: true }, (err, out) => {
      if (err) return resolve(null)
      const first = String(out).split(/\r?\n/).map(s => s.trim()).filter(Boolean)
      // Prefer the .exe / .cmd shim on Windows; a bare extensionless file can't be spawned directly.
      const pick = isWin ? (first.find(p => /\.(exe|cmd)$/i.test(p)) || first[0]) : first[0]
      resolve(pick && fs.existsSync(pick) ? pick : null)
    })
  })
}

function claudeVersion(bin) {
  return new Promise(resolve => {
    execFile(bin, ['--version'], { windowsHide: true, shell: isWin && /\.cmd$/i.test(bin), timeout: 15000 }, (err, out) =>
      resolve(err ? null : String(out).trim()))
  })
}

class ClaudeBridge {
  constructor({ onEvent }) {
    this.onEvent = onEvent    // (conversationId, event) -> void
    this.bin = null
    this.version = null
    this.runs = new Map()      // conversationId -> child
    this.sessions = new Map()  // conversationId -> claude session id
  }

  async status() {
    if (!this.bin) {
      this.bin = await findClaude()
      this.version = this.bin ? await claudeVersion(this.bin) : null
    }
    return { available: !!this.bin, version: this.version, path: this.bin }
  }

  newConversation() { return randomUUID() }

  isRunning(id) { return this.runs.has(id) }

  /**
   * Send one message. Emits events: start, text (streamed chunk), tool (Claude
   * used a tool), done ({ text, costUsd, sessionId }), error ({ message }).
   */
  async send(id, { message, cwd, permissionMode, model }) {
    const st = await this.status()
    if (!st.available) throw new Error('The Claude CLI was not found. Install Claude Code and make sure `claude` is on your PATH.')
    if (this.runs.has(id)) throw new Error('Claude is still answering the previous message.')
    if (!message || !String(message).trim()) throw new Error('Type a message first.')
    if (!fs.existsSync(cwd)) throw new Error('That folder does not exist.')
    const mode = PERMISSION_MODES.includes(permissionMode) ? permissionMode : 'plan'

    const args = ['-p', String(message), '--output-format', 'stream-json', '--verbose',
      '--include-partial-messages', '--permission-mode', mode]
    const resume = this.sessions.get(id)
    if (resume) args.push('--resume', resume)
    if (model && /^[a-z0-9.-]{2,40}$/i.test(model)) args.push('--model', model)

    const child = spawn(this.bin, args, {
      cwd, windowsHide: true, shell: isWin && /\.cmd$/i.test(this.bin),
      env: { ...process.env },
    })
    child.stdin.end()
    this.runs.set(id, child)
    this.onEvent(id, { type: 'start', mode, cwd })

    let buf = '', full = '', stderr = '', finished = false
    const finish = ev => {
      if (finished) return
      finished = true
      this.runs.delete(id)
      this.onEvent(id, ev)
    }

    child.stdout.on('data', chunk => {
      buf += chunk
      let nl
      while ((nl = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, nl).trim()
        buf = buf.slice(nl + 1)
        if (!line) continue
        let ev
        try { ev = JSON.parse(line) } catch { continue }
        if (ev.session_id) this.sessions.set(id, ev.session_id)
        if (ev.type === 'stream_event') {
          const e = ev.event || {}
          // Only top-level text; sub-agent output (parent_tool_use_id set) is not the reply.
          if (!ev.parent_tool_use_id && e.type === 'content_block_delta' && e.delta?.type === 'text_delta') {
            full += e.delta.text
            this.onEvent(id, { type: 'text', text: e.delta.text })
          }
        } else if (ev.type === 'assistant' && !ev.parent_tool_use_id) {
          for (const block of ev.message?.content || []) {
            if (block.type === 'tool_use') this.onEvent(id, { type: 'tool', name: block.name, input: summarise(block.input) })
          }
          // A message boundary inside one reply: keep paragraphs apart.
          if (full && !full.endsWith('\n')) { full += '\n\n'; this.onEvent(id, { type: 'text', text: '\n\n' }) }
        } else if (ev.type === 'result') {
          const text = typeof ev.result === 'string' && ev.result.trim() ? ev.result : full.trim()
          if (ev.is_error || ev.subtype !== 'success') {
            finish({ type: 'error', message: text || `Claude stopped: ${ev.subtype}` })
          } else {
            finish({ type: 'done', text, costUsd: ev.total_cost_usd ?? null, sessionId: ev.session_id })
          }
        }
      }
    })
    child.stderr.on('data', d => { stderr += d; if (stderr.length > 4000) stderr = stderr.slice(-4000) })
    child.on('error', err => finish({ type: 'error', message: `Could not run claude: ${err.message}` }))
    child.on('exit', code => {
      if (code === 0) finish({ type: 'done', text: full.trim(), costUsd: null, sessionId: this.sessions.get(id) })
      else finish({ type: 'error', message: (stderr.trim().split('\n').slice(-3).join('\n')) || `claude exited with code ${code}` })
    })
  }

  stop(id) {
    const child = this.runs.get(id)
    if (!child) return
    if (isWin) execFile('taskkill', ['/PID', String(child.pid), '/T', '/F'], () => {})
    else child.kill('SIGTERM')
    this.runs.delete(id)
    this.onEvent(id, { type: 'error', message: 'Stopped.' })
  }

  reset(id) {
    this.stop(id)
    this.sessions.delete(id)
  }

  stopAll() { for (const id of [...this.runs.keys()]) this.stop(id) }
}

function summarise(input) {
  if (!input || typeof input !== 'object') return ''
  const v = input.file_path || input.path || input.pattern || input.command || input.url || input.description || ''
  return String(v).slice(0, 140)
}

module.exports = { ClaudeBridge, PERMISSION_MODES }
