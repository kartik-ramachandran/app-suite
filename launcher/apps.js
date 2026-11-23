// The list of apps:
//   • launcher/apps.json  — every generated app (title, ports, icon)
//   • the three hand-built apps below
// Ports are then re-read from each app's own config on disk, which wins.
// Each entry also records where its backend project and frontend live, and
// whether the folder actually exists on disk.
const fs = require('fs')
const path = require('path')

const REPO = path.resolve(__dirname, '..')

const HAND_BUILT = [
  { slug: 'travel-manager', title: 'Travel Manager', blurb: 'Trip planning, flight prices and live travel intel, with wardrobe-aware packing lists.', icon: 'airplane', fePort: 5273, bePort: 5320 },
  { slug: 'wardrobe-manager', title: 'Wardrobe Manager', blurb: 'Catalogue clothes from photos, get outfits for the weather, build packing lists.', icon: 'stars', fePort: 5274, bePort: 5321 },
  { slug: 'inkreel', title: 'InkReel', blurb: 'Napkin-style visual documents, plus Video Studio: documents into narrated videos.', icon: 'film', fePort: 5275, bePort: 5322 },
]

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')) } catch { return null }
}

function findBackendProject(slug) {
  const be = path.join(REPO, slug, 'backend')
  if (!fs.existsSync(be)) return null
  for (const dir of fs.readdirSync(be, { withFileTypes: true })) {
    if (!dir.isDirectory()) continue
    const full = path.join(be, dir.name)
    const proj = fs.readdirSync(full).find(f => f.endsWith('.csproj'))
    if (proj) return { dir: full, csproj: path.join(full, proj), name: dir.name }
  }
  return null
}

// Ports actually configured in the app's own files win over the list above.
function portsFromDisk(slug, backend) {
  const out = {}
  const vite = path.join(REPO, slug, 'frontend', 'vite.config.ts')
  if (fs.existsSync(vite)) {
    const m = fs.readFileSync(vite, 'utf8').match(/port:\s*(\d+)/)
    if (m) out.fePort = Number(m[1])
  }
  if (backend) {
    const ls = readJson(path.join(backend.dir, 'Properties', 'launchSettings.json'))
    const url = ls && Object.values(ls.profiles || {}).map(p => p.applicationUrl).find(Boolean)
    const m = url && String(url).match(/localhost:(\d+)/)
    if (m) out.bePort = Number(m[1])
  }
  return out
}

// "bi-flower1" -> "flower1"
const iconName = icon => String(icon || 'grid').replace(/^bi-/, '')

function loadApps() {
  const manifest = readJson(path.join(__dirname, 'apps.json'))
  const generated = (manifest?.apps || []).map(a => ({
    slug: a.slug, title: a.title, blurb: a.blurb, icon: iconName(a.icon),
    fePort: a.fePort, bePort: a.bePort, palette: a.palette || [],
    homePath: a.pages?.[0]?.path || '/',
  }))
  const hand = HAND_BUILT.map(a => ({ ...a, palette: [], homePath: '/' }))
  const seen = new Set()
  return [...hand, ...generated]
    .filter(a => !seen.has(a.slug) && seen.add(a.slug))
    .map(a => {
      const backend = findBackendProject(a.slug)
      const frontend = path.join(REPO, a.slug, 'frontend')
      return {
        ...a,
        ...portsFromDisk(a.slug, backend),
        dir: path.join(REPO, a.slug),
        backend,
        frontend: fs.existsSync(path.join(frontend, 'package.json')) ? frontend : null,
        exists: fs.existsSync(path.join(REPO, a.slug)),
        installed: fs.existsSync(path.join(frontend, 'node_modules', '.bin')),
      }
    })
    .sort((x, y) => x.fePort - y.fePort)
}

module.exports = { loadApps, REPO }
