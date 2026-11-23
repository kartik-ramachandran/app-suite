// The station deck. three.js draws the scene (deck, viewports, desks, crew,
// lamps, envelopes); text lives in a React overlay above the canvas so it stays
// crisp and themeable. The floor reports desk positions back through `onLayout`
// whenever geometry changes, and the overlay anchors to those.
//
// This replaced a Pixi pixel-art floor. The exported surface — `createFloor`,
// `FloorHandle`, `FloorSlot` — is deliberately unchanged, because 42 pages and
// GameField.tsx are built against it. `FloorSlot` coordinates are still CSS
// pixels relative to the host; they are now produced by projecting the 3D desk
// anchors to screen space rather than by multiplying a world position by a
// pixel scale.

import * as THREE from 'three'
import type { FieldAgent, FieldModel } from './types'
import { lookFor, type AvatarLook } from './sprites'
import {
  CELL_W,
  CELL_D,
  PALETTES,
  buildChair,
  buildDesk,
  buildEnvelope,
  buildLamp,
  buildProgressBar,
  buildProps,
  buildBreakArea,
  buildCeilingLights,
  buildCorridor,
  buildCubicle,
  buildMeetingRoom,
  buildReception,
  buildRoom,
  buildSelectionRing,
  buildStars,
  makeMaterials,
  type Materials,
} from './three/kit'
import { buildCrew, type CrewFigure, type Pose } from './three/crew'
import { buildPostChain, type PostChain } from './three/postfx'
import { attachControls, type DeckControls } from './three/controls'
import { SKIES, buildContactShadow, buildSky, type SkyRig } from './three/sky'

/** Where a desk sits on screen, in CSS pixels relative to the host element. */
export interface FloorSlot {
  id: string
  /** Centre of the desk. */
  x: number
  y: number
  /** Cell footprint, for hit targets. */
  w: number
  h: number
  /** Clear of the tallest pose — speech bubbles hang off this. */
  headY: number
  /** Where the nameplate sits, just under the desk. */
  plateY: number
  /** Top of the cell, for the click target. */
  top: number
}

export interface FloorHandle {
  update(model: FieldModel): void
  setTheme(dark: boolean): void
  resize(): void
  /**
   * Mark one desk as chosen, or null to clear. Drawn in the scene as a ring on
   * the deck plus a small lift of the desk itself, so the marker shares the
   * floor's perspective instead of floating over it.
   */
  setSelected(id: string | null): void
  destroy(): void
}

const LAMP_COLORS: Record<string, number> = {
  idle: 0x8d8371,
  walking: 0xd97a2b,
  working: 0x3f8f6d,
  blocked: 0xd9b26a,
  done: 0x4a7fb5,
  failed: 0xb5445f,
}

interface Pt { x: number; z: number }

interface AgentNode {
  id: string
  look: AvatarLook
  root: THREE.Group
  crew: CrewFigure
  shadow: THREE.Mesh
  chair: THREE.Group
  desk: THREE.Group
  cubicle: THREE.Group
  screen: THREE.Mesh
  lamp: ReturnType<typeof buildLamp>
  bar: ReturnType<typeof buildProgressBar>
  accent: THREE.Color
  /** Cell origin in world units. */
  cell: Pt
  seat: Pt
  stand: Pt
  pos: Pt
  path: Pt[]
  walkSpeed: number
  facing: number
  pose: Pose
  drawnPose: Pose | null
  status: FieldAgent['status']
  progress: number
  boss: boolean
  placed: boolean
  statusClock: number
  frameClock: number
  bob: number
}

interface Flight {
  mesh: THREE.Mesh
  from: THREE.Vector3
  to: THREE.Vector3
  t: number
  dur: number
}

export async function createFloor(
  host: HTMLElement,
  opts: {
    dark: boolean
    /** Tallest the host is willing to make the floor, in CSS pixels. */
    maxHeight: number
    onLayout: (slots: FloorSlot[], contentHeight: number) => void
    /**
     * Checked after init. React StrictMode mounts effects twice in development;
     * without this the throwaway mount would build a whole WebGL context only to
     * tear it straight down, which upsets any other floor on the page.
     */
    shouldAbort?: () => boolean
  },
): Promise<FloorHandle | null> {
  if (opts.shouldAbort?.()) return null

  let dark = opts.dark
  let palette = PALETTES[dark ? 'dark' : 'light']

  const width = Math.max(host.clientWidth, 240)
  // No upper clamp: the caller owns its layout, and a full-page deck is a
  // legitimate use. Only the floor is guaranteed.
  const height = Math.max(opts.maxHeight, 240)

  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false, powerPreference: 'low-power' })
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
  renderer.setSize(width, height, false)
  renderer.shadowMap.enabled = true
  renderer.shadowMap.type = THREE.PCFSoftShadowMap
  renderer.toneMapping = THREE.ACESFilmicToneMapping
  renderer.toneMappingExposure = dark ? 1.35 : 0.92

  if (opts.shouldAbort?.()) {
    renderer.dispose()
    return null
  }

  renderer.domElement.style.display = 'block'
  renderer.domElement.style.width = '100%'
  host.appendChild(renderer.domElement)

  const scene = new THREE.Scene()
  // No scene.background: the sky dome below draws it, and a flat colour would
  // simply cover the dome up.
  scene.fog = new THREE.Fog(palette.fog, dark ? 55 : 100, dark ? 150 : 260)

  // Orthographic keeps the whole deck the same size front to back, which is
  // what makes the tilt-shift read as a scale model instead of a wide lens.
  const camera = new THREE.OrthographicCamera(-10, 10, 6, -6, 0.1, 200)
  camera.position.set(14, 15, 18)
  camera.lookAt(0, 0, 0)

  let materials: Materials = makeMaterials(palette)

  // --- lighting -------------------------------------------------------------
  // --- lighting -------------------------------------------------------------
  // The look comes from image-based lighting, not from an ambient term: with
  // scene.environment set, every material gets real reflections and a
  // DIRECTIONAL ambient, so a surface facing up reads differently from one
  // facing sideways. A flat AmbientLight cannot do that and is what made the
  // earlier version look like shaded cardboard.
  const sky: SkyRig = buildSky(renderer, SKIES[dark ? 'dark' : 'light'])
  scene.add(sky.dome)
  scene.environment = sky.envTexture
  scene.environmentIntensity = dark ? 1.5 : 1.35

  // A small ambient remains only to keep deep shadow from going pure black.
  const ambient = new THREE.AmbientLight(0xffffff, dark ? 0.55 : 0.4)
  scene.add(ambient)

  // Sky above, warm bounce from the deck below.
  const hemi = new THREE.HemisphereLight(
    dark ? 0x8899cc : 0xbfd4f2,
    dark ? 0x4a4038 : 0x9a8f7a,
    dark ? 0.95 : 0.85,
  )
  scene.add(hemi)

  const key = new THREE.DirectionalLight(0xfff2e0, dark ? 2.1 : 1.9)
  key.position.set(9, 16, 8)
  key.castShadow = true
  key.shadow.mapSize.set(1024, 1024)
  key.shadow.camera.near = 1
  key.shadow.camera.far = 60
  const shadowSpan = 22
  key.shadow.camera.left = -shadowSpan
  key.shadow.camera.right = shadowSpan
  key.shadow.camera.top = shadowSpan
  key.shadow.camera.bottom = -shadowSpan
  key.shadow.bias = -0.0012
  scene.add(key)

  const rim = new THREE.DirectionalLight(dark ? 0x8fa8d8 : 0xbcd0ee, dark ? 0.55 : 0.3)
  rim.position.set(-14, 9, -11)
  scene.add(rim)

  // --- scene graph ----------------------------------------------------------
  const worldRoot = new THREE.Group()
  scene.add(worldRoot)

  let room: ReturnType<typeof buildRoom> | null = null
  let props: THREE.Group | null = null
  /** Meeting room, break area, reception, corridor, ceiling — rebuilt per layout. */
  let zones: { group: THREE.Group; dispose(): void } | null = null
  let stars = buildStars(palette, 70)
  scene.add(stars.points)

  const deskLayer = new THREE.Group()
  const flightLayer = new THREE.Group()
  worldRoot.add(deskLayer, flightLayer)

  // One texture + material + geometry shared by every blob shadow.
  const contact = buildContactShadow(128)

  const selection = buildSelectionRing(0xffb347)
  worldRoot.add(selection.ring)
  let selectedId: string | null = null

  const agents = new Map<string, AgentNode>()
  const flights: Flight[] = []
  const seenMessages = new Set<string>()
  let order: string[] = []

  let roomW = 16
  let roomD = 12
  /** Where the crew walk in from — the airlock, at the back-left of the deck. */
  let doorPos: Pt = { x: -6, z: -4 }
  let running = false
  let lastLayoutSignature = ''
  let contentHeight = height

  // The framing the LAYOUT chose, before any user pan/zoom. Controls transform
  // this rather than the live camera, so a relayout does not fight the user.
  let baseFrustum = { left: -10, right: 10, top: 6, bottom: -6 }

  const post: PostChain = buildPostChain(renderer, scene, camera, width, height)

  const controls: DeckControls = attachControls(renderer.domElement, camera, {
    baseFrustum: () => baseFrustum,
    // Panning and zooming move every desk on screen, so the DOM overlay has to
    // be told or the nameplates drift away from the desks they belong to.
    onChange: () => emitSlots(),
  })

  // --- layout ---------------------------------------------------------------

  /**
   * Chooses a grid, sizes the room to it, frames the camera on the result, then
   * reports the projected desk anchors. Height is taken from the host's width
   * and a budget — never from the host's current height, or the two would chase
   * each other.
   */
  function relayout() {
    const cw = Math.max(host.clientWidth, 240)
    const budget = Math.max(opts.maxHeight, 240)

    const workers = order.filter(id => !agents.get(id)?.boss)
    const bossId = order.find(id => agents.get(id)?.boss)
    const inGrid = workers.length + (bossId && workers.length === 0 ? 1 : 0)
    const count = Math.max(inGrid, 1)

    // Roughly square, biased wide because the viewport is landscape.
    const cols = Math.max(1, Math.min(count, Math.ceil(Math.sqrt(count * 1.7))))
    const rows = Math.ceil(count / cols)

    // A corner office only makes sense once the open plan still has room.
    const cornerOffice = !!bossId && workers.length > 0 && cols >= 2

    // The room now holds more than desks: a meeting room and a break area sit
    // along the right-hand side, and reception takes a strip at the front.
    const SIDE_ZONE_W = 7.0
    const RECEPTION_D = 3.2
    // Only as big as the plan needs: excess floor reads as an empty warehouse.
    roomW = Math.max(17, cols * CELL_W + 2.2 + (cornerOffice ? CELL_W + 1.2 : 0) + SIDE_ZONE_W)
    roomD = Math.max(13, rows * CELL_D + 3.0 + RECEPTION_D)

    // The deck is drawn to fit; the viewport height follows the room's aspect,
    // clamped to the caller's budget.
    const aspect = roomW / roomD
    // Use the full budget when the room is big enough to need it, rather than
    // only what the aspect suggests — otherwise a tall page keeps a short deck.
    const wanted = Math.max(300, cw / Math.max(aspect * 0.72, 0.9))
    contentHeight = Math.round(Math.min(budget, Math.max(wanted, budget * 0.82)))

    renderer.setSize(cw, contentHeight, false)
    post.setSize(cw, contentHeight)

    // Frame the whole deck: fit the room's diagonal into the ortho box.
    const viewAspect = cw / contentHeight
    const fit = Math.max(roomW / 1.85, roomD / 1.5) * 0.72
    baseFrustum = {
      left: -fit * viewAspect,
      right: fit * viewAspect,
      top: fit,
      bottom: -fit,
    }
    camera.left = baseFrustum.left
    camera.right = baseFrustum.right
    camera.top = baseFrustum.top
    camera.bottom = baseFrustum.bottom
    const dist = Math.max(roomW, roomD) * 1.25
    camera.position.set(dist * 0.62, dist * 0.78, dist * 0.86)
    // Aim at the middle of the deck plate, not the world origin: with a corner
    // office the desk grid is pushed left, and looking at 0,0,0 cropped it.
    camera.lookAt(0, 0.6, 0.4)
    camera.updateProjectionMatrix()
    camera.updateMatrixWorld()
    // Re-apply whatever the user had panned/zoomed to, so a resize or a new
    // agent does not yank the view back to the default framing.
    controls?.apply()

    // Rebuild the shell at the new size.
    room?.dispose()
    if (room) worldRoot.remove(room.group)
    room = buildRoom(materials, palette, roomW, roomD)
    worldRoot.add(room.group)

    if (props) worldRoot.remove(props)
    props = buildProps(materials, roomW, roomD)
    worldRoot.add(props)

    // --- zones -------------------------------------------------------------
    // Rebuilt with the room so the plan scales with the desk count rather than
    // tiling into wallpaper.
    zones?.dispose()
    if (zones) worldRoot.remove(zones.group)

    const zoneGroup = new THREE.Group()
    const zoneDisposers: Array<() => void> = []

    const sideX = roomW / 2 - SIDE_ZONE_W / 2 - 0.4
    const openRight = roomW / 2 - SIDE_ZONE_W

    // Corridor: a runner down the aisle between the open plan and the zones.
    const corridor = buildCorridor(materials, 2.4, roomD - 2)
    corridor.position.set(openRight - 1.4, 0, 0)
    zoneGroup.add(corridor)

    // Meeting room, upper right.
    const meetingD = Math.min(7.4, Math.max(5.6, roomD * 0.42))
    const meeting = buildMeetingRoom(materials, palette, SIDE_ZONE_W - 1.2, meetingD)
    meeting.group.position.set(sideX, 0, -roomD / 2 + meetingD / 2 + 1.4)
    zoneGroup.add(meeting.group)
    zoneDisposers.push(() => meeting.dispose())

    // Break area, lower right.
    const brk = buildBreakArea(materials, palette)
    brk.group.position.set(sideX, 0, roomD / 2 - 2.6)
    zoneGroup.add(brk.group)
    zoneDisposers.push(() => brk.dispose())

    // Reception, at the front by the entrance.
    const reception = buildReception(materials, palette)
    reception.group.position.set(-roomW / 2 + 3.2, 0, roomD / 2 - 2.0)
    reception.group.rotation.y = -0.35
    zoneGroup.add(reception.group)
    zoneDisposers.push(() => reception.dispose())

    zoneGroup.add(buildCeilingLights(materials, roomW, roomD))

    zones = {
      group: zoneGroup,
      dispose: () => { for (const d of zoneDisposers) d() },
    }
    worldRoot.add(zoneGroup)

    // Arrivals come in past reception, not through a wall.
    doorPos = { x: -roomW / 2 + 1.4, z: roomD / 2 - 1.2 }

    const seated = cornerOffice || !bossId ? workers : [bossId, ...workers]
    const gridW = cols * CELL_W
    // The open plan occupies everything left of the boss strip. Centre the grid
    // inside THAT span, not inside the whole room, or the desks drift left and
    // leave the right third of the deck empty.
    // The open plan is everything left of the side zone, and behind reception.
    const openW = roomW - SIDE_ZONE_W - (cornerOffice ? CELL_W + 1.2 : 0)
    const openCentre = -roomW / 2 + openW / 2
    const startX = openCentre - gridW / 2 + CELL_W / 2
    const startZ = -(rows * CELL_D) / 2 + CELL_D / 2 + 1.0 - RECEPTION_D / 2

    seated.forEach((id, i) => {
      const a = agents.get(id)
      if (!a) return
      placeCell(a, startX + (i % cols) * CELL_W, startZ + Math.floor(i / cols) * CELL_D)
    })

    if (cornerOffice && bossId) {
      const a = agents.get(bossId)
      if (a) placeCell(a, roomW / 2 - (CELL_W + 1.2) / 2 - 0.3, startZ + Math.max(0, (rows - 1) / 2) * CELL_D)
    }

    emitSlots()
  }

  function placeCell(a: AgentNode, cx: number, cz: number) {
    const jumped = a.cell.x !== cx || a.cell.z !== cz
    a.cell = { x: cx, z: cz }
    // The chair sits behind the desk; standing is beside it.
    a.seat = { x: cx, z: cz + 0.72 }
    a.stand = { x: cx + 1.15, z: cz + 0.5 }

    a.desk.position.set(cx, 0, cz)
    a.cubicle.position.set(cx, 0, cz)
    a.chair.position.set(cx, 0, cz + 0.78)
    a.lamp.group.position.set(cx + 0.72, 0.86, cz - 0.1)
    a.bar.group.position.set(cx, 0.79, cz + 0.5)

    const target = seatedPose(a.status) ? a.seat : a.stand
    if (!a.placed) {
      // First appearance: walk in from the airlock rather than popping into the
      // chair. Arriving is half of what makes the deck read as a place.
      a.placed = true
      a.pos = { ...doorPos }
      walkTo(a, target)
    } else if (jumped) {
      // A resize is not a hand-off, so relayout teleports.
      a.pos = { ...target }
      a.path = []
    }
  }

  /**
   * Project a world point to CSS pixels inside the host. This is what keeps the
   * DOM overlay (nameplates, bubbles, hit targets) glued to the 3D desks.
   */
  const projected = new THREE.Vector3()
  function toScreen(x: number, y: number, z: number): { x: number; y: number } {
    projected.set(x, y, z).project(camera)
    const cw = renderer.domElement.clientWidth || Math.max(host.clientWidth, 240)
    return {
      x: (projected.x * 0.5 + 0.5) * cw,
      y: (-projected.y * 0.5 + 0.5) * contentHeight,
    }
  }

  function emitSlots() {
    const slots: FloorSlot[] = []
    for (const id of order) {
      const a = agents.get(id)
      if (!a || a.cell.x === -999) continue

      const centre = toScreen(a.cell.x, 0.78, a.cell.z)
      const head = toScreen(a.cell.x, 2.05, a.cell.z + 0.5)
      const plate = toScreen(a.cell.x, 0.12, a.cell.z + 1.05)
      const top = toScreen(a.cell.x, 2.5, a.cell.z - 1.0)
      // A cell's on-screen width, measured rather than assumed, so the hit
      // target tracks the camera framing instead of a magic number.
      const edge = toScreen(a.cell.x + CELL_W / 2, 0.78, a.cell.z)

      slots.push({
        id: a.id,
        x: centre.x,
        y: centre.y,
        w: Math.max(48, Math.abs(edge.x - centre.x) * 2),
        h: Math.max(60, Math.abs(plate.y - top.y)),
        headY: head.y,
        plateY: plate.y,
        top: top.y,
      })
    }

    const signature = slots.map(s => `${s.id}:${Math.round(s.x)}:${Math.round(s.y)}`).join('|') + `@${contentHeight}`
    if (signature !== lastLayoutSignature) {
      lastLayoutSignature = signature
      opts.onLayout(slots, contentHeight)
    }
  }

  function seatedPose(s: FieldAgent['status']) {
    return s === 'working' || s === 'done'
  }

  // --- agent lifecycle ------------------------------------------------------
  function createAgent(a: FieldAgent): AgentNode {
    const look = lookFor(a.id, a.accent)
    const accent = new THREE.Color(a.accent || look.shirt)

    const root = new THREE.Group()
    const crew = buildCrew(look)
    const chair = buildChair(materials)
    const { group: desk, screen, stripMat } = buildDesk(materials, accent, !!a.boss)
    // Dividers, so the open plan reads as workstations rather than desks in a
    // field. The boss's corner office gets none — that is the point of it.
    const cube = buildCubicle(materials, palette)
    const lamp = buildLamp(LAMP_COLORS.idle)
    const bar = buildProgressBar(accent, materials)
    bar.group.visible = false
    // The strip is the desk's own accent; it never animates, so keep the
    // material with the node only so teardown can dispose it.
    void stripMat

    // Blob shadow under the feet. A real shadow map gives a figure this size
    // about four grey pixels; this is what actually plants them on the deck.
    const shadow = new THREE.Mesh(contact.geometry, contact.material)
    shadow.rotation.x = -Math.PI / 2
    shadow.position.y = 0.008
    shadow.scale.setScalar(1.15)
    shadow.renderOrder = 1

    root.add(crew.group, shadow)
    if (!a.boss) deskLayer.add(cube.group)
    deskLayer.add(root, chair, desk, lamp.group, bar.group)

    return {
      id: a.id,
      look,
      root,
      crew,
      shadow,
      chair,
      desk,
      cubicle: cube.group,
      screen,
      lamp,
      bar,
      accent,
      cell: { x: -999, z: -999 },
      seat: { ...doorPos },
      stand: { ...doorPos },
      pos: { ...doorPos },
      path: [],
      walkSpeed: 0.004,
      facing: 0,
      pose: 'stand',
      drawnPose: null,
      status: a.status,
      progress: -1,
      boss: !!a.boss,
      placed: false,
      statusClock: 0,
      frameClock: 0,
      bob: 0,
    }
  }

  function destroyAgent(a: AgentNode) {
    deskLayer.remove(a.root, a.chair, a.desk, a.cubicle, a.lamp.group, a.bar.group)
    a.crew.dispose()
    a.lamp.mat.dispose()
    a.bar.fillMat.dispose()
    if (a.screen.material instanceof THREE.Material) a.screen.material.dispose()
  }

  // Every walk takes about the same time regardless of distance. At a fixed
  // speed, crossing a wide deck took the best part of ten seconds and the room
  // looked deserted for the first few seconds after it opened.
  const WALK_MS = 1100

  function walkTo(a: AgentNode, target: Pt) {
    // L-shaped route: slide along the aisle, then step in to the desk.
    const corner = { x: target.x, z: a.pos.z }
    a.path = [corner, { ...target }]
    const distance = Math.abs(corner.x - a.pos.x) + Math.abs(target.z - corner.z)
    a.walkSpeed = Math.min(0.02, Math.max(0.002, distance / WALK_MS))
  }

  function setLamp(a: AgentNode, status: FieldAgent['status']) {
    const color = LAMP_COLORS[status] ?? LAMP_COLORS.idle
    a.lamp.mat.color.setHex(color)
    a.lamp.light.color.setHex(color)

    // The suit carries the same signal as the desk lamp. At the distance the
    // deck is viewed from, the visor and antenna tip are far easier to read
    // than a lamp behind a monitor.
    const suit = a.crew.suit
    if (suit) {
      suit.visorMat.emissive.setHex(color)
      suit.tipMat.color.setHex(color)
      suit.tipLight.color.setHex(color)
    }
  }

  // --- public update --------------------------------------------------------
  function update(model: FieldModel) {
    running = !!model.running
    const next = model.agents
    const nextIds = new Set(next.map(a => a.id))

    let structural = false

    for (const [id, a] of agents) {
      if (!nextIds.has(id)) {
        destroyAgent(a)
        agents.delete(id)
        structural = true
      }
    }

    for (const a of next) {
      let s = agents.get(a.id)
      if (!s) {
        s = createAgent(a)
        agents.set(a.id, s)
        structural = true
      }
      if (s.boss !== !!a.boss) {
        s.boss = !!a.boss
        structural = true
      }

      const wasSeated = seatedPose(s.status)
      const nowSeated = seatedPose(a.status)
      if (s.status !== a.status) s.statusClock = 0
      s.status = a.status
      s.progress = typeof a.progress === 'number' ? Math.max(0, Math.min(1, a.progress)) : -1
      setLamp(s, a.status)

      s.bar.group.visible = s.progress >= 0 && (a.status === 'working' || a.status === 'blocked')
      s.bar.fill.scale.x = s.progress >= 0 ? Math.max(0.001, s.progress) : 0.001
      // Scaling from the centre would grow both ways; shift so it fills left to right.
      s.bar.fill.position.x = -0.5 + (s.progress >= 0 ? s.progress : 0) * 0.5

      // A screen only glows while the desk is actually hot.
      if (s.screen.material instanceof THREE.MeshStandardMaterial) {
        s.screen.material.emissiveIntensity =
          a.status === 'working' ? 0.95 : a.status === 'blocked' ? 0.5 : a.status === 'failed' ? 0.2 : 0.35
      }

      if (s.cell.x !== -999 && wasSeated !== nowSeated) {
        walkTo(s, nowSeated ? s.seat : s.stand)
      }
    }

    const newOrder = next.map(a => a.id)
    if (structural || newOrder.join('|') !== order.join('|')) {
      order = newOrder
      relayout()
    }

    for (const m of model.messages || []) {
      if (seenMessages.has(m.id)) continue
      seenMessages.add(m.id)
      launch(m.from, m.to)
    }
    if (seenMessages.size > 400) {
      const keep = new Set((model.messages || []).map(m => m.id))
      seenMessages.forEach(id => { if (!keep.has(id)) seenMessages.delete(id) })
    }
  }

  function anchorOf(ref: string): THREE.Vector3 | null {
    if (ref === 'user' || ref === 'inbox') return new THREE.Vector3(doorPos.x, 1.2, doorPos.z)
    const a = agents.get(ref)
    if (!a) return null
    return new THREE.Vector3(a.pos.x, 1.15, a.pos.z)
  }

  function launch(from: string, to: string) {
    const a = anchorOf(from)
    const b = anchorOf(to)
    if (!a || !b) return
    const mesh = buildEnvelope(materials)
    mesh.position.copy(a)
    flightLayer.add(mesh)
    flights.push({ mesh, from: a, to: b, t: 0, dur: 900 })
  }

  // --- render loop ----------------------------------------------------------
  let destroyed = false
  let raf = 0
  let last = performance.now()
  let sinceSlots = 0

  function frame(now: number) {
    if (destroyed) return
    raf = requestAnimationFrame(frame)

    const dt = Math.min(now - last, 64)
    last = now

    // An idle deck still breathes, but it does not need every frame to do it.
    if (!running && dt < 28) return

    for (const a of agents.values()) {
      a.statusClock += dt
      a.frameClock += dt

      if (a.path.length) {
        const target = a.path[0]
        const dx = target.x - a.pos.x
        const dz = target.z - a.pos.z
        const dist = Math.hypot(dx, dz)
        const stepLen = a.walkSpeed * dt
        if (dist <= stepLen) {
          a.pos = { ...target }
          a.path.shift()
        } else {
          a.pos.x += (dx / dist) * stepLen
          a.pos.z += (dz / dist) * stepLen
          a.facing = Math.atan2(dx, dz)
        }
        a.pose = Math.floor(a.frameClock / 140) % 2 === 0 ? 'stand' : 'step'
      } else if (a.status === 'working') {
        a.pose = Math.floor(a.frameClock / 180) % 2 === 0 ? 'sit-type' : 'sit'
        a.facing = Math.PI
      } else if (a.status === 'done') {
        // Hand up the moment the work lands, waving for a few seconds so it
        // catches the eye, then held still so a room of finished desks does not
        // turn into a sea of movement.
        a.pose = a.statusClock < 4000 && Math.floor(a.statusClock / 380) % 2 === 1 ? 'sit-done2' : 'sit-done'
        a.facing = Math.PI
      } else if (seatedPose(a.status)) {
        a.pose = 'sit'
        a.facing = Math.PI
      } else if (a.status === 'blocked') {
        a.pose = Math.floor(a.frameClock / 320) % 2 === 0 ? 'stand' : 'step'
      } else {
        a.pose = 'stand'
      }

      if (a.pose !== a.drawnPose) {
        a.crew.setPose(a.pose)
        a.drawnPose = a.pose
      }

      // idle breathing / anxious jiggle so the deck never looks frozen
      a.bob += dt
      const amp = a.status === 'blocked' ? 0.05 : a.status === 'working' ? 0.02 : 0.012
      const off = Math.sin(a.bob / (a.status === 'blocked' ? 120 : 700)) * amp

      a.root.position.set(a.pos.x, off, a.pos.z)
      a.root.rotation.y = a.facing

      // the chair only belongs behind a seated figure
      a.chair.visible = a.path.length === 0 && seatedPose(a.status)

      // lamp pulses while the desk is hot
      const hot = a.status === 'working' || a.status === 'blocked'
      const pulse = hot ? 0.55 + 0.45 * Math.abs(Math.sin(a.bob / 260)) : a.status === 'idle' ? 0.35 : 0.9
      a.lamp.mat.opacity = pulse
      a.lamp.light.intensity = pulse * 1.6

      if (a.crew.suit) {
        a.crew.suit.visorMat.emissiveIntensity = 0.35 + pulse * 0.5
        a.crew.suit.tipLight.intensity = pulse * 0.7
      }
    }

    // The selection ring sits under the chosen desk and pulses gently, so the
    // eye finds it without it shouting. The desk lifts a little too, which is
    // what actually reads as "this one" at an isometric angle.
    if (selectedId) {
      const sel = agents.get(selectedId)
      if (sel && sel.cell.x !== -999) {
        selection.ring.visible = true
        selection.ring.position.set(sel.cell.x, 0.012, sel.cell.z + 0.35)
        const t = performance.now() / 520
        selection.mat.opacity = 0.55 + 0.35 * Math.abs(Math.sin(t))
        const s2 = 1 + 0.03 * Math.sin(t)
        selection.ring.scale.set(s2, s2, s2)
      } else {
        selection.ring.visible = false
      }
    }

    // Desks ease toward their lift target rather than snapping, so selecting
    // one does not look like a jump cut.
    for (const a of agents.values()) {
      const want = a.id === selectedId ? 0.16 : 0
      const cur = a.desk.position.y
      if (Math.abs(cur - want) > 0.001) {
        a.desk.position.y = cur + (want - cur) * Math.min(1, dt / 120)
        a.lamp.group.position.y = 0.86 + a.desk.position.y
        a.bar.group.position.y = 0.79 + a.desk.position.y
      }
    }

    // envelopes arc between desks
    for (let i = flights.length - 1; i >= 0; i--) {
      const f = flights[i]
      f.t += dt
      const p = Math.min(1, f.t / f.dur)
      const ease = p < 0.5 ? 2 * p * p : 1 - Math.pow(-2 * p + 2, 2) / 2
      f.mesh.position.lerpVectors(f.from, f.to, ease)
      f.mesh.position.y += Math.sin(p * Math.PI) * 1.6
      f.mesh.lookAt(camera.position)
      const mat = f.mesh.material as THREE.MeshBasicMaterial
      mat.transparent = true
      mat.opacity = p > 0.85 ? 1 - (p - 0.85) / 0.15 : 1
      if (p >= 1) {
        flightLayer.remove(f.mesh)
        flights.splice(i, 1)
      }
    }

    // Walking changes where a nameplate should sit, so re-project periodically
    // rather than every frame — the overlay is DOM and cheap to leave alone.
    sinceSlots += dt
    if (sinceSlots > 200) {
      sinceSlots = 0
      if (flights.length || [...agents.values()].some(a => a.path.length)) emitSlots()
    }

    post.composer.render()
  }

  raf = requestAnimationFrame(frame)

  // --- resize ---------------------------------------------------------------
  const ro = new ResizeObserver(() => relayout())
  ro.observe(host)

  relayout()

  return {
    update,
    setTheme(nextDark: boolean) {
      if (nextDark === dark) return
      dark = nextDark
      palette = PALETTES[dark ? 'dark' : 'light']

      const old = materials
      materials = makeMaterials(palette)

      scene.fog = new THREE.Fog(palette.fog, dark ? 55 : 100, dark ? 150 : 260)

      // Regenerating the sky also regenerates the environment map, so the
      // whole scene's ambient character follows the theme rather than just
      // its light levels.
      sky.setPalette(SKIES[dark ? 'dark' : 'light'])
      scene.environment = sky.envTexture
      scene.environmentIntensity = dark ? 1.5 : 1.35

      ambient.intensity = dark ? 0.55 : 0.4
      hemi.color.setHex(dark ? 0x8899cc : 0xbfd4f2)
      hemi.groundColor.setHex(dark ? 0x4a4038 : 0x9a8f7a)
      hemi.intensity = dark ? 0.95 : 0.85
      key.intensity = dark ? 2.1 : 1.9
      rim.color.setHex(dark ? 0x8fa8d8 : 0xbcd0ee)
      rim.intensity = dark ? 0.55 : 0.3
      renderer.toneMappingExposure = dark ? 1.25 : 0.95

      scene.remove(stars.points)
      stars.dispose()
      stars = buildStars(palette, 70)
      scene.add(stars.points)

      // Furniture holds direct references to the old material set, so re-point
      // each mesh at the new one. Rebuilding the agents instead would drop
      // every walk in progress and empty the deck for a frame.
      for (const a of agents.values()) {
        a.chair.traverse(o => {
          if (o instanceof THREE.Mesh) {
            o.material = o.material === old.metal ? materials.metal : materials.chair
          }
        })
        a.desk.traverse(o => {
          if (!(o instanceof THREE.Mesh)) return
          if (o.material === old.deskTop) o.material = materials.deskTop
          else if (o.material === old.desk) o.material = materials.desk
          else if (o.material === old.metal) o.material = materials.metal
          // The screen and accent strip are per-agent clones; leave them.
        })
        a.bar.group.traverse(o => {
          if (o instanceof THREE.Mesh && o.material === old.barTrack) o.material = materials.barTrack
        })
      }

      old.dispose()
      lastLayoutSignature = ''
      relayout()
    },
    resize: relayout,
    setSelected(id: string | null) {
      selectedId = id
      if (!id) selection.ring.visible = false
    },
    destroy() {
      destroyed = true
      cancelAnimationFrame(raf)
      ro.disconnect()
      controls.dispose()

      for (const a of agents.values()) destroyAgent(a)
      agents.clear()

      for (const f of flights) flightLayer.remove(f.mesh)
      flights.length = 0

      room?.dispose()
      zones?.dispose()
      sky.dispose()
      contact.dispose()
      stars.dispose()
      selection.mat.dispose()
      materials.dispose()
      post.dispose()
      renderer.dispose()
      renderer.domElement.remove()
    },
  }
}
