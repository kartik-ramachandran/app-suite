// Crew figures for the three.js floor.
//
// Deliberately low-poly and jointed rather than a rigged GLB: the whole point of
// this module is that it ships no assets. Each figure is ~10 boxes and a sphere,
// posed by rotating a handful of joints, which is enough for the six poses the
// Gamefield speaks and cheap enough to run forty of them at once.
//
// `AvatarLook` is reused from the Pixi sprite sheet so an agent keeps exactly
// the same skin/hair/shirt identity it had before — only the rendering changed.

import * as THREE from 'three'
import type { AvatarLook } from '../sprites'

export type Pose = 'stand' | 'step' | 'sit' | 'sit-type' | 'sit-done' | 'sit-done2'

/** Shared across every crew member; disposed once when the last floor closes. */
const G = {
  torso: new THREE.BoxGeometry(0.42, 0.52, 0.24),
  hips: new THREE.BoxGeometry(0.38, 0.18, 0.24),
  head: new THREE.BoxGeometry(0.3, 0.3, 0.29),
  hair: new THREE.BoxGeometry(0.33, 0.14, 0.32),
  limb: new THREE.BoxGeometry(0.12, 0.42, 0.13),
  shoe: new THREE.BoxGeometry(0.14, 0.08, 0.22),
  glasses: new THREE.BoxGeometry(0.26, 0.06, 0.02),
  tie: new THREE.BoxGeometry(0.07, 0.26, 0.02),

  // --- astronaut suit ---
  // A sphere rather than a box: the helmet is the single silhouette cue that
  // says "astronaut" at this size, and a cube reads as a bigger head.
  helmet: new THREE.SphereGeometry(0.235, 18, 14),
  // A slightly flattened sphere clipped to the helmet's front. Scaled rather
  // than angle-cut: the patch has to face the camera, and the deck is always
  // viewed from the same direction, so 'front' is a fixed place.
  visor: new THREE.SphereGeometry(0.2, 16, 12),
  backpack: new THREE.BoxGeometry(0.34, 0.4, 0.16),
  packVent: new THREE.BoxGeometry(0.26, 0.04, 0.02),
  antenna: new THREE.CylinderGeometry(0.014, 0.02, 0.3, 6),
  antennaTip: new THREE.SphereGeometry(0.05, 10, 8),
  collar: new THREE.CylinderGeometry(0.155, 0.175, 0.07, 14),
  // Soft joints on the suit limbs, which is what makes a pressure suit read as
  // padded rather than as a person in overalls.
  cuff: new THREE.BoxGeometry(0.145, 0.06, 0.155),
  chestPanel: new THREE.BoxGeometry(0.16, 0.12, 0.02),
}

export function disposeCrewGeometry() {
  for (const g of Object.values(G)) g.dispose()
}

export interface CrewFigure {
  group: THREE.Group
  /** Status-coloured parts: the visor glow and the antenna tip. */
  suit: {
    visorMat: THREE.MeshStandardMaterial
    tipMat: THREE.MeshBasicMaterial
    tipLight: THREE.PointLight
  } | null
  /** Rotating these is what makes a pose. */
  joints: {
    hipL: THREE.Group
    hipR: THREE.Group
    shoulderL: THREE.Group
    shoulderR: THREE.Group
    torso: THREE.Group
    head: THREE.Group
  }
  materials: THREE.Material[]
  setPose(pose: Pose): void
  dispose(): void
}

/**
 * Build one crew member. Origin is between the feet, so positioning is just
 * `group.position.set(x, 0, z)` and a standing figure stands on the deck.
 */
export function buildCrew(look: AvatarLook, astronaut = true): CrewFigure {
  const mats: THREE.Material[] = []
  const mat = (color: string | number, rough = 0.9) => {
    const m = new THREE.MeshStandardMaterial({ color, roughness: rough, metalness: 0.02 })
    mats.push(m)
    return m
  }

  const skin = mat(look.skin)
  const shirt = mat(look.shirt)
  const hairMat = mat(look.hair)
  const trouser = mat(0x39405a)
  const shoe = mat(0x22262f)

  const group = new THREE.Group()

  // Torso pivots at the hips so a seated figure can lean.
  const torso = new THREE.Group()
  torso.position.y = 0.62
  group.add(torso)

  const hips = new THREE.Mesh(G.hips, trouser)
  hips.position.y = 0.04
  hips.castShadow = true
  torso.add(hips)

  const chest = new THREE.Mesh(G.torso, shirt)
  chest.position.y = 0.38
  chest.castShadow = true
  torso.add(chest)

  if (look.tie) {
    const tie = new THREE.Mesh(G.tie, mat(0xb5445f))
    tie.position.set(0, 0.36, 0.13)
    torso.add(tie)
  }

  // Head pivots at the neck.
  const head = new THREE.Group()
  head.position.y = 0.68
  torso.add(head)

  const skull = new THREE.Mesh(G.head, skin)
  skull.position.y = 0.15
  skull.castShadow = true
  // Under a helmet the head only shows through the visor, so it is scaled down
  // to sit inside the shell rather than push it out of shape.
  if (astronaut) skull.scale.setScalar(0.82)
  head.add(skull)

  // --- astronaut suit ------------------------------------------------------
  let suit: CrewFigure['suit'] = null

  if (astronaut) {
    // The suit is the agent's accent colour, desaturated toward white so a room
    // of them reads as a crew in matching kit rather than a bag of sweets. The
    // accent survives on the collar, the pack and the antenna tip.
    const suitColor = new THREE.Color(look.shirt).lerp(new THREE.Color(0xf2f4f8), 0.62)
    const suitMat = new THREE.MeshStandardMaterial({
      color: suitColor, roughness: 0.55, metalness: 0.08,
    })
    mats.push(suitMat)
    const accentMat = mat(look.shirt, 0.5)
    const trimMat = new THREE.MeshStandardMaterial({
      color: 0x2b3040, roughness: 0.4, metalness: 0.5,
    })
    mats.push(trimMat)

    // Re-skin the body in suit fabric: a pressure suit covers the torso and
    // limbs, so the shirt/trouser split underneath is no longer visible.
    chest.material = suitMat
    hips.material = suitMat

    // Helmet, sitting over the head bone.
    const helmet = new THREE.Mesh(G.helmet, suitMat)
    helmet.position.y = 0.17
    helmet.castShadow = true
    head.add(helmet)

    // Visor: emissive so it reads as lit from inside, and it is the part the
    // floor tints to show status.
    const visorMat = new THREE.MeshStandardMaterial({
      color: 0x0d1420,
      emissive: 0x4fc3f7,
      emissiveIntensity: 0.55,
      roughness: 0.12,
      metalness: 0.3,
    })
    mats.push(visorMat)
    const visor = new THREE.Mesh(G.visor, visorMat)
    // Flattened front-to-back and pushed forward so it sits proud of the shell
    // as a lens, not as a sphere hidden inside it.
    visor.scale.set(0.92, 0.72, 0.55)
    visor.position.set(0, 0.165, 0.115)
    head.add(visor)

    // Neck ring joining helmet to suit.
    const collar = new THREE.Mesh(G.collar, trimMat)
    collar.position.y = 0.02
    head.add(collar)

    // Life-support pack, on the back.
    const pack = new THREE.Mesh(G.backpack, accentMat)
    pack.position.set(0, 0.36, -0.2)
    pack.castShadow = true
    torso.add(pack)

    for (const vy of [0.46, 0.3]) {
      const vent = new THREE.Mesh(G.packVent, trimMat)
      vent.position.set(0, vy, -0.29)
      torso.add(vent)
    }

    // Chest control panel — the thing an astronaut actually looks down at.
    const panel = new THREE.Mesh(G.chestPanel, trimMat)
    panel.position.set(0.02, 0.4, 0.13)
    torso.add(panel)

    // Antenna on the crown, with a lit tip. Standing it on top rather than out
    // of the side keeps it legible at the distance the deck is viewed from.
    const antenna = new THREE.Mesh(G.antenna, trimMat)
    antenna.position.set(0.1, 0.36, -0.05)
    antenna.rotation.z = -0.14
    head.add(antenna)

    const tipMat = new THREE.MeshBasicMaterial({ color: 0x4fc3f7 })
    mats.push(tipMat)
    const tip = new THREE.Mesh(G.antennaTip, tipMat)
    tip.position.set(0.13, 0.53, -0.05)
    head.add(tip)

    // A real light, so the tip actually throws colour onto the helmet.
    const tipLight = new THREE.PointLight(0x4fc3f7, 0.9, 1.8, 2)
    tipLight.position.copy(tip.position)
    head.add(tipLight)

    suit = { visorMat, tipMat, tipLight }
  } else {
    // hairStyle 3 is the bald one — no cap mesh at all.
    if (look.hairStyle !== 3) {
      const hair = new THREE.Mesh(G.hair, hairMat)
      hair.position.y = 0.28 - (look.hairStyle === 1 ? 0.02 : 0)
      if (look.hairStyle === 2) hair.scale.set(1.05, 1.5, 1.05)
      head.add(hair)
    }

    if (look.glasses) {
      const gl = new THREE.Mesh(G.glasses, mat(0x1a1d24, 0.4))
      gl.position.set(0, 0.16, 0.15)
      head.add(gl)
    }
  }

  /** A limb hanging from a pivot: the pivot sits at the joint, the mesh below it. */
  const limb = (parent: THREE.Group, x: number, y: number, material: THREE.Material, withShoe: boolean) => {
    const pivot = new THREE.Group()
    pivot.position.set(x, y, 0)
    parent.add(pivot)

    const mesh = new THREE.Mesh(G.limb, material)
    mesh.position.y = -0.21
    mesh.castShadow = true
    pivot.add(mesh)

    if (withShoe) {
      const s = new THREE.Mesh(G.shoe, shoe)
      s.position.set(0, -0.44, 0.04)
      pivot.add(s)
    }
    return pivot
  }

  const shoulderL = limb(torso, -0.27, 0.56, shirt, false)
  const shoulderR = limb(torso, 0.27, 0.56, shirt, false)
  const hipL = limb(torso, -0.11, 0.0, trouser, true)
  const hipR = limb(torso, 0.11, 0.0, trouser, true)

  const joints = { hipL, hipR, shoulderL, shoulderR, torso, head }

  /**
   * Poses are absolute joint rotations, reset each time — no accumulation, so
   * switching pose mid-walk can never leave a limb bent from the last frame.
   */
  function setPose(pose: Pose) {
    hipL.rotation.set(0, 0, 0)
    hipR.rotation.set(0, 0, 0)
    shoulderL.rotation.set(0, 0, 0)
    shoulderR.rotation.set(0, 0, 0)
    head.rotation.set(0, 0, 0)
    torso.rotation.set(0, 0, 0)
    torso.position.y = 0.62

    switch (pose) {
      case 'stand':
        shoulderL.rotation.x = 0.06
        shoulderR.rotation.x = -0.06
        break

      case 'step':
        // Mid-stride. The arms swing opposite the legs, which is most of what
        // sells a walk at this level of detail.
        hipL.rotation.x = 0.55
        hipR.rotation.x = -0.55
        shoulderL.rotation.x = -0.45
        shoulderR.rotation.x = 0.45
        break

      case 'sit':
      case 'sit-type':
      case 'sit-done':
      case 'sit-done2': {
        // Seated: thighs forward from the hips. Rotating a limb that hangs
        // downward by -90 deg swings its far end forward and UP, so the figure
        // must also drop to chair height or it appears to hover.
        torso.position.y = 0.54
        hipL.rotation.x = -Math.PI / 2
        hipR.rotation.x = -Math.PI / 2

        if (pose === 'sit-type') {
          // Hands out over the keyboard.
          shoulderL.rotation.x = -1.15
          shoulderR.rotation.x = -1.15
          shoulderL.rotation.z = 0.16
          shoulderR.rotation.z = -0.16
          torso.rotation.x = 0.12
        } else if (pose === 'sit-done' || pose === 'sit-done2') {
          // One hand up — the finished-work raise. The second frame lifts it
          // further so alternating the two reads as a wave.
          shoulderR.rotation.x = pose === 'sit-done2' ? -2.7 : -2.35
          shoulderR.rotation.z = -0.3
          shoulderL.rotation.x = -0.2
        } else {
          shoulderL.rotation.x = -0.25
          shoulderR.rotation.x = -0.25
        }
        break
      }
    }
  }

  setPose('stand')

  return {
    group,
    suit,
    joints,
    materials: mats,
    setPose,
    dispose() {
      for (const m of mats) m.dispose()
    },
  }
}
