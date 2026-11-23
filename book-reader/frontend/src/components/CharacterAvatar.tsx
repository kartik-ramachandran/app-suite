import { useEffect, useRef } from 'react'

/**
 * A procedurally-drawn avatar for one character in a book.
 *
 * Drawn rather than illustrated on purpose: the cast is different for every
 * book, so there is no fixed set of portraits to ship, and generating images
 * would mean an image-model call per character — real money for decoration.
 *
 * Everything about the face is derived from the character's NAME, so the same
 * character always looks the same, across sessions and devices, with no state
 * stored anywhere. Gender shifts the palette and hair; the "speaking" flag
 * animates the mouth so you can see who is talking during playback.
 *
 * Canvas 2D rather than a WebGL library: this is a 56px flat portrait, and a
 * reader can have a dozen on screen at once. A WebGL context each was the one
 * thing that actually risked exhausting the browser's context limit, and none
 * of it needs a GPU.
 */

interface Props {
  name: string
  gender: string
  colour: string
  speaking?: boolean
  size?: number
}

/** Stable 32-bit hash, so a name always produces the same face. */
function hash(text: string): number {
  let h = 2166136261
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return Math.abs(h)
}

const SKIN = ['#f2d5b8', '#e8c39e', '#d4a373', '#b5835a', '#8d5524', '#5c3a21']
const HAIR = ['#2c1810', '#4a2c17', '#8b4513', '#c19a6b', '#e6be8a', '#6b6b6b', '#1a1a1a']

function ellipse(ctx: CanvasRenderingContext2D, x: number, y: number, rx: number, ry: number, fill: string, alpha = 1) {
  ctx.globalAlpha = alpha
  ctx.beginPath()
  ctx.ellipse(x, y, rx, ry, 0, 0, Math.PI * 2)
  ctx.fillStyle = fill
  ctx.fill()
  ctx.globalAlpha = 1
}

export default function CharacterAvatar({ name, gender, colour, speaking = false, size = 56 }: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const rafRef = useRef<number>(0)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return

    // Books are read for long stretches; a full device-pixel-ratio canvas per
    // character is more memory than a 56px avatar deserves.
    const dpr = Math.min(window.devicePixelRatio || 1, 2)
    canvas.width = size * dpr
    canvas.height = size * dpr
    const ctx = canvas.getContext('2d')
    if (!ctx) return
    ctx.scale(dpr, dpr)

    const seed = hash(name)
    const r = size / 2
    const skin = SKIN[seed % SKIN.length]
    const hair = HAIR[(seed >> 3) % HAIR.length]
    const long = gender === 'female' ? true : gender === 'male' ? false : (seed >> 7) % 2 === 0
    const beard = !long && (seed >> 13) % 3 === 0
    const browLift = ((seed >> 11) % 3) * r * 0.02
    const eyeY = -r * 0.08
    const eyeX = r * 0.2

    /** One full portrait. `openness` is the only thing that changes per frame. */
    const draw = (openness: number) => {
      ctx.clearRect(0, 0, size, size)
      ctx.save()
      ctx.translate(r, r)

      // Ring in the speaker's colour — the same colour used for their lines in
      // the reader, so the panel and the text agree at a glance.
      ctx.globalAlpha = 0.9
      ctx.beginPath()
      ctx.arc(0, 0, r - 1, 0, Math.PI * 2)
      ctx.lineWidth = 2
      ctx.strokeStyle = colour
      ctx.stroke()
      ctx.globalAlpha = 1

      if (long) ellipse(ctx, 0, r * 0.1, r * 0.72, r * 0.8, hair)

      ellipse(ctx, 0, 0, r * 0.52, r * 0.62, skin)
      ellipse(ctx, 0, -r * 0.34, r * 0.55, r * 0.3, hair)

      // Beard sits under the mouth, so it is painted before it.
      if (beard) ellipse(ctx, 0, r * 0.3, r * 0.38, r * 0.24, hair, 0.85)

      ellipse(ctx, -eyeX, eyeY, r * 0.07, r * 0.07, '#ffffff')
      ellipse(ctx, eyeX, eyeY, r * 0.07, r * 0.07, '#ffffff')
      ellipse(ctx, -eyeX, eyeY, r * 0.035, r * 0.035, '#1a1a1a')
      ellipse(ctx, eyeX, eyeY, r * 0.035, r * 0.035, '#1a1a1a')

      ctx.fillStyle = hair
      ctx.fillRect(-eyeX - r * 0.09, eyeY - r * 0.16 - browLift, r * 0.18, r * 0.035)
      ctx.fillRect(eyeX - r * 0.09, eyeY - r * 0.16 - browLift, r * 0.18, r * 0.035)

      ellipse(ctx, 0, r * 0.22, r * 0.18, r * openness, '#8b3a3a')

      // Initial badge, bottom-right.
      ellipse(ctx, r * 0.62, r * 0.62, r * 0.26, r * 0.26, colour)
      ctx.fillStyle = '#ffffff'
      ctx.font = `700 ${r * 0.34}px system-ui, sans-serif`
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      ctx.fillText(name.charAt(0).toUpperCase(), r * 0.62, r * 0.62)

      ctx.restore()
    }

    draw(0.12)

    // Animate only the character currently speaking. Running a loop for every
    // avatar would spend real work on a dozen idle faces.
    if (!speaking) return

    let t = 0
    const tick = () => {
      t += 0.25
      draw(0.12 + Math.abs(Math.sin(t)) * 0.3)
      rafRef.current = requestAnimationFrame(tick)
    }
    rafRef.current = requestAnimationFrame(tick)

    return () => cancelAnimationFrame(rafRef.current)
  }, [name, gender, colour, size, speaking])

  return (
    <canvas
      ref={canvasRef}
      style={{ width: size, height: size, flexShrink: 0 }}
      aria-hidden
    />
  )
}
