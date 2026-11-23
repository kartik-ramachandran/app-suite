// Sky, and the image-based lighting taken from it.
//
// This module is what makes the deck look lit rather than shaded. Two pieces:
//
//  1. A gradient dome with a sun in it, drawn as one inverted sphere with one
//     shader. Changing the look costs a few uniform writes rather than
//     rebuilding anything.
//
//  2. That same dome, prefiltered through PMREMGenerator into an environment
//     map. This is the important half: with `scene.environment` set, every
//     standard material gets real reflections and a *directional* ambient
//     instead of the flat grey an AmbientLight gives. Metal picks up the sky,
//     the glass in the meeting room picks up the sky, and surfaces facing up
//     read differently from surfaces facing sideways — which is most of what
//     separates a rich render from a flat one.
//
// The environment is generated ONCE at build time here, not per frame: our sky
// does not move, so there is nothing to regenerate.

import * as THREE from 'three'

const SKY_VERT = /* glsl */ `
  varying vec3 vDir;
  void main() {
    vDir = position;
    // Pinned to the camera, so it can never be walked out of.
    vec4 mv = modelViewMatrix * vec4(position, 1.0);
    gl_Position = projectionMatrix * mv;
  }
`

const SKY_FRAG = /* glsl */ `
  varying vec3 vDir;
  uniform vec3 uTop;
  uniform vec3 uHorizon;
  uniform vec3 uSunColor;
  uniform vec3 uSunDir;
  uniform float uGlow;

  void main() {
    vec3 d = normalize(vDir);
    float h = clamp(d.y * 0.5 + 0.5, 0.0, 1.0);

    // A quick gradient near the horizon and a slow one overhead reads far more
    // like sky than a linear ramp does.
    vec3 col = mix(uHorizon, uTop, pow(h, 0.42));

    float sun = max(dot(d, uSunDir), 0.0);
    // Wide scatter then a tight halo. Kept modest on purpose: the sky is the
    // largest surface on screen, so anything above 1.0 feeds the bloom pass
    // across the whole frame and washes the room out.
    col += uSunColor * pow(sun, 6.0) * uGlow * 0.14;
    col += uSunColor * pow(sun, 60.0) * uGlow * 0.3;

    gl_FragColor = vec4(col, 1.0);
    #include <tonemapping_fragment>
    #include <colorspace_fragment>
  }
`

export interface SkyPalette {
  top: number
  horizon: number
  sun: number
  glow: number
}

export const SKIES: Record<'dark' | 'light', SkyPalette> = {
  // Night station: deep blue overhead, warmer at the deck line.
  dark: { top: 0x0a1230, horizon: 0x2a3550, sun: 0x9fc6ff, glow: 0.5 },
  // Daylit: pale sky, warm horizon.
  light: { top: 0x9dc4f0, horizon: 0xe8e2d4, sun: 0xfff2d8, glow: 0.85 },
}

export interface SkyRig {
  dome: THREE.Mesh
  /** Prefiltered environment. Assign to scene.environment. */
  envTexture: THREE.Texture | null
  setPalette(p: SkyPalette): void
  dispose(): void
}

/**
 * Build the dome and prefilter it into an environment map.
 *
 * `renderer` is needed because PMREM runs on the GPU — this is why the sky has
 * to be built after the renderer rather than alongside the scene.
 */
export function buildSky(renderer: THREE.WebGLRenderer, palette: SkyPalette): SkyRig {
  const uniforms = {
    uTop: { value: new THREE.Color(palette.top) },
    uHorizon: { value: new THREE.Color(palette.horizon) },
    uSunColor: { value: new THREE.Color(palette.sun) },
    uSunDir: { value: new THREE.Vector3(0.4, 0.55, 0.3).normalize() },
    uGlow: { value: palette.glow },
  }

  const geo = new THREE.SphereGeometry(1, 32, 20)
  const mat = new THREE.ShaderMaterial({
    uniforms,
    vertexShader: SKY_VERT,
    fragmentShader: SKY_FRAG,
    // Inverted: we are inside the sphere looking out.
    side: THREE.BackSide,
    depthWrite: false,
    depthTest: false,
  })

  const dome = new THREE.Mesh(geo, mat)
  // Must sit INSIDE the camera's far plane or it is clipped away entirely.
  // frustumCulled = false does not save it: the far plane still clips.
  dome.scale.setScalar(90)
  dome.frustumCulled = false
  // Drawn first, behind everything.
  dome.renderOrder = -1

  // --- environment ----------------------------------------------------------
  let envTexture: THREE.Texture | null = null
  let envTarget: THREE.WebGLRenderTarget | null = null
  let pmrem: THREE.PMREMGenerator | null = null

  function refreshEnvironment() {
    if (!pmrem) pmrem = new THREE.PMREMGenerator(renderer)

    // A second, small copy of the same dome. Sharing the material means every
    // palette change lands here too without a second set of uniform writes.
    const envScene = new THREE.Scene()
    const envDome = new THREE.Mesh(geo, mat)
    envDome.scale.setScalar(10)
    envDome.frustumCulled = false
    envScene.add(envDome)

    const next = pmrem.fromScene(envScene, 0, 0.5, 60)
    envTarget?.dispose()
    envTarget = next
    envTexture = next.texture
  }

  refreshEnvironment()

  return {
    dome,
    get envTexture() { return envTexture },
    setPalette(p: SkyPalette) {
      uniforms.uTop.value.setHex(p.top)
      uniforms.uHorizon.value.setHex(p.horizon)
      uniforms.uSunColor.value.setHex(p.sun)
      uniforms.uGlow.value = p.glow
      // The environment is derived from the dome, so it has to follow.
      refreshEnvironment()
    },
    dispose() {
      geo.dispose()
      mat.dispose()
      envTarget?.dispose()
      pmrem?.dispose()
    },
  }
}

/**
 * A soft blob shadow, drawn as a radial-gradient sprite on the deck.
 *
 * Real shadow maps at this resolution give each small figure about four grey
 * pixels. A contact shadow under every crew member and desk is what actually
 * plants things on the floor — cheap, and it survives any shadow setting.
 */
export function buildContactShadow(size = 128): {
  texture: THREE.Texture
  material: THREE.MeshBasicMaterial
  geometry: THREE.PlaneGeometry
  dispose(): void
} {
  const canvas = document.createElement('canvas')
  canvas.width = canvas.height = size
  const ctx = canvas.getContext('2d')!

  const g = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2)
  // Falls off fast, so the darkest part hugs the feet rather than spreading
  // into a grey disc.
  g.addColorStop(0, 'rgba(0,0,0,0.55)')
  g.addColorStop(0.45, 'rgba(0,0,0,0.22)')
  g.addColorStop(1, 'rgba(0,0,0,0)')
  ctx.fillStyle = g
  ctx.fillRect(0, 0, size, size)

  const texture = new THREE.CanvasTexture(canvas)
  texture.colorSpace = THREE.SRGBColorSpace

  const material = new THREE.MeshBasicMaterial({
    map: texture,
    transparent: true,
    depthWrite: false,
  })
  const geometry = new THREE.PlaneGeometry(1, 1)

  return {
    texture,
    material,
    geometry,
    dispose() {
      texture.dispose()
      material.dispose()
      geometry.dispose()
    },
  }
}
