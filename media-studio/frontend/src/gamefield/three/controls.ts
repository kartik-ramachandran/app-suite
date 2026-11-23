// Camera controls for the deck: drag to pan, wheel to zoom at the cursor.
//
// The one rule worth stating: the wheel zooms at the POINTER, not at the screen
// centre. The deck point under the cursor is held still while the zoom applies,
// so zooming into a corner of the room actually goes to that corner instead of
// sliding it off screen. Without that, zooming on a wide floor is a fight.
//
// Works on an orthographic camera, where "zoom" is the frustum size rather than
// a dolly — the camera never moves toward the deck, it just looks at less of it.

import * as THREE from 'three'

export interface DeckControls {
  /** Current zoom multiplier: 1 is the framing the layout chose. */
  readonly zoom: number
  /** True while a drag is in progress, so a click can be told from a drag. */
  readonly dragging: boolean
  /** Did the last pointer sequence move far enough to count as a drag? */
  readonly wasDrag: boolean
  /** Re-apply pan/zoom after the layout reframes the camera. */
  apply(): void
  /** Back to the framing the layout chose. */
  reset(): void
  dispose(): void
}

const MIN_ZOOM = 0.45
const MAX_ZOOM = 4.5
/** Pointer travel, in px, past which a press is a drag rather than a click. */
const DRAG_SLOP = 6

export function attachControls(
  dom: HTMLElement,
  camera: THREE.OrthographicCamera,
  opts: {
    /** Re-read whenever the layout changes: the untransformed frustum. */
    baseFrustum: () => { left: number; right: number; top: number; bottom: number }
    /** Called whenever pan/zoom changed, so the host can re-project overlays. */
    onChange: () => void
  },
): DeckControls {
  let zoom = 1
  const pan = new THREE.Vector2(0, 0)

  let dragging = false
  let moved = 0
  let lastX = 0
  let lastY = 0

  // Reused so a wheel burst does not allocate per event.
  const ray = new THREE.Raycaster()
  const ndc = new THREE.Vector2()
  const deckPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0)
  const hit = new THREE.Vector3()

  /** Where on the deck plane does this screen point land? */
  function groundAt(clientX: number, clientY: number, out: THREE.Vector3): boolean {
    const rect = dom.getBoundingClientRect()
    if (!rect.width || !rect.height) return false
    ndc.x = ((clientX - rect.left) / rect.width) * 2 - 1
    ndc.y = -((clientY - rect.top) / rect.height) * 2 + 1
    ray.setFromCamera(ndc, camera)
    return ray.ray.intersectPlane(deckPlane, out) !== null
  }

  /** Push the current pan/zoom onto the camera's frustum. */
  function apply() {
    const b = opts.baseFrustum()
    const w = (b.right - b.left) / 2 / zoom
    const h = (b.top - b.bottom) / 2 / zoom
    camera.left = -w + pan.x
    camera.right = w + pan.x
    camera.top = h + pan.y
    camera.bottom = -h + pan.y
    camera.updateProjectionMatrix()
    camera.updateMatrixWorld()
  }

  const onWheel = (e: WheelEvent) => {
    e.preventDefault()

    // Trackpads report tiny per-event deltas and mice report ~100, so both are
    // normalised to the same felt step. A trackpad pinch arrives as ctrl+wheel.
    const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? 100 : 1
    const raw = (e.deltaY * unit) / 100
    const step = Math.sign(raw) * Math.min(Math.abs(raw), 2.5) * (e.ctrlKey ? 1.6 : 1)

    // Anchor: remember the deck point under the cursor, zoom, then shift the
    // frustum so that same point is back under the cursor.
    const anchored = groundAt(e.clientX, e.clientY, hit)
    const before = anchored ? hit.clone() : null

    const next = THREE.MathUtils.clamp(zoom * (1 - step * 0.16), MIN_ZOOM, MAX_ZOOM)
    if (next === zoom) return
    zoom = next
    apply()

    if (before && groundAt(e.clientX, e.clientY, hit)) {
      // The deck is viewed at a fixed angle, so a world offset maps onto the
      // frustum's own axes rather than straight onto x/z.
      const dx = before.x - hit.x
      const dz = before.z - hit.z
      const right = new THREE.Vector3()
      const up = new THREE.Vector3()
      camera.matrixWorld.extractBasis(right, up, new THREE.Vector3())
      const delta = new THREE.Vector3(dx, 0, dz)
      pan.x += delta.dot(right)
      pan.y += delta.dot(up)
      apply()
    }

    opts.onChange()
  }

  const onDown = (e: PointerEvent) => {
    // Left button only; the nameplates handle their own clicks.
    if (e.button !== 0) return
    dragging = true
    moved = 0
    lastX = e.clientX
    lastY = e.clientY
    dom.setPointerCapture?.(e.pointerId)
  }

  const onMove = (e: PointerEvent) => {
    if (!dragging) return
    const dx = e.clientX - lastX
    const dy = e.clientY - lastY
    lastX = e.clientX
    lastY = e.clientY
    moved += Math.abs(dx) + Math.abs(dy)

    // Frustum units per pixel, so the deck tracks the cursor 1:1 at any zoom.
    const rect = dom.getBoundingClientRect()
    const b = opts.baseFrustum()
    const unitsPerPxX = ((b.right - b.left) / zoom) / Math.max(rect.width, 1)
    const unitsPerPxY = ((b.top - b.bottom) / zoom) / Math.max(rect.height, 1)

    pan.x -= dx * unitsPerPxX
    pan.y += dy * unitsPerPxY
    apply()
    opts.onChange()
  }

  const onUp = (e: PointerEvent) => {
    if (!dragging) return
    dragging = false
    dom.releasePointerCapture?.(e.pointerId)
  }

  dom.addEventListener('wheel', onWheel, { passive: false })
  dom.addEventListener('pointerdown', onDown)
  window.addEventListener('pointermove', onMove)
  window.addEventListener('pointerup', onUp)

  return {
    get zoom() { return zoom },
    get dragging() { return dragging },
    get wasDrag() { return moved > DRAG_SLOP },
    apply,
    reset() {
      zoom = 1
      pan.set(0, 0)
      apply()
      opts.onChange()
    },
    dispose() {
      dom.removeEventListener('wheel', onWheel)
      dom.removeEventListener('pointerdown', onDown)
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
    },
  }
}
