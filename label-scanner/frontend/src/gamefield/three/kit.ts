// Geometry kit for the three.js floor: the station deck, the furniture and the
// props that dress it. Everything here is built from primitives at module scope
// where it can be shared, and cloned per desk — a room of forty desks is forty
// clones of five geometries, not two hundred fresh buffers.
//
// World units are metres-ish: a desk is ~1.6 wide, a person ~1.7 tall. The
// camera frames whatever the layout asks for, so nothing here cares about
// pixels.

import * as THREE from 'three'

/** One workstation's footprint, in world units. Layout maths keys off these. */
export const CELL_W = 3.2
export const CELL_D = 3.6

/** Palette, split by theme. Emissive values are what bloom picks up. */
export interface Palette {
  deck: number
  deckLine: number
  wall: number
  wallTrim: number
  panelGlow: number
  desk: number
  deskTop: number
  chair: number
  metal: number
  space: number
  fog: number
  star: number
  /** Corridor carpet — a run of a different colour is what makes a floor plan read. */
  corridor: number
  /** Cubicle panel fabric. */
  partition: number
  /** Meeting-room glazing. */
  glass: number
  /** Warm wood for counters and tables. */
  wood: number
  /** Sofa / soft furnishing. */
  fabric: number
}

export const PALETTES: Record<'dark' | 'light', Palette> = {
  dark: {
    deck: 0x3d465c,
    deckLine: 0x6b7a9c,
    wall: 0x333a4e,
    wallTrim: 0x55628a,
    panelGlow: 0x4fc3f7,
    desk: 0x4d5670,
    deskTop: 0x636d8a,
    chair: 0x424a60,
    metal: 0x8d97ad,
    space: 0x0a0d16,
    fog: 0x1b2130,
    star: 0xffffff,
    corridor: 0x323a4e,
    partition: 0x5a6580,
    glass: 0x8fd3ff,
    wood: 0x7a6248,
    fabric: 0x4a5570,
  },
  light: {
    deck: 0xc9c3b4,
    deckLine: 0x9c927c,
    wall: 0xdad4c6,
    wallTrim: 0xa89e88,
    panelGlow: 0x59b0e8,
    desk: 0xa89a80,
    deskTop: 0xbfb49b,
    chair: 0x857c69,
    metal: 0x8f8779,
    space: 0xdfe6f2,
    fog: 0xd5dae6,
    star: 0x9fb0c8,
    corridor: 0xb8b0a0,
    partition: 0x9aa3b5,
    glass: 0xa8d8f0,
    wood: 0xa8845c,
    fabric: 0x8d94a8,
  },
}

// ── shared geometry ─────────────────────────────────────────────────────────
// Created once. Disposed by disposeKit() when the last floor goes away.

const G = {
  deskTop: new THREE.BoxGeometry(1.9, 0.08, 0.95),
  deskLeg: new THREE.BoxGeometry(0.1, 0.7, 0.1),
  monitor: new THREE.BoxGeometry(0.9, 0.55, 0.05),
  monitorStand: new THREE.CylinderGeometry(0.05, 0.12, 0.22, 10),
  chairSeat: new THREE.BoxGeometry(0.62, 0.09, 0.6),
  chairBack: new THREE.BoxGeometry(0.62, 0.7, 0.09),
  chairPost: new THREE.CylinderGeometry(0.06, 0.06, 0.34, 8),
  chairBase: new THREE.CylinderGeometry(0.32, 0.32, 0.05, 12),
  lamp: new THREE.SphereGeometry(0.11, 14, 12),
  envelope: new THREE.PlaneGeometry(0.34, 0.24),
  barTrack: new THREE.BoxGeometry(1.0, 0.045, 0.02),
  plantPot: new THREE.CylinderGeometry(0.2, 0.26, 0.34, 12),
  plantLeaf: new THREE.IcosahedronGeometry(0.34, 0),
  cooler: new THREE.BoxGeometry(0.42, 1.1, 0.42),
  coolerTank: new THREE.CylinderGeometry(0.17, 0.17, 0.42, 12),
  window: new THREE.PlaneGeometry(2.4, 1.3),
  panelStrip: new THREE.PlaneGeometry(0.5, 0.06),
  // Selection ring: flat on the deck, so it takes the floor's perspective for
  // free instead of fighting it the way a screen-space box does.
  selectRing: new THREE.RingGeometry(1.05, 1.28, 48),

  // --- office fittings ---
  cubiclePanel: new THREE.BoxGeometry(0.06, 1.15, 1.9),
  cubicleCap: new THREE.BoxGeometry(0.09, 0.05, 1.9),
  glassPane: new THREE.BoxGeometry(0.05, 2.2, 1),
  meetingTable: new THREE.CylinderGeometry(1.15, 1.15, 0.09, 24),
  meetingLeg: new THREE.CylinderGeometry(0.12, 0.22, 0.66, 12),
  counter: new THREE.BoxGeometry(3.2, 0.9, 0.7),
  counterTop: new THREE.BoxGeometry(3.3, 0.08, 0.78),
  sofaBase: new THREE.BoxGeometry(2.1, 0.42, 0.8),
  sofaBack: new THREE.BoxGeometry(2.1, 0.55, 0.18),
  sofaArm: new THREE.BoxGeometry(0.18, 0.3, 0.8),
  coffeeTable: new THREE.BoxGeometry(1.0, 0.07, 0.6),
  cabinet: new THREE.BoxGeometry(0.5, 1.05, 0.55),
  cabinetHandle: new THREE.BoxGeometry(0.26, 0.04, 0.03),
  whiteboard: new THREE.BoxGeometry(2.4, 1.25, 0.06),
  ceilingLight: new THREE.BoxGeometry(1.8, 0.06, 0.28),
  mug: new THREE.CylinderGeometry(0.07, 0.06, 0.11, 8),
  paper: new THREE.BoxGeometry(0.28, 0.01, 0.36),
  rug: new THREE.PlaneGeometry(1, 1),
}

export function disposeKit() {
  for (const g of Object.values(G)) g.dispose()
}

// ── materials ───────────────────────────────────────────────────────────────

export interface Materials {
  partition: THREE.MeshStandardMaterial
  glass: THREE.MeshStandardMaterial
  wood: THREE.MeshStandardMaterial
  fabric: THREE.MeshStandardMaterial
  corridor: THREE.MeshStandardMaterial
  paper: THREE.MeshStandardMaterial
  ceilingLight: THREE.MeshBasicMaterial
  deck: THREE.MeshStandardMaterial
  deckLine: THREE.MeshBasicMaterial
  wall: THREE.MeshStandardMaterial
  trim: THREE.MeshStandardMaterial
  desk: THREE.MeshStandardMaterial
  deskTop: THREE.MeshStandardMaterial
  chair: THREE.MeshStandardMaterial
  metal: THREE.MeshStandardMaterial
  screen: THREE.MeshStandardMaterial
  glow: THREE.MeshBasicMaterial
  envelope: THREE.MeshBasicMaterial
  barTrack: THREE.MeshBasicMaterial
  leaf: THREE.MeshStandardMaterial
  dispose(): void
}

export function makeMaterials(p: Palette): Materials {
  // envMapIntensity is the dial that decides how much of scene.environment a
  // surface picks up. Left at the default of 1 with high roughness, matte
  // materials swallow the environment and the IBL work is invisible; these are
  // tuned so every surface returns *some* of the sky.
  const std = (color: number, rough = 0.85, metal = 0.05) =>
    new THREE.MeshStandardMaterial({
      color,
      roughness: rough,
      metalness: metal,
      envMapIntensity: 1.15,
    })

  const mats = {
    deck: std(p.deck, 0.78, 0.08),
    deckLine: new THREE.MeshBasicMaterial({ color: p.deckLine, transparent: true, opacity: 0.5 }),
    wall: std(p.wall, 0.95, 0.02),
    trim: std(p.wallTrim, 0.7, 0.3),
    desk: std(p.desk, 0.8, 0.1),
    deskTop: std(p.deskTop, 0.42, 0.25),
    chair: std(p.chair, 0.85, 0.08),
    metal: std(p.metal, 0.28, 0.85),
    // Monitors read as "on" without needing a texture.
    screen: new THREE.MeshStandardMaterial({
      color: 0x0d1420,
      emissive: p.panelGlow,
      emissiveIntensity: 0.6,
      roughness: 0.3,
    }),
    glow: new THREE.MeshBasicMaterial({ color: p.panelGlow }),
    envelope: new THREE.MeshBasicMaterial({ color: 0xffe9a8, side: THREE.DoubleSide }),
    barTrack: new THREE.MeshBasicMaterial({ color: p.deckLine, transparent: true, opacity: 0.55 }),
    leaf: std(0x4f8f5e, 0.95, 0),
    partition: std(p.partition, 0.95, 0.02),
    // Glazing: transparent enough to see the room through, with a faint tint so
    // the pane still catches light and reads as glass rather than a gap.
    glass: new THREE.MeshStandardMaterial({
      color: p.glass,
      transparent: true,
      opacity: 0.18,
      roughness: 0.03,
      metalness: 0.2,
      // Glass with a strong environment response is the single most convincing
      // surface in the room — it is the one thing that obviously mirrors a sky.
      envMapIntensity: 2.2,
    }),
    wood: std(p.wood, 0.75, 0.05),
    fabric: std(p.fabric, 0.98, 0),
    corridor: new THREE.MeshStandardMaterial({ color: p.corridor, roughness: 0.95, metalness: 0.02 }),
    paper: std(0xf2efe6, 0.9, 0),
    // Ceiling panels explain where the light comes from. They must stay UNDER
    // the bloom threshold (0.82): at full white each panel became a flare that
    // washed out the desks below it, which is the opposite of lighting a room.
    ceilingLight: new THREE.MeshBasicMaterial({ color: 0xd8d2be }),
  }

  return {
    ...mats,
    dispose() {
      for (const m of Object.values(mats)) {
        if (m && typeof (m as THREE.Material).dispose === 'function') (m as THREE.Material).dispose()
      }
    },
  }
}

// ── furniture ───────────────────────────────────────────────────────────────

/**
 * A desk with monitor, stand and a progress track across the front edge.
 * Returns the group plus the handles the floor animates.
 */
export function buildDesk(m: Materials, accent: THREE.Color, boss: boolean) {
  const g = new THREE.Group()

  const top = new THREE.Mesh(G.deskTop, m.deskTop)
  top.position.y = 0.72
  top.castShadow = true
  top.receiveShadow = true
  if (boss) top.scale.set(1.15, 1, 1.1)
  g.add(top)

  // Four legs, inset from the corners.
  const lx = (boss ? 1.09 : 0.86)
  const lz = 0.38
  for (const [sx, sz] of [[-1, -1], [1, -1], [-1, 1], [1, 1]] as const) {
    const leg = new THREE.Mesh(G.deskLeg, m.desk)
    leg.position.set(sx * lx, 0.36, sz * lz)
    leg.castShadow = true
    g.add(leg)
  }

  const stand = new THREE.Mesh(G.monitorStand, m.metal)
  stand.position.set(0, 0.86, -0.22)
  g.add(stand)

  const screen = new THREE.Mesh(G.monitor, m.screen.clone())
  screen.position.set(0, 1.25, -0.24)
  screen.rotation.x = -0.12
  g.add(screen)

  // Accent strip along the desk's front edge — this is the per-agent colour.
  const stripMat = new THREE.MeshBasicMaterial({ color: accent })
  const strip = new THREE.Mesh(G.barTrack, stripMat)
  strip.position.set(0, 0.77, 0.47)
  strip.scale.x = boss ? 1.15 : 1
  g.add(strip)

  return { group: g, screen, stripMat }
}

export function buildChair(m: Materials) {
  const g = new THREE.Group()
  const seat = new THREE.Mesh(G.chairSeat, m.chair)
  seat.position.y = 0.46
  seat.castShadow = true
  g.add(seat)
  const back = new THREE.Mesh(G.chairBack, m.chair)
  back.position.set(0, 0.8, -0.27)
  back.castShadow = true
  g.add(back)
  const post = new THREE.Mesh(G.chairPost, m.metal)
  post.position.y = 0.24
  g.add(post)
  const base = new THREE.Mesh(G.chairBase, m.metal)
  base.position.y = 0.04
  g.add(base)
  return g
}

/**
 * The status lamp. An emissive sphere plus a real point light, so it both
 * blooms and throws a little colour onto the desk around it.
 */
export function buildLamp(color: number) {
  const g = new THREE.Group()
  const mat = new THREE.MeshBasicMaterial({ color })
  const bulb = new THREE.Mesh(G.lamp, mat)
  g.add(bulb)
  const light = new THREE.PointLight(color, 1.4, 3.2, 2)
  g.add(light)
  return { group: g, mat, light, bulb }
}

export function buildProgressBar(accent: THREE.Color, m: Materials) {
  const g = new THREE.Group()
  const track = new THREE.Mesh(G.barTrack, m.barTrack)
  g.add(track)
  const fillMat = new THREE.MeshBasicMaterial({ color: accent })
  const fill = new THREE.Mesh(G.barTrack, fillMat)
  fill.position.z = 0.006
  fill.scale.x = 0
  g.add(fill)
  return { group: g, fill, fillMat }
}

export function buildEnvelope(m: Materials) {
  return new THREE.Mesh(G.envelope, m.envelope)
}

// ── room shell ──────────────────────────────────────────────────────────────

/**
 * The deck plate, back wall, viewports and floor grid. Rebuilt whenever the
 * layout changes size, so the room is always exactly as big as the desks need.
 */
export function buildRoom(m: Materials, p: Palette, w: number, d: number) {
  const g = new THREE.Group()
  const disposables: (THREE.BufferGeometry | THREE.Material)[] = []

  // Deck
  const deckGeo = new THREE.BoxGeometry(w, 0.3, d)
  const deck = new THREE.Mesh(deckGeo, m.deck)
  deck.position.y = -0.15
  deck.receiveShadow = true
  g.add(deck)
  disposables.push(deckGeo)

  // Grid lines, drawn as a single LineSegments rather than N meshes.
  const pts: number[] = []
  const step = 1.6
  for (let x = -w / 2; x <= w / 2 + 0.001; x += step) pts.push(x, 0.002, -d / 2, x, 0.002, d / 2)
  for (let z = -d / 2; z <= d / 2 + 0.001; z += step) pts.push(-w / 2, 0.002, z, w / 2, 0.002, z)
  const gridGeo = new THREE.BufferGeometry()
  gridGeo.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3))
  const gridMat = new THREE.LineBasicMaterial({ color: p.deckLine, transparent: true, opacity: 0.28 })
  g.add(new THREE.LineSegments(gridGeo, gridMat))
  disposables.push(gridGeo, gridMat)

  // Back wall with a lit trim strip along the top.
  const wallGeo = new THREE.BoxGeometry(w, 2.6, 0.18)
  const wall = new THREE.Mesh(wallGeo, m.wall)
  wall.position.set(0, 1.3, -d / 2 - 0.09)
  wall.receiveShadow = true
  g.add(wall)
  disposables.push(wallGeo)

  const trimGeo = new THREE.BoxGeometry(w, 0.06, 0.06)
  const trimMat = new THREE.MeshBasicMaterial({ color: p.panelGlow })
  const trim = new THREE.Mesh(trimGeo, trimMat)
  trim.position.set(0, 2.62, -d / 2 - 0.02)
  g.add(trim)
  disposables.push(trimGeo, trimMat)

  // Viewports onto space, evenly spaced and never at the very edge.
  const winMat = new THREE.MeshBasicMaterial({ color: p.space })
  const frameMat = new THREE.MeshStandardMaterial({ color: p.wallTrim, roughness: 0.5, metalness: 0.4 })
  const count = Math.max(1, Math.floor((w - 2) / 3.6))
  const gap = w / (count + 1)
  for (let i = 1; i <= count; i++) {
    const x = -w / 2 + gap * i
    const win = new THREE.Mesh(G.window, winMat)
    win.position.set(x, 1.5, -d / 2 + 0.01)
    g.add(win)

    const frameGeo = new THREE.BoxGeometry(2.6, 1.5, 0.06)
    const frame = new THREE.Mesh(frameGeo, frameMat)
    frame.position.set(x, 1.5, -d / 2 - 0.02)
    g.add(frame)
    disposables.push(frameGeo)
  }
  disposables.push(winMat, frameMat)

  return {
    group: g,
    dispose() {
      for (const item of disposables) item.dispose()
    },
  }
}

/** Water cooler, plant and a couple of glowing wall panels. */
export function buildProps(m: Materials, w: number, d: number) {
  const g = new THREE.Group()

  const cooler = new THREE.Group()
  const body = new THREE.Mesh(G.cooler, m.metal)
  body.position.y = 0.55
  body.castShadow = true
  cooler.add(body)
  const tank = new THREE.Mesh(G.coolerTank, m.glow)
  tank.position.y = 1.3
  cooler.add(tank)
  cooler.position.set(-w / 2 + 0.8, 0, -d / 2 + 1.0)
  g.add(cooler)

  const plant = new THREE.Group()
  const pot = new THREE.Mesh(G.plantPot, m.desk)
  pot.position.y = 0.17
  pot.castShadow = true
  plant.add(pot)
  const leaf = new THREE.Mesh(G.plantLeaf, m.leaf)
  leaf.position.y = 0.55
  leaf.scale.set(1, 1.25, 1)
  leaf.castShadow = true
  plant.add(leaf)
  plant.position.set(w / 2 - 0.8, 0, d / 2 - 0.9)
  g.add(plant)

  return g
}

/** A field of stars behind the viewports. Points, so it costs one draw call. */
export function buildStars(p: Palette, radius: number) {
  const count = 700
  const pos = new Float32Array(count * 3)
  for (let i = 0; i < count; i++) {
    // Rejection-free spherical distribution, biased above the deck.
    const theta = Math.random() * Math.PI * 2
    const phi = Math.acos(2 * Math.random() - 1) * 0.7
    const r = radius * (0.75 + Math.random() * 0.25)
    pos[i * 3] = r * Math.sin(phi) * Math.cos(theta)
    pos[i * 3 + 1] = r * Math.cos(phi) * 0.6 + 6
    pos[i * 3 + 2] = r * Math.sin(phi) * Math.sin(theta)
  }
  const geo = new THREE.BufferGeometry()
  geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3))
  const mat = new THREE.PointsMaterial({ color: p.star, size: 0.12, sizeAttenuation: true, transparent: true, opacity: 0.9 })
  const points = new THREE.Points(geo, mat)
  return {
    points,
    dispose() {
      geo.dispose()
      mat.dispose()
    },
  }
}


/**
 * The selection marker: a ring laid on the deck under the chosen desk.
 *
 * Drawn in the scene rather than as a DOM outline so it sits in the same
 * perspective as everything else — a 2D rectangle over an isometric floor
 * never aligns with the desk it is marking and reads as a sticker.
 *
 * `depthWrite: false` keeps it from z-fighting the deck plate it lies on.
 */
export function buildSelectionRing(color: number) {
  const mat = new THREE.MeshBasicMaterial({
    color,
    transparent: true,
    opacity: 0.9,
    side: THREE.DoubleSide,
    depthWrite: false,
  })
  const ring = new THREE.Mesh(G.selectRing, mat)
  ring.rotation.x = -Math.PI / 2
  ring.position.y = 0.012
  ring.visible = false
  ring.renderOrder = 2
  return { ring, mat }
}


// ── office fittings ─────────────────────────────────────────────────────────
// What turns a room with desks in it into an office: dividers between the
// workstations, a room you can hold a meeting in, somewhere to make coffee, and
// a door you arrive through. Each returns a group the floor positions.

/**
 * Cubicle dividers for one workstation: a panel on the left and one behind,
 * each capped in a lighter trim. Two panels rather than four — a full box would
 * hide the person sitting in it, and the point is to see them working.
 */
export function buildCubicle(m: Materials, p: Palette) {
  const g = new THREE.Group()
  const capMat = new THREE.MeshStandardMaterial({ color: p.wallTrim, roughness: 0.6, metalness: 0.25 })

  const side = new THREE.Mesh(G.cubiclePanel, m.partition)
  side.position.set(-1.15, 0.575, 0.1)
  side.castShadow = true
  side.receiveShadow = true
  g.add(side)

  const sideCap = new THREE.Mesh(G.cubicleCap, capMat)
  sideCap.position.set(-1.15, 1.17, 0.1)
  g.add(sideCap)

  const back = new THREE.Mesh(G.cubiclePanel, m.partition)
  back.rotation.y = Math.PI / 2
  back.position.set(0, 0.575, -0.85)
  back.castShadow = true
  back.receiveShadow = true
  g.add(back)

  const backCap = new THREE.Mesh(G.cubicleCap, capMat)
  backCap.rotation.y = Math.PI / 2
  backCap.position.set(0, 1.17, -0.85)
  g.add(backCap)

  return { group: g, dispose: () => capMat.dispose() }
}

/**
 * A glass-walled meeting room: table, chairs around it, a whiteboard on the
 * back wall. Glazing on two sides only, so the interior stays visible from the
 * camera's fixed angle.
 */
export function buildMeetingRoom(m: Materials, p: Palette, w: number, d: number) {
  const g = new THREE.Group()
  const disposables: (THREE.BufferGeometry | THREE.Material)[] = []

  // Floor plate, so the room reads as its own space rather than open deck.
  const floorGeo = new THREE.BoxGeometry(w, 0.04, d)
  const floor = new THREE.Mesh(floorGeo, m.corridor)
  floor.position.y = 0.02
  floor.receiveShadow = true
  g.add(floor)
  disposables.push(floorGeo)

  // Glazing: front and one side. Panes are separate meshes with mullions
  // between them, which is what makes it read as a partition wall.
  const mullion = new THREE.MeshStandardMaterial({ color: p.wallTrim, roughness: 0.4, metalness: 0.6 })
  const panes = Math.max(2, Math.round(w / 1.2))
  for (let i = 0; i < panes; i++) {
    const x = -w / 2 + (w / panes) * (i + 0.5)
    const pane = new THREE.Mesh(G.glassPane, m.glass)
    pane.scale.z = w / panes / 1.0
    pane.rotation.y = Math.PI / 2
    pane.position.set(x, 1.1, d / 2)
    g.add(pane)

    const postGeo = new THREE.BoxGeometry(0.07, 2.2, 0.07)
    const post = new THREE.Mesh(postGeo, mullion)
    post.position.set(x - w / panes / 2, 1.1, d / 2)
    g.add(post)
    disposables.push(postGeo)
  }
  disposables.push(mullion)

  const sidePanes = Math.max(2, Math.round(d / 1.2))
  for (let i = 0; i < sidePanes; i++) {
    const z = -d / 2 + (d / sidePanes) * (i + 0.5)
    const pane = new THREE.Mesh(G.glassPane, m.glass)
    pane.scale.z = d / sidePanes / 1.0
    pane.position.set(w / 2, 1.1, z)
    g.add(pane)
  }

  // Header beam along the top of the glazing.
  const beamGeo = new THREE.BoxGeometry(w + 0.1, 0.12, 0.12)
  const beam = new THREE.Mesh(beamGeo, m.trim)
  beam.position.set(0, 2.25, d / 2)
  g.add(beam)
  disposables.push(beamGeo)

  // Table and chairs.
  const table = new THREE.Mesh(G.meetingTable, m.wood)
  table.position.y = 0.72
  table.castShadow = true
  g.add(table)

  const leg = new THREE.Mesh(G.meetingLeg, m.metal)
  leg.position.y = 0.35
  g.add(leg)

  for (let i = 0; i < 6; i++) {
    const a = (i / 6) * Math.PI * 2
    const chair = buildChair(m)
    chair.position.set(Math.cos(a) * 1.75, 0, Math.sin(a) * 1.75)
    chair.rotation.y = -a + Math.PI / 2
    chair.scale.setScalar(0.9)
    g.add(chair)
  }

  // Whiteboard on the closed back wall.
  const board = new THREE.Mesh(G.whiteboard, m.paper)
  board.position.set(0, 1.45, -d / 2 + 0.06)
  g.add(board)

  const frameGeo = new THREE.BoxGeometry(2.5, 1.35, 0.04)
  const frame = new THREE.Mesh(frameGeo, m.trim)
  frame.position.set(0, 1.45, -d / 2 + 0.02)
  g.add(frame)
  disposables.push(frameGeo)

  return {
    group: g,
    dispose() { for (const item of disposables) item.dispose() },
  }
}

/** Break area: kitchen counter, sofa, coffee table, a couple of mugs. */
export function buildBreakArea(m: Materials, p: Palette) {
  const g = new THREE.Group()
  const disposables: (THREE.BufferGeometry | THREE.Material)[] = []

  const rugMat = new THREE.MeshStandardMaterial({ color: p.corridor, roughness: 0.98 })
  const rug = new THREE.Mesh(G.rug, rugMat)
  rug.rotation.x = -Math.PI / 2
  rug.position.y = 0.006
  rug.scale.set(4.4, 3.6, 1)
  g.add(rug)
  disposables.push(rugMat)

  const counter = new THREE.Mesh(G.counter, m.wood)
  counter.position.set(0, 0.45, -1.5)
  counter.castShadow = true
  counter.receiveShadow = true
  g.add(counter)

  const top = new THREE.Mesh(G.counterTop, m.deskTop)
  top.position.set(0, 0.94, -1.5)
  g.add(top)

  // Two mugs left on the counter — small, but it is the kind of detail that
  // makes a room look used rather than modelled.
  for (const dx of [-0.5, 0.3]) {
    const mug = new THREE.Mesh(G.mug, m.paper)
    mug.position.set(dx, 1.03, -1.42)
    g.add(mug)
  }

  const sofa = new THREE.Group()
  const base = new THREE.Mesh(G.sofaBase, m.fabric)
  base.position.y = 0.28
  base.castShadow = true
  sofa.add(base)
  const back = new THREE.Mesh(G.sofaBack, m.fabric)
  back.position.set(0, 0.62, -0.31)
  back.castShadow = true
  sofa.add(back)
  for (const dx of [-1.05, 1.05]) {
    const arm = new THREE.Mesh(G.sofaArm, m.fabric)
    arm.position.set(dx, 0.42, 0)
    sofa.add(arm)
  }
  sofa.position.set(0, 0, 1.3)
  sofa.rotation.y = Math.PI
  g.add(sofa)

  const coffee = new THREE.Mesh(G.coffeeTable, m.wood)
  coffee.position.set(0, 0.36, 0.2)
  coffee.castShadow = true
  g.add(coffee)

  const legGeo = new THREE.BoxGeometry(0.06, 0.36, 0.06)
  for (const [lx, lz] of [[-0.42, -0.22], [0.42, -0.22], [-0.42, 0.22], [0.42, 0.22]] as const) {
    const l = new THREE.Mesh(legGeo, m.metal)
    l.position.set(lx, 0.18, 0.2 + lz)
    g.add(l)
  }
  disposables.push(legGeo)

  return {
    group: g,
    dispose() { for (const item of disposables) item.dispose() },
  }
}

/** Reception: a curved-ish desk by the entry, with a plant and a filing bank. */
export function buildReception(m: Materials, p: Palette) {
  const g = new THREE.Group()
  const disposables: (THREE.BufferGeometry | THREE.Material)[] = []

  const deskGeo = new THREE.BoxGeometry(2.6, 1.02, 0.72)
  const desk = new THREE.Mesh(deskGeo, m.wood)
  desk.position.y = 0.51
  desk.castShadow = true
  desk.receiveShadow = true
  g.add(desk)
  disposables.push(deskGeo)

  const topGeo = new THREE.BoxGeometry(2.75, 0.08, 0.85)
  const top = new THREE.Mesh(topGeo, m.deskTop)
  top.position.y = 1.06
  g.add(top)
  disposables.push(topGeo)

  // Sign strip on the front, lit so it catches the eye at the entrance.
  const signGeo = new THREE.BoxGeometry(1.6, 0.16, 0.04)
  const signMat = new THREE.MeshBasicMaterial({ color: p.panelGlow })
  const sign = new THREE.Mesh(signGeo, signMat)
  sign.position.set(0, 0.72, 0.38)
  g.add(sign)
  disposables.push(signGeo, signMat)

  // Filing cabinets behind the desk.
  for (const dx of [-1.6, 1.6]) {
    const cab = new THREE.Mesh(G.cabinet, m.metal)
    cab.position.set(dx, 0.53, -0.5)
    cab.castShadow = true
    g.add(cab)
    for (const dy of [0.28, 0.75]) {
      const handle = new THREE.Mesh(G.cabinetHandle, m.trim)
      handle.position.set(dx, dy, -0.22)
      g.add(handle)
    }
  }

  return {
    group: g,
    dispose() { for (const item of disposables) item.dispose() },
  }
}

/**
 * A carpet runner marking the main corridor. Flat on the deck and a different
 * colour, which is what makes the layout read as a floor plan rather than a
 * scatter of furniture.
 */
export function buildCorridor(m: Materials, w: number, d: number) {
  const g = new THREE.Group()
  const run = new THREE.Mesh(G.rug, m.corridor)
  run.rotation.x = -Math.PI / 2
  run.position.set(0, 0.004, 0)
  run.scale.set(w, d, 1)
  run.receiveShadow = true
  g.add(run)
  return g
}

/** Ceiling light panels, floating above the deck to explain the lighting. */
export function buildCeilingLights(m: Materials, w: number, d: number) {
  const g = new THREE.Group()
  const cols = Math.max(1, Math.floor(w / 7))
  const rows = Math.max(1, Math.floor(d / 7))
  for (let i = 0; i < cols; i++) {
    for (let j = 0; j < rows; j++) {
      const panel = new THREE.Mesh(G.ceilingLight, m.ceilingLight)
      panel.position.set(
        -w / 2 + (w / cols) * (i + 0.5),
        3.4,
        -d / 2 + (d / rows) * (j + 0.5),
      )
      g.add(panel)
    }
  }
  return g
}
