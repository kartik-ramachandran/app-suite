import PhosphorIcon from './PhosphorIcon'
import { useEffect, useRef, useState } from 'react'

interface Props {
  /** Called with the picked files (camera or upload). Read files[0] synchronously (before any await). */
  onPick: (files: FileList | null) => void
  busy?: boolean
  disabled?: boolean
  label: string
  busyLabel?: string
  icon?: string
  multiple?: boolean
  /** Extra copy shown in the chooser modal under the title. */
  hint?: string
}

/**
 * Shared "Take a photo / Upload" chooser used across the snap tools.
 * "Take a photo" opens a live camera preview via getUserMedia — this works on desktop
 * webcams as well as phones. If a camera isn't available or permission is denied, it
 * falls back to the native file dialog (which opens the camera app on mobile).
 */
export default function SnapButton({ onPick, busy, disabled, label, busyLabel = 'Reading…', icon = 'bi-camera', multiple, hint }: Props) {
  const [open, setOpen] = useState(false)
  const [cam, setCam] = useState(false)
  const [starting, setStarting] = useState(false)
  const camFallbackRef = useRef<HTMLInputElement>(null) // native camera/file dialog fallback
  const upRef = useRef<HTMLInputElement>(null)
  const videoRef = useRef<HTMLVideoElement>(null)
  const streamRef = useRef<MediaStream | null>(null)

  const stopStream = () => {
    streamRef.current?.getTracks().forEach(t => t.stop())
    streamRef.current = null
  }

  const onChange = (e: React.ChangeEvent<HTMLInputElement>) => { onPick(e.target.files); e.currentTarget.value = '' }

  const openUpload = () => { setOpen(false); upRef.current?.click() }

  const openCamera = async () => {
    // getUserMedia needs a secure context (https/localhost); fall back to the native dialog otherwise.
    if (!navigator.mediaDevices?.getUserMedia) { setOpen(false); camFallbackRef.current?.click(); return }
    setStarting(true)
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } }, audio: false })
      streamRef.current = stream
      setOpen(false)
      setCam(true)
    } catch {
      // No camera, blocked, or permission denied — fall back to the native file/camera dialog.
      setOpen(false)
      camFallbackRef.current?.click()
    } finally {
      setStarting(false)
    }
  }

  // Attach the stream once the video element is mounted.
  useEffect(() => {
    if (cam && videoRef.current && streamRef.current) {
      videoRef.current.srcObject = streamRef.current
      videoRef.current.play().catch(() => {})
    }
  }, [cam])

  // Release the camera if the component unmounts mid-capture.
  useEffect(() => () => stopStream(), [])

  const closeCam = () => { stopStream(); setCam(false) }

  const capture = () => {
    const v = videoRef.current
    if (!v || !v.videoWidth || !v.videoHeight) return
    const canvas = document.createElement('canvas')
    canvas.width = v.videoWidth
    canvas.height = v.videoHeight
    const ctx = canvas.getContext('2d')
    if (!ctx) return
    ctx.drawImage(v, 0, 0, canvas.width, canvas.height)
    canvas.toBlob(blob => {
      if (!blob) return
      const file = new File([blob], `photo-${Date.now()}.jpg`, { type: 'image/jpeg' })
      const dt = new DataTransfer()
      dt.items.add(file)
      const files = dt.files
      closeCam()
      onPick(files)
    }, 'image/jpeg', 0.92)
  }

  return (
    <>
      <button className="st-btn st-btn-primary" onClick={() => setOpen(true)} disabled={busy || disabled}>
        {busy ? <><span className="st-spin" /> {busyLabel}</> : <><PhosphorIcon className={`bi ${icon}`} /> {label}</>}
      </button>

      {/* Fallback native inputs (used only when getUserMedia is unavailable/denied). */}
      <input ref={camFallbackRef} type="file" accept="image/*" capture="environment" multiple={multiple} hidden onChange={onChange} />
      <input ref={upRef} type="file" accept="image/*" multiple={multiple} hidden onChange={onChange} />

      {open && (
        <div className="sb-backdrop" onClick={() => setOpen(false)}>
          <div className="sb-modal" onClick={e => e.stopPropagation()}>
            <button className="sb-close" aria-label="Close" onClick={() => setOpen(false)}><PhosphorIcon className="bi bi-x-lg" /></button>
            <h3><PhosphorIcon className="bi bi-camera" /> Add a photo</h3>
            {hint && <p className="sb-hint">{hint}</p>}
            <div className="sb-choices">
              <button className="sb-choice" onClick={openCamera} disabled={starting}>
                <PhosphorIcon className="bi bi-camera-fill" /><strong>{starting ? 'Starting…' : 'Take a photo'}</strong><span>Use your camera</span>
              </button>
              <button className="sb-choice" onClick={openUpload}>
                <PhosphorIcon className="bi bi-folder2-open" /><strong>Upload</strong><span>Choose from your device</span>
              </button>
            </div>
          </div>
        </div>
      )}

      {cam && (
        <div className="sb-backdrop" onClick={closeCam}>
          <div className="sb-cam" onClick={e => e.stopPropagation()}>
            <button className="sb-close" aria-label="Close" onClick={closeCam}><PhosphorIcon className="bi bi-x-lg" /></button>
            <video ref={videoRef} className="sb-cam-video" playsInline muted autoPlay />
            <div className="sb-cam-bar">
              <button className="sb-shutter" onClick={capture} aria-label="Capture photo"><PhosphorIcon className="bi bi-camera-fill" /></button>
              <button className="sb-cam-alt" onClick={() => { closeCam(); openUpload() }}><PhosphorIcon className="bi bi-folder2-open" /> Upload instead</button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
