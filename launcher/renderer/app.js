// App Launcher UI — the same file runs in the desktop window (window.launcher
// from preload.js) and on a phone browser (fetch + EventSource to remote.js).
(() => {
  'use strict'

  // ── Transport ────────────────────────────────────────────────────────────
  const desktop = !!(window.launcher && window.launcher.desktop)
  const call = desktop
    ? (method, ...args) => window.launcher.call(method, ...args)
    : async (method, ...args) => {
        const res = await fetch('/rpc', {
          method: 'POST', credentials: 'same-origin',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ method, args }),
        })
        if (res.status === 401) { location.reload(); throw new Error('Not paired') }
        const data = await res.json().catch(() => ({}))
        if (!res.ok || data.error) throw new Error(data.error || `Request failed (${res.status})`)
        return data.result
      }
  const onEvent = desktop
    ? fn => window.launcher.onEvent(fn)
    : fn => {
        let es
        const open = () => {
          es = new EventSource('/events', { withCredentials: true })
          es.onmessage = e => { try { fn(JSON.parse(e.data)) } catch { /* ignore */ } }
          es.onerror = () => { es.close(); setTimeout(open, 2000) }
        }
        open()
      }

  // ── Helpers ──────────────────────────────────────────────────────────────
  const $ = sel => document.querySelector(sel)
  const el = (tag, props = {}, ...kids) => {
    const n = document.createElement(tag)
    for (const [k, v] of Object.entries(props)) {
      if (k === 'class') n.className = v
      else if (k === 'style') Object.assign(n.style, v)
      else if (k.startsWith('on')) n.addEventListener(k.slice(2), v)
      else if (v !== false && v != null) n.setAttribute(k, v === true ? '' : v)
    }
    for (const kid of kids.flat()) if (kid != null && kid !== false) n.append(kid.nodeType ? kid : String(kid))
    return n
  }
  let toastTimer
  const toast = (msg, err = false) => {
    const t = $('#toast')
    t.textContent = msg
    t.className = `toast show${err ? ' err' : ''}`
    clearTimeout(toastTimer)
    toastTimer = setTimeout(() => { t.className = 'toast' }, 3800)
  }
  const guard = fn => async (...a) => { try { return await fn(...a) } catch (e) { toast(e.message, true) } }

  // ── Tabs ─────────────────────────────────────────────────────────────────
  document.querySelectorAll('.top .tab').forEach(btn => btn.addEventListener('click', () => {
    document.querySelectorAll('.top .tab').forEach(b => b.classList.toggle('active', b === btn))
    document.querySelectorAll('.panel').forEach(p => p.classList.toggle('active', p.id === `tab-${btn.dataset.tab}`))
    if (btn.dataset.tab === 'settings') loadSettings()
    if (btn.dataset.tab === 'claude') loadClaudeStatus()
  }))
  if (!desktop) $('#subtitle').textContent = 'Connected from your phone.'

  // ── Apps ─────────────────────────────────────────────────────────────────
  let apps = []
  const busy = new Set()

  const initials = title => title.split(/\s+/).map(w => w[0]).join('').slice(0, 2).toUpperCase()
  const label = s => ({ running: 'Running', starting: 'Starting…', installing: 'Installing…', failed: 'Failed', stopped: 'Stopped' }[s] || s)
  const isLive = a => a.backend !== 'stopped' || a.frontend !== 'stopped'

  function renderApps() {
    const q = $('#search').value.trim().toLowerCase()
    const shown = apps.filter(a => !q || `${a.title} ${a.blurb} ${a.slug}`.toLowerCase().includes(q))
    const live = apps.filter(a => a.backend === 'running' && a.frontend === 'running').length
    $('#counts').replaceChildren(
      el('span', { class: 'pill' }, `${apps.length} apps`),
      el('span', { class: `pill${live ? ' live' : ''}` }, `${live} running`),
    )
    $('#grid').replaceChildren(...shown.map(a => {
      const running = a.backend === 'running' && a.frontend === 'running'
      const c1 = a.palette?.[0] || '#ff5a36', c2 = a.palette?.[2] || '#6d3cff'
      const actions = []
      if (!a.exists || !a.ready) {
        actions.push(el('span', { class: 'missing' }, a.exists ? 'Backend or frontend folder missing' : 'Folder not found'))
      } else if (isLive(a)) {
        if (running) actions.push(desktop
          ? el('button', { class: 'btn open', onclick: guard(() => call('open', a.slug)) }, 'Open')
          : el('span', { class: 'muted small' }, 'Open it on the computer'))
        actions.push(el('button', { class: 'btn ghost', disabled: busy.has(a.slug), onclick: guard(async () => {
          busy.add(a.slug); renderApps()
          try { await call('restart', a.slug); toast(`Restarting ${a.title}…`) } finally { busy.delete(a.slug); refresh() }
        }) }, 'Restart'))
        actions.push(el('button', { class: 'btn stop', disabled: busy.has(a.slug), onclick: guard(async () => {
          busy.add(a.slug); renderApps()
          try { await call('stop', a.slug) } finally { busy.delete(a.slug); refresh() }
        }) }, 'Stop'))
      } else {
        actions.push(el('button', { class: 'btn go', disabled: busy.has(a.slug), onclick: guard(async () => {
          busy.add(a.slug); renderApps()
          try { await call('start', a.slug); toast(`Starting ${a.title}…${a.installed ? '' : ' (first run installs packages — give it a minute)'}`) }
          finally { busy.delete(a.slug); refresh() }
        }) }, 'Start'))
      }
      actions.push(el('button', { class: 'btn ghost', onclick: () => showLogs(a) }, 'Logs'))
      actions.push(el('button', { class: 'btn ghost', onclick: () => askClaudeAbout(a) }, 'Ask Claude'))
      return el('article', { class: `app${running ? ' running' : ''}`, style: { '--c1': c1, '--c2': c2 } },
        el('div', { class: 'app-head' },
          el('div', { class: 'app-icon' }, initials(a.title)),
          el('div', {}, el('h3', {}, a.title), el('div', { class: 'ports' }, `localhost:${a.fePort} · api ${a.bePort}`)),
        ),
        el('p', { class: 'blurb' }, a.blurb || ''),
        el('div', { class: 'state' },
          el('span', { class: `dot ${a.backend}` }, `Backend: ${label(a.backend)}`),
          el('span', { class: `dot ${a.frontend}` }, `Frontend: ${label(a.frontend)}`),
        ),
        el('div', { class: 'actions' }, actions),
      )
    }))
    fillClaudeFolders()
  }

  let refreshing = false
  async function refresh() {
    if (refreshing) return
    refreshing = true
    try { apps = await call('apps'); renderApps() } catch (e) { toast(e.message, true) } finally { refreshing = false }
  }
  $('#search').addEventListener('input', renderApps)
  $('#stopAll').addEventListener('click', guard(async () => { await call('stopAll'); toast('Stopping everything…'); refresh() }))

  // Logs
  let logApp = null, logPart = 'be', logTimer = null
  async function paintLogs() {
    if (!logApp) return
    const logs = await call('logs', logApp.slug).catch(() => ({ be: [], fe: [] }))
    const body = $('#logsBody')
    const atBottom = body.scrollTop + body.clientHeight >= body.scrollHeight - 30
    body.textContent = (logs[logPart] || []).join('\n') || 'Nothing yet — start the app to see its output here.'
    if (atBottom) body.scrollTop = body.scrollHeight
  }
  function showLogs(a) {
    logApp = a
    $('#logsTitle').textContent = `${a.title} — logs`
    $('#logs').showModal()
    paintLogs()
    clearInterval(logTimer)
    logTimer = setInterval(paintLogs, 1500)
  }
  $('#logs').addEventListener('close', () => { clearInterval(logTimer); logApp = null })
  document.querySelectorAll('.logs-tabs .tab').forEach(b => b.addEventListener('click', () => {
    logPart = b.dataset.log
    document.querySelectorAll('.logs-tabs .tab').forEach(x => x.classList.toggle('active', x === b))
    paintLogs()
  }))

  // ── Claude ───────────────────────────────────────────────────────────────
  let convo = null, current = null, claudeReady = false, phoneAllowEdits = false

  async function loadClaudeStatus() {
    const st = await call('claudeStatus').catch(e => ({ available: false, error: e.message }))
    claudeReady = st.available
    phoneAllowEdits = !!st.phoneAllowEdits
    const box = $('#claudeStatus')
    box.className = `claude-status ${st.available ? 'ok' : 'bad'}`
    box.textContent = st.available
      ? `Claude CLI ready — ${st.version || 'installed'}`
      : 'Claude CLI not found. Install Claude Code on this computer and make sure `claude` works in a terminal.'
    const editOpt = $('#claudeMode').querySelector('option[value="acceptEdits"]')
    editOpt.disabled = !desktop && !phoneAllowEdits
    if (editOpt.disabled) { $('#claudeMode').value = 'plan'; editOpt.textContent = 'Can edit files (allow on the computer first)' }
    else editOpt.textContent = 'Can edit files'
    $('#send').disabled = !claudeReady
    if (!convo && claudeReady) convo = await call('claudeNew')
  }

  function fillClaudeFolders() {
    const sel = $('#claudeApp')
    const keep = sel.value
    const opts = [el('option', { value: '__repo__' }, 'Whole repo')]
      .concat(apps.filter(a => a.exists).map(a => el('option', { value: a.slug }, a.title)))
    if (sel.options.length !== opts.length) {
      sel.replaceChildren(...opts)
      sel.value = keep && [...sel.options].some(o => o.value === keep) ? keep : '__repo__'
    }
  }

  function askClaudeAbout(a) {
    document.querySelector('.top .tab[data-tab="claude"]').click()
    $('#claudeApp').value = a.slug
    newChat()
    $('#prompt').focus()
  }

  // Minimal, safe rendering: fenced code blocks and inline `code`; everything else is text.
  function renderText(target, text) {
    target.replaceChildren()
    const parts = text.split(/```(?:[\w-]*)\n?([\s\S]*?)(?:```|$)/)
    parts.forEach((part, i) => {
      if (i % 2 === 1) { target.append(el('pre', {}, part)); return }
      part.split(/(`[^`\n]+`)/).forEach(seg => {
        if (/^`[^`]+`$/.test(seg)) target.append(el('code', {}, seg.slice(1, -1)))
        else if (seg) target.append(document.createTextNode(seg))
      })
    })
  }

  function addMsg(kind, text = '') {
    const body = el('div', { class: 'body' })
    const node = el('div', { class: `msg ${kind}` }, body)
    if (text) renderText(body, text)
    $('#chat').append(node)
    $('#chat').scrollTop = $('#chat').scrollHeight
    return { node, body, text, tools: null }
  }

  function setRunning(on) {
    $('#send').classList.toggle('hidden', on)
    $('#stopClaude').classList.toggle('hidden', !on)
    $('#prompt').disabled = on
  }

  async function newChat() {
    if (convo) await call('claudeReset', convo).catch(() => {})
    convo = await call('claudeNew').catch(() => null)
    current = null
    $('#chat').replaceChildren()
    setRunning(false)
  }
  $('#claudeNew').addEventListener('click', guard(newChat))
  $('#claudeApp').addEventListener('change', guard(newChat))

  $('#composer').addEventListener('submit', guard(async e => {
    e.preventDefault()
    const message = $('#prompt').value.trim()
    if (!message || !claudeReady) return
    if (!convo) convo = await call('claudeNew')
    addMsg('user', message)
    $('#prompt').value = ''
    current = addMsg('claude')
    current.body.classList.add('typing')
    setRunning(true)
    try {
      const r = await call('claudeSend', convo, { message, app: $('#claudeApp').value, permissionMode: $('#claudeMode').value })
      if (r?.mode === 'plan' && $('#claudeMode').value !== 'plan') toast('From the phone Claude is read-only — allow edits on the computer to change that.')
    } catch (err) {
      current.node.className = 'msg err'
      current.body.classList.remove('typing')
      renderText(current.body, err.message)
      current = null
      setRunning(false)
    }
  }))
  $('#prompt').addEventListener('keydown', e => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); $('#composer').requestSubmit() }
  })
  $('#stopClaude').addEventListener('click', guard(() => call('claudeStop', convo)))

  function onClaude(id, ev) {
    if (id !== convo) return
    if (!current && (ev.type === 'text' || ev.type === 'tool')) { current = addMsg('claude'); setRunning(true) }
    if (ev.type === 'text') {
      current.text += ev.text
      renderText(current.body, current.text)
    } else if (ev.type === 'tool') {
      if (!current.tools) { current.tools = el('div', { class: 'tools' }); current.node.append(current.tools) }
      current.tools.append(el('div', { class: 'tool' }, `↳ ${ev.name}${ev.input ? `  ${ev.input}` : ''}`))
    } else if (ev.type === 'done' || ev.type === 'error') {
      if (!current) current = addMsg('claude')
      current.body.classList.remove('typing')
      if (ev.type === 'error') {
        current.node.className = 'msg err'
        renderText(current.body, (current.text ? current.text + '\n\n' : '') + ev.message)
      } else {
        if (!current.text && ev.text) renderText(current.body, ev.text)
        if (ev.costUsd != null) current.node.append(el('div', { class: 'meta' }, `Done · $${Number(ev.costUsd).toFixed(4)}`))
      }
      current = null
      setRunning(false)
    }
    $('#chat').scrollTop = $('#chat').scrollHeight
  }

  // ── Settings ─────────────────────────────────────────────────────────────
  const keyFields = [['anthropicKey', 'hasAnthropicKey'], ['azureKey', 'hasAzureKey'], ['openaiKey', 'hasOpenaiKey']]

  async function loadSettings() {
    const s = await call('settings').catch(e => { toast(e.message, true); return null })
    if (!s) return
    $('#aiPreferred').value = s.ai.preferred
    for (const f of ['anthropicModel', 'azureEndpoint', 'azureDeployment', 'openaiModel']) $(`#${f}`).value = s.ai[f] || ''
    for (const [f, has] of keyFields) {
      const input = $(`#${f}`)
      input.value = ''
      input.placeholder = s.ai[has] ? 'Saved — type to replace' : 'Not set'
    }
    $('#phoneEdits').checked = !!s.phone.allowEdits
    const lock = !desktop
    document.querySelectorAll('#aiCard input, #aiCard select, #aiCard button, #phoneEdits').forEach(n => { n.disabled = lock })
    if (lock) $('#aiSaved').textContent = 'Settings can only be changed on the computer.'
    renderPhone()
  }

  $('#saveAi').addEventListener('click', guard(async () => {
    const ai = { preferred: $('#aiPreferred').value }
    for (const f of ['anthropicModel', 'azureEndpoint', 'azureDeployment', 'openaiModel']) ai[f] = $(`#${f}`).value.trim()
    for (const [f] of keyFields) { const v = $(`#${f}`).value.trim(); if (v) ai[f] = v }
    await call('saveSettings', { ai })
    $('#aiSaved').textContent = 'Saved.'
    setTimeout(() => { $('#aiSaved').textContent = '' }, 2500)
    loadSettings()
  }))
  $('#phoneEdits').addEventListener('change', guard(async e => {
    await call('saveSettings', { phone: { allowEdits: e.target.checked } })
    toast(e.target.checked ? 'Claude can edit files when asked from the phone.' : 'From the phone, Claude is read-only.')
  }))

  async function renderPhone() {
    const st = await call('phoneStatus').catch(() => null)
    const body = $('#phoneBody')
    if (!st) { body.replaceChildren(); return }
    if (!desktop) {
      body.replaceChildren(el('p', { class: 'pill live' }, 'This phone is paired.'))
      return
    }
    if (!st.on) {
      body.replaceChildren(el('div', { class: 'row' },
        el('button', { class: 'btn hot', onclick: guard(async () => { await call('phoneStart'); renderPhone() }) }, 'Turn on phone access'),
        st.addresses.length ? null : el('span', { class: 'missing' }, 'No Wi-Fi network found on this computer.'),
      ))
      return
    }
    body.replaceChildren(
      el('div', { class: 'pair' },
        el('div', { class: 'muted small' }, 'Pairing code'),
        el('div', { class: 'code' }, st.code),
        el('div', { class: 'muted small' }, 'Open this link on your phone (same Wi-Fi):'),
        ...st.links.map(l => el('div', { class: 'link' }, l)),
      ),
      el('div', { class: 'row' },
        el('button', { class: 'btn stop', onclick: guard(async () => { await call('phoneStop'); renderPhone() }) }, 'Turn off phone access'),
        el('span', { class: 'muted small' }, 'Turning it off and on again makes a new link, so old ones stop working.'),
      ),
    )
  }

  // ── Wiring ───────────────────────────────────────────────────────────────
  let pending = null
  onEvent(msg => {
    if (msg.type === 'apps') { clearTimeout(pending); pending = setTimeout(refresh, 150) }
    if (msg.type === 'claude') onClaude(msg.id, msg.ev)
  })
  refresh()
  loadClaudeStatus()
  setInterval(refresh, 5000)
})()
