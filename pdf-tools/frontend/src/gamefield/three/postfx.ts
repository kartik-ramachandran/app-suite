// Post chain: bloom for the lamps and screens, then a tilt-shift blur.
//
// The tilt-shift is what makes the deck read as a model rather than a room you
// are standing in. It is a two-pass separable blur whose strength ramps with
// distance from a focus band across the screen — cheap, and the effect is
// entirely in how sharply that band falls off.

import * as THREE from 'three'
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js'
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js'
import { ShaderPass } from 'three/addons/postprocessing/ShaderPass.js'
import { SMAAPass } from 'three/addons/postprocessing/SMAAPass.js'
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js'
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js'

/**
 * One axis of the blur. `direction` is a pixel-space step, so the same shader
 * serves both the horizontal and vertical pass.
 */
const TiltShiftShader = {
  uniforms: {
    tDiffuse: { value: null as THREE.Texture | null },
    direction: { value: new THREE.Vector2(1, 0) },
    resolution: { value: new THREE.Vector2(1, 1) },
    /** 0..1 screen height where the plane of focus sits. */
    focus: { value: 0.55 },
    /** How tall the sharp band is. */
    band: { value: 0.22 },
    /** Max blur radius in pixels. */
    strength: { value: 2.6 },
  },
  vertexShader: /* glsl */ `
    varying vec2 vUv;
    void main() {
      vUv = uv;
      gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }
  `,
  fragmentShader: /* glsl */ `
    uniform sampler2D tDiffuse;
    uniform vec2 direction;
    uniform vec2 resolution;
    uniform float focus;
    uniform float band;
    uniform float strength;
    varying vec2 vUv;

    void main() {
      // Distance from the focus band, normalised so the ramp is smooth rather
      // than a hard edge either side of the sharp strip.
      float d = abs(vUv.y - focus);
      float blur = smoothstep(band, band + 0.42, d) * strength;

      if (blur < 0.01) {
        gl_FragColor = texture2D(tDiffuse, vUv);
        return;
      }

      vec2 step = direction / resolution * blur;

      // 9-tap gaussian. Weights sum to 1, so the pass is energy preserving and
      // repeated application does not brighten or darken the image.
      vec4 sum = texture2D(tDiffuse, vUv) * 0.2270270270;
      sum += texture2D(tDiffuse, vUv + step * 1.3846153846) * 0.3162162162;
      sum += texture2D(tDiffuse, vUv - step * 1.3846153846) * 0.3162162162;
      sum += texture2D(tDiffuse, vUv + step * 3.2307692308) * 0.0702702703;
      sum += texture2D(tDiffuse, vUv - step * 3.2307692308) * 0.0702702703;

      gl_FragColor = sum;
    }
  `,
}

export interface PostChain {
  composer: EffectComposer
  setSize(w: number, h: number): void
  setBloom(strength: number): void
  setTiltShift(enabled: boolean): void
  dispose(): void
}

export function buildPostChain(
  renderer: THREE.WebGLRenderer,
  scene: THREE.Scene,
  camera: THREE.Camera,
  w: number,
  h: number,
): PostChain {
  const composer = new EffectComposer(renderer)
  composer.addPass(new RenderPass(scene, camera))

  // Threshold high enough that only the lamps, screens and trim bloom — a room
  // where everything glows reads as fog, not light. Radius slightly wider than
  // before so the glow feels like light in air rather than an outline.
  const bloom = new UnrealBloomPass(new THREE.Vector2(w, h), 0.62, 0.62, 0.8)
  composer.addPass(bloom)

  const hPass = new ShaderPass(TiltShiftShader)
  const vPass = new ShaderPass(TiltShiftShader)
  vPass.uniforms.direction.value = new THREE.Vector2(0, 1)
  composer.addPass(hPass)
  composer.addPass(vPass)

  // The deck is all hard edges — cubicle panels, desk corners, helmets — and
  // without antialiasing they crawl as the camera moves. SMAA is cheaper than
  // MSAA on a post chain and does not need a multisampled target.
  const smaa = new SMAAPass()
  composer.addPass(smaa)

  composer.addPass(new OutputPass())

  const setSize = (nw: number, nh: number) => {
    composer.setSize(nw, nh)
    bloom.setSize(nw, nh)
    hPass.uniforms.resolution.value.set(nw, nh)
    vPass.uniforms.resolution.value.set(nw, nh)
  }
  setSize(w, h)

  return {
    composer,
    setSize,
    setBloom(strength: number) {
      bloom.strength = strength
    },
    setTiltShift(enabled: boolean) {
      hPass.enabled = enabled
      vPass.enabled = enabled
    },
    dispose() {
      bloom.dispose()
      smaa.dispose?.()
      hPass.dispose?.()
      vPass.dispose?.()
      composer.dispose()
    },
  }
}
