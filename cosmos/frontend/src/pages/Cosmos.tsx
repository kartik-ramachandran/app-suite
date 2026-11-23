import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useRef, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import * as THREE from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import api from '../services/api'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { setCosmosHandoff } from '../utils/cosmosHandoff'
import ModuleVoiceChat from '../components/ModuleVoiceChat'
import './Cosmos.css'

// ── Types ────────────────────────────────────────────────────────────────────

interface CosmosNode {
  id: string
  title: string
  content?: string | null
  kind: 'note' | 'concept' | 'tag'
  source: string
  sourceModule?: string | null
  createdAt: string
  updatedAt: string
}

interface CosmosLink {
  id: string
  sourceId: string
  targetId: string
  kind: string
  label?: string | null
  weight: number
}

interface CosmosAttachment {
  id: number
  fileName: string
  contentType: string
  fileSize: number
  createdAt: string
}

interface NodeDetail extends CosmosNode {
  sourceRef?: string | null
  links: CosmosLink[]
  attachments: CosmosAttachment[]
}

interface Graph {
  nodes: CosmosNode[]
  links: CosmosLink[]
}

// ── Visual constants ─────────────────────────────────────────────────────────
// Saturated marks on a bright, misty canvas. Suns (concepts) are also bigger
// than planets (notes), so color is never the only identity channel.

const KIND_COLORS: Record<string, string> = {
  concept: '#d9930b', // amber suns
  tag: '#5965d8',     // indigo beacons
  note: '#ed6547',    // coral planets
}

const LINK_COLORS: Record<string, string> = {
  wiki: '#e78a62',
  tag: '#737de2',
  concept: '#d9930b',
  manual: '#7668b4',
}

const SPACE_BG = '#f7f7ff'
const MAX_COSMOS_FILES = 10
const MAX_COSMOS_FILE_BYTES = 10 * 1024 * 1024
const MAX_INDEXED_FILE_CHARS = 30_000
const MAX_INDEXED_CONTENT_CHARS = 80_000
const TEXT_FILE_EXTENSIONS = new Set([
  'txt', 'md', 'markdown', 'csv', 'json', 'yaml', 'yml', 'xml', 'html', 'htm',
  'css', 'js', 'jsx', 'ts', 'tsx', 'py', 'rb', 'go', 'java', 'cs', 'cpp', 'c',
  'h', 'rs', 'sql', 'sh', 'bat', 'ps1', 'toml', 'ini', 'log', 'rtf',
])

function fileExtension(fileName: string): string {
  return fileName.split('.').pop()?.toLowerCase() ?? ''
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function fileIcon(fileName: string): string {
  const ext = fileExtension(fileName)
  if (ext === 'pdf') return 'bi-file-earmark-pdf'
  if (['doc', 'docx', 'rtf'].includes(ext)) return 'bi-file-earmark-word'
  if (['xls', 'xlsx', 'csv'].includes(ext)) return 'bi-file-earmark-spreadsheet'
  if (['ppt', 'pptx'].includes(ext)) return 'bi-file-earmark-ppt'
  if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'heic'].includes(ext)) return 'bi-file-earmark-image'
  if (['zip', 'rar', '7z', 'tar', 'gz'].includes(ext)) return 'bi-file-earmark-zip'
  if (['json', 'xml', 'yaml', 'yml', 'js', 'ts', 'tsx', 'jsx', 'py', 'cs'].includes(ext)) return 'bi-file-earmark-code'
  return 'bi-file-earmark'
}

async function extractIndexableText(file: File): Promise<string> {
  const ext = fileExtension(file.name)
  if (file.type.startsWith('text/') || TEXT_FILE_EXTENSIONS.has(ext)) {
    return (await file.text()).slice(0, MAX_INDEXED_FILE_CHARS)
  }
  if (ext === 'pdf' || ext === 'docx') {
    const form = new FormData()
    form.append('file', file)
    const response = await api.post<{ text: string }>('/files/extract-text', form, {
      headers: { 'Content-Type': 'multipart/form-data' },
    })
    return (response.data.text ?? '').slice(0, MAX_INDEXED_FILE_CHARS)
  }
  return ''
}

// Deterministic pseudo-random from a node id, so a node lands in the same
// region of space every visit.
function hash01(str: string, salt: number): number {
  let h = 2166136261 ^ salt
  for (let i = 0; i < str.length; i++) {
    h ^= str.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return ((h >>> 0) % 100000) / 100000
}

interface BodyState {
  pos: THREE.Vector3
  vel: THREE.Vector3
}

// ── Component ────────────────────────────────────────────────────────────────

export default function Cosmos() {
  const { setBreadcrumbs } = useLayoutControls()

  const [graph, setGraph] = useState<Graph>({ nodes: [], links: [] })
  const [loading, setLoading] = useState(true)
  const [dumpText, setDumpText] = useState('')
  const [saving, setSaving] = useState(false)
  const [toast, setToast] = useState<string | null>(null)
  const [selected, setSelected] = useState<CosmosNode | null>(null)
  const [detail, setDetail] = useState<NodeDetail | null>(null)
  const [linkMode, setLinkMode] = useState(false)
  const [editMode, setEditMode] = useState(false)
  const [editTitle, setEditTitle] = useState('')
  const [editContent, setEditContent] = useState('')
  const [search, setSearch] = useState('')
  const [dumpFiles, setDumpFiles] = useState<File[]>([])
  const [draggingFiles, setDraggingFiles] = useState(false)

  const mountRef = useRef<HTMLDivElement | null>(null)
  const labelLayerRef = useRef<HTMLDivElement | null>(null)
  const fileInputRef = useRef<HTMLInputElement | null>(null)

  // Mutable world shared between React and the render loop
  const worldRef = useRef<{
    renderer?: THREE.WebGLRenderer
    scene?: THREE.Scene
    camera?: THREE.PerspectiveCamera
    controls?: OrbitControls
    raycaster: THREE.Raycaster
    sprites: Map<string, THREE.Sprite>
    lineGeo?: THREE.BufferGeometry
    lineLinks: CosmosLink[]
    bodies: Map<string, BodyState>
    degrees: Map<string, number>
    alpha: number
    hoverId: string | null
    selectedId: string | null
    focusIds: Set<string> | null
    focusTarget: THREE.Vector3 | null
    labelPool: HTMLDivElement[]
    graph: Graph
    raf: number
    textures: Map<string, THREE.Texture>
    disposed: boolean
  }>({
    raycaster: new THREE.Raycaster(),
    sprites: new Map(),
    lineLinks: [],
    bodies: new Map(),
    degrees: new Map(),
    alpha: 0,
    hoverId: null,
    selectedId: null,
    focusIds: null,
    focusTarget: null,
    labelPool: [],
    graph: { nodes: [], links: [] },
    raf: 0,
    textures: new Map(),
    disposed: false,
  })

  const linkModeRef = useRef(false)
  const selectedRef = useRef<CosmosNode | null>(null)
  useEffect(() => { linkModeRef.current = linkMode }, [linkMode])
  useEffect(() => { selectedRef.current = selected }, [selected])

  useEffect(() => {
    setBreadcrumbs([{ label: 'Cosmos' }])
  }, [setBreadcrumbs])

  const showToast = useCallback((message: string) => {
    setToast(message)
    window.setTimeout(() => setToast(null), 3200)
  }, [])

  // ── Data ───────────────────────────────────────────────────────────────────

  const loadGraph = useCallback(async () => {
    try {
      const response = await api.get('/knowledge/graph')
      const data = response.data as { nodes: CosmosNode[]; links: CosmosLink[] }
      const ids = new Set(data.nodes.map(n => n.id))
      setGraph({
        nodes: data.nodes,
        links: data.links.filter(l => ids.has(l.sourceId) && ids.has(l.targetId)),
      })
    } catch {
      showToast('Could not load your cosmos')
    } finally {
      setLoading(false)
    }
  }, [showToast])

  useEffect(() => { loadGraph() }, [loadGraph])

  const openNode = useCallback(async (node: CosmosNode) => {
    setSelected(node)
    setEditMode(false)
    setDetail(null)
    try {
      const response = await api.get(`/knowledge/nodes/${node.id}`)
      setDetail(response.data as NodeDetail)
    } catch { /* panel shows the summary we already have */ }
  }, [])

  const queueFiles = (incoming: FileList) => {
    const candidates = Array.from(incoming)
    const oversized = candidates.filter(file => file.size > MAX_COSMOS_FILE_BYTES)
    const allowed = candidates.filter(file => file.size <= MAX_COSMOS_FILE_BYTES)

    setDumpFiles(current => {
      const next = [...current]
      for (const file of allowed) {
        const alreadyAdded = next.some(existing =>
          existing.name === file.name &&
          existing.size === file.size &&
          existing.lastModified === file.lastModified)
        if (!alreadyAdded && next.length < MAX_COSMOS_FILES) next.push(file)
      }
      return next
    })

    if (oversized.length > 0) {
      showToast(`${oversized.length === 1 ? `“${oversized[0].name}” exceeds` : `${oversized.length} files exceed`} the 10 MB limit`)
    } else if (dumpFiles.length + allowed.length > MAX_COSMOS_FILES) {
      showToast(`You can attach up to ${MAX_COSMOS_FILES} files at once`)
    }
  }

  const removeQueuedFile = (file: File) => {
    setDumpFiles(current => current.filter(candidate =>
      candidate.name !== file.name ||
      candidate.size !== file.size ||
      candidate.lastModified !== file.lastModified))
  }

  const handleDump = async () => {
    const note = dumpText.trim()
    const files = [...dumpFiles]
    if ((!note && files.length === 0) || saving) return
    setSaving(true)
    try {
      const fileSections: string[] = []
      for (const file of files) {
        let extracted = ''
        try {
          extracted = await extractIndexableText(file)
        } catch {
          // The original file is still stored. Some binary or scanned formats
          // simply do not have indexable text.
        }
        const metadata = `Attached file: ${file.name} (${file.type || 'unknown type'}, ${formatFileSize(file.size)})`
        fileSections.push(extracted
          ? `## ${file.name}\n${metadata}\n\n${extracted}`
          : `## ${file.name}\n${metadata}`)
      }

      const content = [note, ...fileSections]
        .filter(Boolean)
        .join('\n\n')
        .slice(0, MAX_INDEXED_CONTENT_CHARS)
      const fileTitle = files.length === 1
        ? files[0].name
        : files.length > 1
          ? `${files[0].name} + ${files.length - 1} more`
          : undefined
      const response = await api.post<{ id: string }>('/knowledge/nodes', {
        title: note ? undefined : fileTitle?.slice(0, 280),
        content,
        source: 'dumped',
        sourceModule: 'cosmos',
      })

      const nodeId = response.data.id
      try {
        for (const file of files) {
          const form = new FormData()
          form.append('entityType', 'knowledge')
          form.append('entityId', nodeId)
          form.append('files', file)
          await api.post('/files/upload', form, {
            headers: { 'Content-Type': 'multipart/form-data' },
          })
        }
      } catch (uploadError) {
        // Roll back the note and any attachments already stored so a retry does
        // not leave a partial or duplicate star.
        await api.delete(`/knowledge/nodes/${nodeId}`).catch(() => {})
        throw uploadError
      }

      setDumpText('')
      setDumpFiles([])
      if (fileInputRef.current) fileInputRef.current.value = ''
      await loadGraph()
      showToast(files.length > 0
        ? `${files.length} ${files.length === 1 ? 'file' : 'files'} woven into your cosmos ✦`
        : 'Woven into your cosmos ✦')
    } catch {
      showToast('Could not save — try again')
    } finally {
      setSaving(false)
    }
  }

  const handleConnect = async (target: CosmosNode) => {
    const source = selectedRef.current
    if (!source || target.id === source.id) return
    setLinkMode(false)
    try {
      await api.post('/knowledge/links', { sourceId: source.id, targetId: target.id })
      await loadGraph()
      openNode(source)
      showToast(`Connected “${source.title}” ↔ “${target.title}”`)
    } catch {
      showToast('Could not draw the connection')
    }
  }

  const handleDeleteNode = async () => {
    if (!selected) return
    if (!window.confirm(`Remove “${selected.title}” and its connections from your cosmos?`)) return
    try {
      await api.delete(`/knowledge/nodes/${selected.id}`)
      setSelected(null)
      setDetail(null)
      await loadGraph()
    } catch {
      showToast('Could not delete')
    }
  }

  const handleDeleteLink = async (linkId: string) => {
    try {
      await api.delete(`/knowledge/links/${linkId}`)
      await loadGraph()
      if (selectedRef.current) openNode(selectedRef.current)
    } catch {
      showToast('Could not remove connection')
    }
  }

  const handleDownloadAttachment = async (attachment: CosmosAttachment) => {
    try {
      const response = await api.get(`/files/${attachment.id}/download`, { responseType: 'blob' })
      const url = URL.createObjectURL(response.data as Blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = attachment.fileName
      anchor.click()
      URL.revokeObjectURL(url)
    } catch {
      showToast('Could not download the file')
    }
  }

  const handleDeleteAttachment = async (attachmentId: number) => {
    try {
      await api.delete(`/files/${attachmentId}`)
      if (selectedRef.current) await openNode(selectedRef.current)
      showToast('Attachment removed')
    } catch {
      showToast('Could not remove the attachment')
    }
  }

  const handleSaveEdit = async () => {
    if (!selected) return
    try {
      await api.put(`/knowledge/nodes/${selected.id}`, { title: editTitle, content: editContent })
      setEditMode(false)
      await loadGraph()
      const refreshed = { ...selected, title: editTitle }
      setSelected(refreshed)
      openNode(refreshed)
    } catch {
      showToast('Could not save changes')
    }
  }

  // ── Three.js world ─────────────────────────────────────────────────────────

  useEffect(() => {
    const mount = mountRef.current
    const labelLayer = labelLayerRef.current
    if (!mount || !labelLayer) return
    const world = worldRef.current
    world.disposed = false

    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches

    const scene = new THREE.Scene()
    scene.background = new THREE.Color(SPACE_BG)
    scene.fog = new THREE.FogExp2(SPACE_BG, 0.0018)

    const camera = new THREE.PerspectiveCamera(58, 1, 0.1, 2000)
    camera.position.set(0, 55, 130)

    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false })
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
    mount.appendChild(renderer.domElement)

    const controls = new OrbitControls(camera, renderer.domElement)
    controls.enableDamping = true
    controls.dampingFactor = 0.06
    controls.minDistance = 20
    controls.maxDistance = 500
    controls.autoRotate = !reduceMotion
    controls.autoRotateSpeed = 0.35

    // Starfield backdrop
    const starCount = 1400
    const starPositions = new Float32Array(starCount * 3)
    for (let i = 0; i < starCount; i++) {
      const r = 420 + Math.random() * 480
      const theta = Math.random() * Math.PI * 2
      const phi = Math.acos(2 * Math.random() - 1)
      starPositions[i * 3] = r * Math.sin(phi) * Math.cos(theta)
      starPositions[i * 3 + 1] = r * Math.cos(phi)
      starPositions[i * 3 + 2] = r * Math.sin(phi) * Math.sin(theta)
    }
    const starGeo = new THREE.BufferGeometry()
    starGeo.setAttribute('position', new THREE.BufferAttribute(starPositions, 3))
    const starMat = new THREE.PointsMaterial({ color: '#aaa4d3', size: 1.25, sizeAttenuation: true, transparent: true, opacity: 0.48 })
    const stars = new THREE.Points(starGeo, starMat)
    scene.add(stars)

    world.scene = scene
    world.camera = camera
    world.renderer = renderer
    world.controls = controls

    const resize = () => {
      const rect = mount.getBoundingClientRect()
      const w = Math.max(1, rect.width)
      const h = Math.max(1, rect.height)
      renderer.setSize(w, h)
      camera.aspect = w / h
      camera.updateProjectionMatrix()
    }
    resize()
    const resizeObserver = new ResizeObserver(resize)
    resizeObserver.observe(mount)

    // ── Pointer interaction ──────────────────────────────────────────────────
    const pointer = new THREE.Vector2()
    let downAt: { x: number; y: number } | null = null

    const pick = (event: PointerEvent): string | null => {
      const rect = renderer.domElement.getBoundingClientRect()
      pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1
      pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1
      world.raycaster.setFromCamera(pointer, camera)
      const hits = world.raycaster.intersectObjects([...world.sprites.values()], false)
      return hits.length > 0 ? (hits[0].object.userData.id as string) : null
    }

    const onPointerMove = (event: PointerEvent) => {
      const id = pick(event)
      world.hoverId = id
      renderer.domElement.style.cursor = id ? 'pointer' : 'grab'
    }

    const onPointerDown = (event: PointerEvent) => {
      downAt = { x: event.clientX, y: event.clientY }
    }

    const onPointerUp = (event: PointerEvent) => {
      if (!downAt) return
      const moved = Math.hypot(event.clientX - downAt.x, event.clientY - downAt.y)
      downAt = null
      if (moved > 6) return // it was a drag, not a click
      const id = pick(event)
      if (!id) return
      const node = world.graph.nodes.find(n => n.id === id)
      if (!node) return
      if (linkModeRef.current && selectedRef.current && selectedRef.current.id !== id) {
        handleConnectRef.current(node)
      } else {
        controls.autoRotate = false
        world.focusTarget = world.bodies.get(id)?.pos.clone() ?? null
        openNodeRef.current(node)
      }
    }

    renderer.domElement.addEventListener('pointermove', onPointerMove)
    renderer.domElement.addEventListener('pointerdown', onPointerDown)
    renderer.domElement.addEventListener('pointerup', onPointerUp)
    const stopAutoRotate = () => { controls.autoRotate = false }
    renderer.domElement.addEventListener('wheel', stopAutoRotate, { passive: true })

    // ── Simulation + render loop ─────────────────────────────────────────────
    const tmp = new THREE.Vector3()

    const step = () => {
      const bodies = world.bodies
      const nodes = world.graph.nodes
      if (world.alpha > 0.02 && nodes.length > 0) {
        const entries = nodes
          .map(n => ({ n, st: bodies.get(n.id) }))
          .filter((e): e is { n: CosmosNode; st: BodyState } => !!e.st)
        const a = world.alpha

        // Repulsion (sampled above 400 bodies to stay O(n·k))
        const sampled = entries.length > 400
        for (let i = 0; i < entries.length; i++) {
          const A = entries[i]
          const partners = sampled ? 60 : entries.length
          for (let s = 0; s < partners; s++) {
            const j = sampled ? Math.floor(Math.random() * entries.length) : s
            if (j === i) continue
            const B = entries[j]
            tmp.subVectors(A.st.pos, B.st.pos)
            const d2 = Math.max(tmp.lengthSq(), 4)
            if (d2 > 20000) continue
            tmp.multiplyScalar((900 / (d2 * Math.sqrt(d2))) * a)
            A.st.vel.add(tmp)
            if (!sampled) B.st.vel.sub(tmp)
          }
        }

        // Springs along links
        for (const link of world.lineLinks) {
          const A = bodies.get(link.sourceId)
          const B = bodies.get(link.targetId)
          if (!A || !B) continue
          const rest = link.kind === 'manual' || link.kind === 'wiki' ? 17 : 24
          tmp.subVectors(B.pos, A.pos)
          const d = Math.max(tmp.length(), 0.01)
          tmp.multiplyScalar(((d - rest) / d) * 0.035 * a)
          A.vel.add(tmp)
          B.vel.sub(tmp)
        }

        // Gentle centering + galactic-disk flattening, then integrate
        for (const { st } of entries) {
          st.vel.x -= st.pos.x * 0.0016 * a
          st.vel.z -= st.pos.z * 0.0016 * a
          st.vel.y -= st.pos.y * 0.006 * a
          st.pos.add(st.vel)
          st.vel.multiplyScalar(0.88)
        }
        world.alpha *= 0.992

        // Push positions into sprites + lines
        for (const [id, sprite] of world.sprites) {
          const st = bodies.get(id)
          if (st) sprite.position.copy(st.pos)
        }
        if (world.lineGeo) {
          const attr = world.lineGeo.getAttribute('position') as THREE.BufferAttribute
          world.lineLinks.forEach((link, i) => {
            const A = bodies.get(link.sourceId)
            const B = bodies.get(link.targetId)
            if (!A || !B) return
            attr.setXYZ(i * 2, A.pos.x, A.pos.y, A.pos.z)
            attr.setXYZ(i * 2 + 1, B.pos.x, B.pos.y, B.pos.z)
          })
          attr.needsUpdate = true
          world.lineGeo.computeBoundingSphere()
        }
      }

      // Hover/selection emphasis
      const focusId = world.hoverId ?? world.selectedId
      let focusSet: Set<string> | null = null
      if (focusId) {
        focusSet = new Set([focusId])
        for (const link of world.lineLinks) {
          if (link.sourceId === focusId) focusSet.add(link.targetId)
          if (link.targetId === focusId) focusSet.add(link.sourceId)
        }
      }
      world.focusIds = focusSet
      // Every star keeps glowing — focus reads as *brighter and bigger*, never as
      // the rest of the sky going dark. A line that ends in nothing looks broken.
      for (const [id, sprite] of world.sprites) {
        const material = sprite.material as THREE.SpriteMaterial
        const base = sprite.userData.baseOpacity as number
        const scale = sprite.userData.baseScale as number
        const emphasized = focusSet?.has(id)
        const targetOpacity = focusSet ? (emphasized ? 1 : base * 0.62) : base
        material.opacity += (targetOpacity - material.opacity) * 0.18
        const target = emphasized
          ? (id === focusId ? scale * 1.4 : scale * 1.12)
          : scale
        sprite.scale.setScalar(sprite.scale.x + (target - sprite.scale.x) * 0.2)
      }

      // Camera glide toward a clicked star
      if (world.focusTarget && world.controls) {
        world.controls.target.lerp(world.focusTarget, 0.08)
        if (world.controls.target.distanceTo(world.focusTarget) < 0.5) world.focusTarget = null
      }

      updateLabels()
      controls.update()
      renderer.render(scene, camera)
      if (!world.disposed) world.raf = requestAnimationFrame(step)
    }

    // ── Labels (HTML overlay, capped) ────────────────────────────────────────
    const updateLabels = () => {
      const camera = world.camera!
      const rect = renderer.domElement.getBoundingClientRect()
      const wanted: { id: string; title: string; kind: string }[] = []
      const pushNode = (id: string) => {
        const n = world.graph.nodes.find(x => x.id === id)
        if (n && !wanted.some(w => w.id === id)) wanted.push({ id, title: n.title, kind: n.kind })
      }
      // Hub labels first (top-degree concepts/tags), then interaction focus
      const hubs = world.graph.nodes
        .filter(n => n.kind !== 'note')
        .sort((a, b) => (world.degrees.get(b.id) ?? 0) - (world.degrees.get(a.id) ?? 0))
        .slice(0, 14)
      hubs.forEach(h => pushNode(h.id))
      if (world.selectedId) pushNode(world.selectedId)
      if (world.hoverId) pushNode(world.hoverId)
      world.focusIds?.forEach(id => { if (wanted.length < 34) pushNode(id) })

      while (world.labelPool.length < wanted.length) {
        const el = document.createElement('div')
        el.className = 'cosmos-label'
        labelLayer.appendChild(el)
        world.labelPool.push(el)
      }
      world.labelPool.forEach((el, i) => {
        const item = wanted[i]
        if (!item) { el.style.display = 'none'; return }
        const st = world.bodies.get(item.id)
        if (!st) { el.style.display = 'none'; return }
        tmp.copy(st.pos).project(camera)
        if (tmp.z > 1 || Math.abs(tmp.x) > 1.05 || Math.abs(tmp.y) > 1.05) {
          el.style.display = 'none'
          return
        }
        el.style.display = 'block'
        el.textContent = item.title
        el.dataset.kind = item.kind
        el.style.transform = `translate(-50%, 8px) translate(${((tmp.x + 1) / 2) * rect.width}px, ${((1 - tmp.y) / 2) * rect.height}px)`
      })
    }

    world.raf = requestAnimationFrame(step)

    return () => {
      world.disposed = true
      cancelAnimationFrame(world.raf)
      resizeObserver.disconnect()
      renderer.domElement.removeEventListener('pointermove', onPointerMove)
      renderer.domElement.removeEventListener('pointerdown', onPointerDown)
      renderer.domElement.removeEventListener('pointerup', onPointerUp)
      renderer.domElement.removeEventListener('wheel', stopAutoRotate)
      controls.dispose()
      world.sprites.forEach(s => (s.material as THREE.SpriteMaterial).dispose())
      world.sprites.clear()
      world.lineGeo?.dispose()
      starGeo.dispose()
      starMat.dispose()
      world.textures.forEach(t => t.dispose())
      world.textures.clear()
      world.labelPool.forEach(el => el.remove())
      world.labelPool = []
      renderer.dispose()
      mount.removeChild(renderer.domElement)
    }
  }, [])

  // Stable refs so the (mount-once) scene effect can call the latest handlers
  const openNodeRef = useRef(openNode)
  const handleConnectRef = useRef(handleConnect)
  useEffect(() => { openNodeRef.current = openNode })
  useEffect(() => { handleConnectRef.current = handleConnect })

  useEffect(() => { worldRef.current.selectedId = selected?.id ?? null }, [selected])

  // ── Rebuild scene objects when the graph changes ───────────────────────────
  useEffect(() => {
    const world = worldRef.current
    const scene = world.scene
    if (!scene) return
    world.graph = graph

    // Node degrees drive size
    const degrees = new Map<string, number>()
    graph.links.forEach(l => {
      degrees.set(l.sourceId, (degrees.get(l.sourceId) ?? 0) + 1)
      degrees.set(l.targetId, (degrees.get(l.targetId) ?? 0) + 1)
    })
    world.degrees = degrees

    // Keep positions of surviving bodies; seed new ones deterministically
    const nextBodies = new Map<string, BodyState>()
    for (const node of graph.nodes) {
      const existing = world.bodies.get(node.id)
      if (existing) {
        nextBodies.set(node.id, existing)
        continue
      }
      const isHub = node.kind !== 'note'
      const theta = hash01(node.id, 1) * Math.PI * 2
      const radius = (isHub ? 14 : 34) + hash01(node.id, 2) * (isHub ? 14 : 22)
      const y = (hash01(node.id, 3) - 0.5) * 14
      nextBodies.set(node.id, {
        pos: new THREE.Vector3(Math.cos(theta) * radius, y, Math.sin(theta) * radius),
        vel: new THREE.Vector3(),
      })
    }
    world.bodies = nextBodies

    // Glow texture per color (cached)
    const textureFor = (color: string): THREE.Texture => {
      const cached = world.textures.get(color)
      if (cached) return cached
      const size = 128
      const canvas = document.createElement('canvas')
      canvas.width = size
      canvas.height = size
      const ctx = canvas.getContext('2d')!
      const gradient = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2)
      // Small white-hot core, a wide saturated body, then a long halo — the halo
      // is what reads as "glow" against the bright canvas.
      gradient.addColorStop(0, '#ffffff')
      gradient.addColorStop(0.12, color)
      gradient.addColorStop(0.3, color + 'cc')
      gradient.addColorStop(0.55, color + '55')
      gradient.addColorStop(1, color + '00')
      ctx.fillStyle = gradient
      ctx.fillRect(0, 0, size, size)
      const texture = new THREE.CanvasTexture(canvas)
      world.textures.set(color, texture)
      return texture
    }

    // Rebuild sprites
    world.sprites.forEach(s => { scene.remove(s); (s.material as THREE.SpriteMaterial).dispose() })
    world.sprites.clear()
    for (const node of graph.nodes) {
      const color = KIND_COLORS[node.kind] ?? KIND_COLORS.note
      const isHub = node.kind !== 'note'
      const degree = degrees.get(node.id) ?? 0
      const material = new THREE.SpriteMaterial({
        map: textureFor(color),
        transparent: true,
        depthWrite: false,
        blending: THREE.NormalBlending,
      })
      const sprite = new THREE.Sprite(material)
      const scale = (isHub ? 7.5 : 4.6) + Math.min(6, degree * 0.55)
      sprite.scale.setScalar(scale)
      sprite.userData.id = node.id
      sprite.userData.baseScale = scale
      sprite.userData.baseOpacity = isHub ? 0.98 : 0.9
      material.opacity = sprite.userData.baseOpacity
      const st = world.bodies.get(node.id)!
      sprite.position.copy(st.pos)
      scene.add(sprite)
      world.sprites.set(node.id, sprite)
    }

    // Rebuild link lines (one LineSegments, vertex-colored, additive)
    if (world.lineGeo) {
      const old = scene.getObjectByName('cosmos-links')
      if (old) scene.remove(old)
      world.lineGeo.dispose()
      world.lineGeo = undefined
    }
    world.lineLinks = graph.links
    if (graph.links.length > 0) {
      const positions = new Float32Array(graph.links.length * 6)
      const colors = new Float32Array(graph.links.length * 6)
      const c = new THREE.Color()
      graph.links.forEach((link, i) => {
        const A = world.bodies.get(link.sourceId)!
        const B = world.bodies.get(link.targetId)!
        positions.set([A.pos.x, A.pos.y, A.pos.z, B.pos.x, B.pos.y, B.pos.z], i * 6)
        c.set(LINK_COLORS[link.kind] ?? '#7668b4')
        const intensity = link.kind === 'manual' ? 0.95 : 0.72
        colors.set([c.r * intensity, c.g * intensity, c.b * intensity, c.r * intensity, c.g * intensity, c.b * intensity], i * 6)
      })
      const geometry = new THREE.BufferGeometry()
      geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3))
      geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3))
      const material = new THREE.LineBasicMaterial({
        vertexColors: true,
        transparent: true,
        opacity: 0.72,
        blending: THREE.NormalBlending,
        depthWrite: false,
      })
      const lines = new THREE.LineSegments(geometry, material)
      lines.name = 'cosmos-links'
      scene.add(lines)
      world.lineGeo = geometry
    }

    world.alpha = 1 // reheat the simulation so the new layout settles
  }, [graph])

  // Esc cancels link mode / closes panel
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return
      if (linkModeRef.current) setLinkMode(false)
      else { setSelected(null); setDetail(null) }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  // ── Search ────────────────────────────────────────────────────────────────
  const matches = search.trim().length >= 2
    ? graph.nodes.filter(n => n.title.toLowerCase().includes(search.trim().toLowerCase())).slice(0, 8)
    : []

  const flyTo = (node: CosmosNode) => {
    const world = worldRef.current
    if (world.controls) world.controls.autoRotate = false
    world.focusTarget = world.bodies.get(node.id)?.pos.clone() ?? null
    setSearch('')
    openNode(node)
  }

  const noteCount = graph.nodes.filter(n => n.kind === 'note').length

  // ── Sync modules: import existing content from every module ───────────────
  const navigate = useNavigate()
  const [syncing, setSyncing] = useState(false)
  const handleSync = async () => {
    if (syncing) return
    setSyncing(true)
    try {
      const response = await api.post('/knowledge/backfill')
      const { imported } = response.data as { imported: number }
      await loadGraph()
      showToast(imported > 0
        ? `Imported ${imported} ${imported === 1 ? 'item' : 'items'} from your modules ✦`
        : 'Your modules are already fully woven in')
    } catch {
      showToast('Sync failed — try again')
    } finally {
      setSyncing(false)
    }
  }

  // ── AI routing: the AI proposes a plan (module + goal + brief), the user
  //    refines the brief and can override the module before sending ──────────
  interface RouteTarget { module: string; path: string; label: string; kind?: string; description?: string }
  interface RoutePlan {
    module: string; path: string; label: string
    goal: string; reason: string; mode?: 'topic' | 'forecast'; input: string; title: string
    nodeId: string
    targets: RouteTarget[]
  }
  const [routeIntent, setRouteIntent] = useState('')
  const [routing, setRouting] = useState(false)
  const [routePlan, setRoutePlan] = useState<RoutePlan | null>(null)
  const [routeDraft, setRouteDraft] = useState('')
  const [routeModule, setRouteModule] = useState('')

  const handleRoute = async () => {
    if (!selected || routing) return
    setRouting(true)
    try {
      const response = await api.post('/knowledge/route', {
        nodeId: selected.id,
        intent: routeIntent.trim() || undefined,
      })
      const plan = response.data as RoutePlan
      setRoutePlan(plan)
      setRouteDraft(plan.input)
      setRouteModule(plan.module)
    } catch {
      showToast('Could not route this star — try again')
    } finally {
      setRouting(false)
    }
  }

  // The module currently chosen in the refine step, and the picker grouped by
  // how the module receives the brief.
  const activeTarget = routePlan?.targets.find(t => t.module === routeModule) ?? null
  const routeGroups = routePlan
    ? [
        { kind: 'text', label: 'Works with the brief', targets: routePlan.targets.filter(t => t.kind !== 'app') },
        { kind: 'app', label: 'Opens the tool', targets: routePlan.targets.filter(t => t.kind === 'app') },
      ].filter(g => g.targets.length > 0)
    : []

  const confirmRoute = () => {
    if (!routePlan || !routeDraft.trim()) return
    const target = routePlan.targets.find(t => t.module === routeModule) ?? routePlan
    setCosmosHandoff({
      path: target.path,
      input: routeDraft.trim(),
      title: routePlan.title,
      sourceNodeId: routePlan.nodeId,
      mode: routePlan.mode,
    })
    showToast(`Taking this star to ${target.label}…`)
    window.setTimeout(() => navigate(target.path), 450)
  }

  useEffect(() => {
    setRouteIntent(''); setRouting(false); setRoutePlan(null); setRouteDraft(''); setRouteModule('')
  }, [selected?.id])

  // ── Render ────────────────────────────────────────────────────────────────
  return (
    <div className="cosmos-page">
      <div className="cosmos-stage" ref={mountRef} />
      <div className="cosmos-labels" ref={labelLayerRef} />

      {/* Ask the notes directly. Floating, because the page is a full-bleed
          canvas — an inline panel would sit under the graph. */}
      <div className="cosmos-ask">
        <ModuleVoiceChat module="cosmos" />
      </div>

      {/* Top bar: search + stats */}
      <div className="cosmos-topbar">
        <div className="cosmos-search">
          <PhosphorIcon className="bi bi-search" />
          <input
            type="text"
            placeholder="Find a star…"
            value={search}
            onChange={e => setSearch(e.target.value)}
          />
          {matches.length > 0 && (
            <div className="cosmos-search-results">
              {matches.map(m => (
                <button key={m.id} onClick={() => flyTo(m)}>
                  <span className="cosmos-dot" data-kind={m.kind} />
                  {m.title}
                </button>
              ))}
            </div>
          )}
        </div>
        <div className="cosmos-topbar-right">
          <button
            className="cosmos-sync"
            onClick={handleSync}
            disabled={syncing}
            title="Import your existing content from every module into the cosmos"
          >
            {syncing ? <span className="cosmos-spinner" /> : <PhosphorIcon className="bi bi-arrow-repeat" />}
            {syncing ? 'Weaving…' : 'Sync modules'}
          </button>
          <div className="cosmos-stats">
            {noteCount} {noteCount === 1 ? 'note' : 'notes'} · {graph.links.length} connections
          </div>
        </div>
      </div>

      {/* Link-mode banner */}
      {linkMode && selected && (
        <div className="cosmos-linkmode">
          <PhosphorIcon className="bi bi-stars" /> Click another star to connect it to
          “{selected.title}” <span>· Esc to cancel</span>
        </div>
      )}

      {/* Empty state */}
      {!loading && graph.nodes.length === 0 && (
        <div className="cosmos-empty">
          <PhosphorIcon className="bi bi-stars" />
          <h3>Your cosmos is empty</h3>
          <p>
            Drop anything below — a thought, a paste, a file, a finding. Use{' '}
            <code>[[wiki links]]</code> and <code>#tags</code>, or let the AI find the
            concepts. Everything you save, search, and create becomes a star, and the
            connections between them light up.
          </p>
        </div>
      )}

      {loading && <div className="cosmos-loading"><span className="cosmos-spinner" /> Charting your cosmos…</div>}

      {/* Dump dock */}
      <div
        className={`cosmos-dock ${draggingFiles ? 'dragging' : ''}`}
        onDragEnter={e => { e.preventDefault(); setDraggingFiles(true) }}
        onDragOver={e => { e.preventDefault(); setDraggingFiles(true) }}
        onDragLeave={e => {
          if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDraggingFiles(false)
        }}
        onDrop={e => {
          e.preventDefault()
          setDraggingFiles(false)
          if (e.dataTransfer.files.length > 0) queueFiles(e.dataTransfer.files)
        }}
      >
        <input
          ref={fileInputRef}
          className="cosmos-file-input"
          type="file"
          multiple
          aria-label="Attach files to Cosmos"
          onChange={e => {
            if (e.target.files) queueFiles(e.target.files)
            e.target.value = ''
          }}
        />
        <div className="cosmos-dock-main">
          {dumpFiles.length > 0 && (
            <div className="cosmos-file-chips" aria-label="Files ready to upload">
              {dumpFiles.map(file => (
                <span className="cosmos-file-chip" key={`${file.name}-${file.size}-${file.lastModified}`}>
                  <PhosphorIcon className={`bi ${fileIcon(file.name)}`} />
                  <span title={file.name}>{file.name}</span>
                  <small>{formatFileSize(file.size)}</small>
                  <button
                    type="button"
                    onClick={() => removeQueuedFile(file)}
                    disabled={saving}
                    aria-label={`Remove ${file.name}`}
                  >
                    <PhosphorIcon className="bi bi-x" />
                  </button>
                </span>
              ))}
            </div>
          )}
          <textarea
            placeholder="Message your cosmos"
            value={dumpText}
            onChange={e => setDumpText(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) handleDump() }}
            rows={Math.min(6, Math.max(1, dumpText.split('\n').length))}
          />
          <div className="cosmos-dock-actions">
            <button
              type="button"
              className="cosmos-attach"
              onClick={() => fileInputRef.current?.click()}
              disabled={saving || dumpFiles.length >= MAX_COSMOS_FILES}
              title="Attach any file type (up to 10 MB each)"
              aria-label="Attach files"
            >
              <PhosphorIcon className="bi bi-plus-lg" />
            </button>
            <button
              className="cosmos-dock-save"
              onClick={handleDump}
              disabled={saving || (!dumpText.trim() && dumpFiles.length === 0)}
              aria-label={saving ? 'Adding to Cosmos' : 'Add to Cosmos'}
              title={saving ? 'Adding to Cosmos' : 'Add to Cosmos (Ctrl or Command + Enter)'}
            >
              {saving ? <span className="cosmos-spinner" /> : <PhosphorIcon className="bi bi-arrow-up" />}
            </button>
          </div>
        </div>
      </div>

      {/* Legend */}
      <div className="cosmos-legend">
        <span><span className="cosmos-dot" data-kind="concept" /> Concept</span>
        <span><span className="cosmos-dot" data-kind="tag" /> Tag</span>
        <span><span className="cosmos-dot" data-kind="note" /> Note</span>
      </div>

      {/* Detail panel */}
      {selected && (
        <aside className="cosmos-panel">
          <div className="cosmos-panel-head">
            <span className="cosmos-chip" data-kind={selected.kind}>{selected.kind}</span>
            {selected.kind === 'note' && <span className="cosmos-chip cosmos-chip-source">{selected.source}{selected.sourceModule ? ` · ${selected.sourceModule}` : ''}</span>}
            <button className="cosmos-panel-close" onClick={() => { setSelected(null); setDetail(null); setLinkMode(false) }} aria-label="Close">
              <PhosphorIcon className="bi bi-x-lg" />
            </button>
          </div>

          {editMode ? (
            <div className="cosmos-edit">
              <input value={editTitle} onChange={e => setEditTitle(e.target.value)} placeholder="Title" />
              <textarea value={editContent} onChange={e => setEditContent(e.target.value)} rows={10} placeholder="Content — [[links]] and #tags re-weave on save" />
              <div className="cosmos-panel-actions">
                <button className="cosmos-btn-primary" onClick={handleSaveEdit}><PhosphorIcon className="bi bi-check-lg" /> Save</button>
                <button className="cosmos-btn-ghost" onClick={() => setEditMode(false)}>Cancel</button>
              </div>
            </div>
          ) : (
            <>
              <h3 className="cosmos-panel-title">{selected.title}</h3>
              {(detail?.content ?? selected.content) && (
                <div className="cosmos-panel-content">{detail?.content ?? selected.content}</div>
              )}
              {detail && (detail.attachments?.length ?? 0) > 0 && (
                <div className="cosmos-attachments">
                  <h4>Files ({detail.attachments.length})</h4>
                  {detail.attachments.map(attachment => (
                    <div className="cosmos-attachment" key={attachment.id}>
                      <button
                        className="cosmos-attachment-download"
                        onClick={() => handleDownloadAttachment(attachment)}
                        title={`Download ${attachment.fileName}`}
                      >
                        <PhosphorIcon className={`bi ${fileIcon(attachment.fileName)}`} />
                        <span>
                          <strong>{attachment.fileName}</strong>
                          <small>{formatFileSize(attachment.fileSize)}</small>
                        </span>
                        <PhosphorIcon className="bi bi-download" />
                      </button>
                      <button
                        className="cosmos-attachment-remove"
                        onClick={() => handleDeleteAttachment(attachment.id)}
                        aria-label={`Remove ${attachment.fileName}`}
                        title="Remove attachment"
                      >
                        <PhosphorIcon className="bi bi-x" />
                      </button>
                    </div>
                  ))}
                </div>
              )}
              <div className="cosmos-panel-meta">
                Added {new Date(selected.createdAt).toLocaleDateString()}
                {detail?.sourceRef && <> · <a href={detail.sourceRef} target="_blank" rel="noreferrer">source</a></>}
              </div>

              <div className="cosmos-panel-actions">
                <button
                  className={`cosmos-btn-primary ${linkMode ? 'active' : ''}`}
                  onClick={() => setLinkMode(v => !v)}
                >
                  <PhosphorIcon className="bi bi-bezier2" /> {linkMode ? 'Cancel connect' : 'Connect'}
                </button>
                {selected.kind === 'note' && (
                  <button
                    className="cosmos-btn-ghost"
                    onClick={() => {
                      setEditTitle(selected.title)
                      setEditContent(detail?.content ?? selected.content ?? '')
                      setEditMode(true)
                    }}
                  >
                    <PhosphorIcon className="bi bi-pencil" /> Edit
                  </button>
                )}
                <button className="cosmos-btn-danger" onClick={handleDeleteNode}><PhosphorIcon className="bi bi-trash" /></button>
              </div>

              {/* AI routing: this star talks to the rest of the app.
                  Step 1: state your goal → AI proposes module + brief.
                  Step 2: refine the brief / override the module → send. */}
              <div className="cosmos-route">
                <h4>Take this somewhere</h4>
                {!routePlan ? (
                  <>
                    <div className="cosmos-route-row">
                      <input
                        type="text"
                        value={routeIntent}
                        onChange={e => setRouteIntent(e.target.value)}
                        onKeyDown={e => { if (e.key === 'Enter') handleRoute() }}
                        placeholder="Your goal — diagram it, research it, post it… (optional)"
                        disabled={routing}
                      />
                      <button className="cosmos-btn-primary" onClick={handleRoute} disabled={routing} title="AI proposes the best module and a work brief — you refine it before it goes">
                        {routing ? <span className="cosmos-spinner" /> : <PhosphorIcon className="bi bi-send" />}
                      </button>
                    </div>
                    <p className="cosmos-route-hint">AI proposes a destination and a brief; you refine both before anything is sent.</p>
                  </>
                ) : (
                  <div className="cosmos-route-plan">
                    <div className="cosmos-route-plan-head">
                      <select
                        value={routeModule}
                        onChange={e => setRouteModule(e.target.value)}
                        aria-label="Destination module"
                        title={activeTarget?.description}
                      >
                        {routeGroups.map(group => (
                          <optgroup key={group.kind} label={group.label}>
                            {group.targets.map(t => (
                              <option key={t.module} value={t.module}>
                                {t.label}{t.module === routePlan.module ? ' ✦ AI pick' : ''}
                              </option>
                            ))}
                          </optgroup>
                        ))}
                      </select>
                      {routeModule === routePlan.module && (
                        <span className="cosmos-route-badge" title="The AI chose this destination for you">
                          <PhosphorIcon className="bi bi-stars" /> AI pick
                        </span>
                      )}
                    </div>
                    {/* What this module actually does — so overriding the AI pick
                        is an informed choice, not a guess at a name. */}
                    {(activeTarget?.description || (routePlan.reason && routeModule === routePlan.module)) && (
                      <p className="cosmos-route-about">
                        <PhosphorIcon className="bi bi-info-circle" />
                        <span>
                          {activeTarget?.description}
                          {activeTarget?.kind === 'app' && <em> The brief opens with the tool rather than as text input.</em>}
                          {routePlan.reason && routeModule === routePlan.module && (
                            <span className="cosmos-route-reason"> — {routePlan.reason}</span>
                          )}
                        </span>
                      </p>
                    )}
                    {routePlan.goal && <p className="cosmos-route-goal"><PhosphorIcon className="bi bi-bullseye" /> {routePlan.goal}</p>}
                    <textarea
                      value={routeDraft}
                      onChange={e => setRouteDraft(e.target.value)}
                      rows={5}
                      placeholder="Refine the brief that will be carried over…"
                    />
                    <div className="cosmos-panel-actions">
                      <button className="cosmos-btn-primary" onClick={confirmRoute} disabled={!routeDraft.trim()}>
                        <PhosphorIcon className="bi bi-send" /> Send to {routePlan.targets.find(t => t.module === routeModule)?.label ?? routePlan.label}
                      </button>
                      <button className="cosmos-btn-ghost" onClick={() => { setRoutePlan(null); setRouteDraft(''); }}>
                        Back
                      </button>
                    </div>
                  </div>
                )}
              </div>

              {detail && detail.links.length > 0 && (
                <div className="cosmos-panel-links">
                  <h4>Connections ({detail.links.length})</h4>
                  {detail.links.map(link => {
                    const otherId = link.sourceId === selected.id ? link.targetId : link.sourceId
                    const other = graph.nodes.find(n => n.id === otherId)
                    if (!other) return null
                    return (
                      <div key={link.id} className="cosmos-panel-link">
                        <button className="cosmos-panel-link-jump" onClick={() => flyTo(other)}>
                          <span className="cosmos-dot" data-kind={other.kind} />
                          <span className="cosmos-panel-link-title">{other.title}</span>
                          <span className="cosmos-panel-link-kind">{link.kind}{link.label ? ` · ${link.label}` : ''}</span>
                        </button>
                        <button className="cosmos-panel-link-remove" onClick={() => handleDeleteLink(link.id)} aria-label="Remove connection">
                          <PhosphorIcon className="bi bi-x" />
                        </button>
                      </div>
                    )
                  })}
                </div>
              )}
            </>
          )}
        </aside>
      )}

      {toast && <div className="cosmos-toast">{toast}</div>}
    </div>
  )
}
