// Procedural pixel-art. Every avatar, desk and prop on the floor is drawn into
// an offscreen canvas here. The 3D floor no longer draws these, but lookFor()
// still assigns each agent its stable skin/hair/shirt identity --
// no image assets ship with the app.

const SKIN = ['#f6d5b4', '#eabb92', '#d69a6c', '#b3714a', '#8a5232', '#5f3720']
const HAIR = ['#2b2118', '#4a3121', '#7b4a25', '#b5772f', '#d9b26a', '#8d8d95', '#3a3a44', '#6b2f2f']
const SHIRT = ['#e4573d', '#d97a2b', '#4a7fb5', '#3f8f6d', '#7b5aa6', '#b5445f', '#2f6f8f', '#8a7b3f']

/** Deterministic 32-bit hash so an agent id always yields the same character. */
export function hashId(id: string): number {
  let h = 2166136261
  for (let i = 0; i < id.length; i++) {
    h ^= id.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return h >>> 0
}

export interface AvatarLook {
  skin: string
  hair: string
  hairStyle: 0 | 1 | 2 | 3
  shirt: string
  glasses: boolean
  tie: boolean
}

export function lookFor(id: string, accent?: string): AvatarLook {
  const h = hashId(id)
  return {
    skin: SKIN[h % SKIN.length],
    hair: HAIR[(h >> 3) % HAIR.length],
    hairStyle: ((h >> 7) % 4) as 0 | 1 | 2 | 3,
    shirt: accent || SHIRT[(h >> 11) % SHIRT.length],
    glasses: ((h >> 17) & 3) === 0,
    tie: ((h >> 19) & 3) === 0,
  }
}

function makeCanvas(w: number, h: number): [HTMLCanvasElement, CanvasRenderingContext2D] {
  const c = document.createElement('canvas')
  c.width = w
  c.height = h
  const ctx = c.getContext('2d')!
  ctx.imageSmoothingEnabled = false
  return [c, ctx]
}

function px(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, fill: string) {
  ctx.fillStyle = fill
  ctx.fillRect(x, y, w, h)
}

/**
 * `sit-done` / `sit-done2` are the two frames of the finished-work hand raise --
 * the office equivalent of putting your hand up to say you have something.
 */
export type AvatarPose = 'stand' | 'step' | 'sit' | 'sit-type' | 'sit-done' | 'sit-done2'

/**
 * A 16x24 character. `pose` swaps the legs for the walk cycle or seats the
 * figure at a desk; `sit-type` puts its hands out on the keyboard.
 */
export function drawAvatar(look: AvatarLook, pose: AvatarPose): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(16, 24)
  const { skin, hair, hairStyle, shirt, glasses, tie } = look
  const raised = pose === 'sit-done' || pose === 'sit-done2'
  const seated = pose === 'sit' || pose === 'sit-type' || raised

  // long hair sits behind the head
  if (hairStyle === 2) px(ctx, 3, 5, 10, 9, hair)
  if (hairStyle === 3) px(ctx, 3, 5, 10, 6, hair)

  px(ctx, 4, 4, 8, 8, skin)
  px(ctx, 4, 3, 8, 3, hair)
  if (hairStyle === 0) px(ctx, 4, 6, 2, 2, hair)
  if (hairStyle === 1) {
    px(ctx, 4, 2, 8, 1, hair)
    px(ctx, 3, 4, 1, 4, hair)
    px(ctx, 12, 4, 1, 4, hair)
  }
  if (hairStyle === 3) px(ctx, 5, 2, 6, 1, hair)

  px(ctx, 6, 8, 1, 1, '#1c1917')
  px(ctx, 9, 8, 1, 1, '#1c1917')
  if (glasses) {
    px(ctx, 5, 7, 3, 3, 'rgba(28,25,23,0.75)')
    px(ctx, 8, 7, 3, 3, 'rgba(28,25,23,0.75)')
    px(ctx, 6, 8, 1, 1, '#e7e5e4')
    px(ctx, 9, 8, 1, 1, '#e7e5e4')
  }
  px(ctx, 7, 10, 2, 1, 'rgba(60,30,20,0.55)')

  px(ctx, 4, 12, 8, seated ? 7 : 6, shirt)
  px(ctx, 6, 12, 4, 1, 'rgba(255,255,255,0.35)')
  if (tie) px(ctx, 7, 13, 2, 4, '#a93727')

  if (pose === 'sit-type') {
    px(ctx, 2, 14, 2, 3, shirt)
    px(ctx, 12, 14, 2, 3, shirt)
    px(ctx, 2, 17, 2, 2, skin)
    px(ctx, 12, 17, 2, 2, skin)
  } else if (raised) {
    // left arm rests, right arm goes up; the two frames tilt the hand to wave
    px(ctx, 2, 13, 2, 5, shirt)
    px(ctx, 2, 18, 2, 2, skin)
    px(ctx, 12, 9, 2, 5, shirt)
    const handX = pose === 'sit-done' ? 12 : 13
    px(ctx, handX, 6, 2, 3, skin)
    px(ctx, handX - 1, 6, 1, 2, skin)
  } else {
    px(ctx, 2, 13, 2, 5, shirt)
    px(ctx, 12, 13, 2, 5, shirt)
    px(ctx, 2, 18, 2, 2, skin)
    px(ctx, 12, 18, 2, 2, skin)
  }

  if (seated) {
    px(ctx, 4, 19, 8, 3, '#3a3f4b')
  } else if (pose === 'step') {
    px(ctx, 4, 18, 3, 5, '#3a3f4b')
    px(ctx, 9, 18, 3, 4, '#3a3f4b')
    px(ctx, 3, 23, 4, 1, '#1f2937')
    px(ctx, 9, 22, 4, 1, '#1f2937')
  } else {
    px(ctx, 4, 18, 3, 5, '#3a3f4b')
    px(ctx, 9, 18, 3, 5, '#3a3f4b')
    px(ctx, 4, 23, 3, 1, '#1f2937')
    px(ctx, 9, 23, 3, 1, '#1f2937')
  }

  return c
}

/**
 * A desk seen from the front. The monitor sits on the left half so the seated
 * figure on the right stays fully visible -- the whole point of the floor is
 * seeing who is at their desk.
 */
export function drawDesk(accent: string, boss = false): HTMLCanvasElement {
  const w = boss ? 44 : 36
  const [c, ctx] = makeCanvas(w, 22)
  const top = boss ? '#6b4a2f' : '#8a6a48'
  const edge = boss ? '#4e341f' : '#6a4f33'

  // monitor, left of the worktop
  px(ctx, 2, 0, 14, 9, '#2b2f38')
  px(ctx, 3, 1, 12, 7, '#0f172a')
  px(ctx, 4, 2, 10, 1, accent)
  px(ctx, 4, 4, 7, 1, 'rgba(255,255,255,0.35)')
  px(ctx, 4, 6, 9, 1, 'rgba(255,255,255,0.2)')
  px(ctx, 8, 9, 2, 2, '#2b2f38')

  // worktop
  px(ctx, 0, 11, w, 3, top)
  px(ctx, 0, 14, w, 1, edge)

  // keyboard under the monitor, mug and paper tray on the free right half
  px(ctx, 3, 11, 11, 1, '#d6d3d1')
  px(ctx, w - 5, 9, 3, 2, accent)
  px(ctx, w - 2, 9, 1, 1, accent)
  px(ctx, w - 12, 10, 5, 1, '#e7e5e4')
  px(ctx, w - 12, 9, 5, 1, '#f5f5f4')

  // legs
  px(ctx, 1, 15, 2, 7, edge)
  px(ctx, w - 3, 15, 2, 7, edge)

  return c
}

/** Chair back, drawn behind a seated avatar. */
export function drawChair(): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(14, 18)
  px(ctx, 2, 0, 10, 9, '#3f4654')
  px(ctx, 3, 1, 8, 7, '#4c5464')
  px(ctx, 6, 9, 2, 5, '#2f3540')
  px(ctx, 2, 14, 10, 2, '#2f3540')
  return c
}

/** A single white pixel, stretched into the desk progress bars. */
export function drawPixel(): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(1, 1)
  px(ctx, 0, 0, 1, 1, '#ffffff')
  return c
}

/** Flying inter-agent message. */
export function drawEnvelope(): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(12, 9)
  px(ctx, 0, 0, 12, 9, '#f5f2ea')
  px(ctx, 0, 0, 12, 1, '#d6cfc0')
  px(ctx, 0, 8, 12, 1, '#d6cfc0')
  px(ctx, 0, 0, 1, 9, '#d6cfc0')
  px(ctx, 11, 0, 1, 9, '#d6cfc0')
  ctx.fillStyle = '#e0d8c8'
  for (let i = 0; i < 6; i++) {
    ctx.fillRect(i, 1 + i, 1, 1)
    ctx.fillRect(11 - i, 1 + i, 1, 1)
  }
  px(ctx, 4, 3, 4, 2, '#e4573d')
  return c
}

/** Carpet tile the floor is tiled with. */
export function drawCarpet(dark: boolean): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(16, 16)
  const base = dark ? '#232733' : '#cfc4ad'
  const alt = dark ? '#272c39' : '#c7bba3'
  px(ctx, 0, 0, 16, 16, base)
  for (let y = 0; y < 16; y += 2) {
    for (let x = (y / 2) % 2; x < 16; x += 2) px(ctx, x, y, 1, 1, alt)
  }
  px(ctx, 0, 0, 16, 1, dark ? '#1e222c' : '#c0b49b')
  px(ctx, 0, 0, 1, 16, dark ? '#1e222c' : '#c0b49b')
  return c
}

/** Plain tileable back wall -- features are placed as separate props. */
export function drawWall(dark: boolean): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(16, 40)
  px(ctx, 0, 0, 16, 33, dark ? '#2c3140' : '#e8e0cf')
  px(ctx, 0, 12, 16, 1, dark ? '#333949' : '#ded5c1')
  px(ctx, 0, 33, 16, 3, dark ? '#3a4152' : '#d3c8b2')
  px(ctx, 0, 36, 16, 4, dark ? '#20242f' : '#b9ad95')
  return c
}

/** A window with a view -- placed a couple of times along the wall. */
export function drawWindow(dark: boolean): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(26, 22)
  px(ctx, 0, 0, 26, 22, dark ? '#4b5567' : '#8d8371')
  px(ctx, 2, 2, 22, 18, dark ? '#141b2b' : '#9ec9e8')
  px(ctx, 12, 2, 2, 18, dark ? '#4b5567' : '#8d8371')
  px(ctx, 2, 10, 22, 1, dark ? '#4b5567' : '#8d8371')
  if (dark) {
    px(ctx, 5, 5, 1, 1, '#f5f2ea')
    px(ctx, 18, 7, 1, 1, '#f5f2ea')
    px(ctx, 8, 15, 1, 1, '#f5f2ea')
    px(ctx, 20, 16, 1, 1, '#f5f2ea')
  } else {
    px(ctx, 15, 4, 6, 3, '#ffffff')
    px(ctx, 4, 13, 7, 2, '#ffffff')
  }
  return c
}

/** Wall clock. */
export function drawClock(dark: boolean): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(10, 10)
  px(ctx, 1, 0, 8, 10, dark ? '#3d4455' : '#f5f2ea')
  px(ctx, 0, 1, 10, 8, dark ? '#3d4455' : '#f5f2ea')
  px(ctx, 2, 2, 6, 6, dark ? '#8a93a6' : '#cfc4ad')
  px(ctx, 4, 3, 1, 3, '#2b2118')
  px(ctx, 5, 5, 2, 1, '#e4573d')
  return c
}

/** Potted plant for the floor corners. */
export function drawPlant(): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(12, 18)
  px(ctx, 3, 12, 6, 6, '#a4562f')
  px(ctx, 3, 12, 6, 1, '#8b4526')
  px(ctx, 5, 4, 3, 9, '#2f6f52')
  px(ctx, 1, 6, 4, 2, '#3f8f6d')
  px(ctx, 7, 8, 4, 2, '#3f8f6d')
  px(ctx, 2, 2, 3, 3, '#3f8f6d')
  px(ctx, 7, 1, 4, 3, '#3f8f6d')
  return c
}

/** Water cooler -- every office has one. */
export function drawCooler(): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(10, 20)
  px(ctx, 2, 0, 6, 7, '#9ec9e8')
  px(ctx, 3, 1, 4, 5, '#bfe0f5')
  px(ctx, 1, 7, 8, 12, '#e7e5e4')
  px(ctx, 1, 7, 8, 1, '#c9c6c2')
  px(ctx, 3, 11, 4, 2, '#7b8794')
  px(ctx, 1, 19, 8, 1, '#9aa3ad')
  return c
}

/** Shared printer, tucked against a wall. */
export function drawPrinter(): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(18, 14)
  px(ctx, 0, 3, 18, 11, '#d6d3d1')
  px(ctx, 0, 3, 18, 1, '#b7b4b1')
  px(ctx, 3, 0, 12, 3, '#f5f5f4')
  px(ctx, 2, 6, 6, 1, '#3f8f6d')
  px(ctx, 12, 6, 4, 3, '#7b8794')
  px(ctx, 2, 10, 14, 2, '#b7b4b1')
  return c
}

/** Status lamp on the desk corner -- one canvas per tone. */
export function drawLamp(color: string): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(6, 6)
  ctx.fillStyle = color
  ctx.globalAlpha = 0.3
  ctx.fillRect(0, 0, 6, 6)
  ctx.globalAlpha = 1
  ctx.fillRect(2, 2, 2, 2)
  ctx.fillRect(1, 2, 1, 2)
  ctx.fillRect(4, 2, 1, 2)
  ctx.fillRect(2, 1, 2, 1)
  ctx.fillRect(2, 4, 2, 1)
  return c
}

/** The door new arrivals walk in through. */
export function drawDoor(dark: boolean): HTMLCanvasElement {
  const [c, ctx] = makeCanvas(18, 30)
  px(ctx, 0, 0, 18, 30, dark ? '#4b3a2a' : '#7a5636')
  px(ctx, 2, 2, 14, 26, dark ? '#5c4733' : '#8f6741')
  px(ctx, 3, 3, 12, 10, dark ? '#6a5340' : '#a2794f')
  px(ctx, 3, 15, 12, 11, dark ? '#6a5340' : '#a2794f')
  px(ctx, 13, 14, 2, 2, '#d9b26a')
  return c
}
