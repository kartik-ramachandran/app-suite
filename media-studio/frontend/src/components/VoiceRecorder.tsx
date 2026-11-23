import PhosphorIcon from './PhosphorIcon'
import { useState, useRef } from 'react'
import api from '../services/api'

interface VoiceRecorderProps {
  onTranscription: (text: string, refinedIntent: string) => void
  onError?: (error: string) => void
}

function VoiceRecorder({ onTranscription, onError }: VoiceRecorderProps) {
  const [isRecording, setIsRecording] = useState(false)
  const [isTranscribing, setIsTranscribing] = useState(false)
  const mediaRecorderRef = useRef<MediaRecorder | null>(null)
  const chunksRef = useRef<Blob[]>([])

  const isSupported = typeof navigator !== 'undefined' && navigator.mediaDevices && typeof navigator.mediaDevices.getUserMedia === 'function'

  if (!isSupported) return null

  const startRecording = async () => {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
      const mediaRecorder = new MediaRecorder(stream, { mimeType: 'audio/webm' })
      mediaRecorderRef.current = mediaRecorder
      chunksRef.current = []

      mediaRecorder.ondataavailable = (event) => {
        if (event.data.size > 0) {
          chunksRef.current.push(event.data)
        }
      }

      mediaRecorder.onstop = async () => {
        const audioBlob = new Blob(chunksRef.current, { type: 'audio/webm' })
        stream.getTracks().forEach(track => track.stop())
        await sendAudioForTranscription(audioBlob)
      }

      mediaRecorder.start()
      setIsRecording(true)
    } catch (err) {
      onError?.('Microphone access denied. Please allow microphone access.')
      console.error('Error accessing microphone:', err)
    }
  }

  const stopRecording = () => {
    if (mediaRecorderRef.current && isRecording) {
      mediaRecorderRef.current.stop()
      setIsRecording(false)
    }
  }

  const sendAudioForTranscription = async (audioBlob: Blob) => {
    try {
      setIsTranscribing(true)
      const formData = new FormData()
      formData.append('audioFile', audioBlob, 'recording.webm')

      const response = await api.post<{ text: string; refinedIntent: string }>('/prompts/voice-to-text', formData, {
        headers: { 'Content-Type': 'multipart/form-data' }
      })

      onTranscription(response.data.text, response.data.refinedIntent)
    } catch (err) {
      onError?.('Failed to transcribe audio. Please try again.')
      console.error('Transcription error:', err)
    } finally {
      setIsTranscribing(false)
    }
  }

  const handleClick = () => {
    if (isRecording) {
      stopRecording()
    } else {
      startRecording()
    }
  }

  return (
    <button
      type="button"
      className={`voice-recorder-btn ${isRecording ? 'recording' : ''} ${isTranscribing ? 'transcribing' : ''}`}
      onClick={handleClick}
      disabled={isTranscribing}
      title={isRecording ? 'Stop recording' : isTranscribing ? 'Transcribing...' : 'Voice input'}
    >
      {isTranscribing ? (
        <div className="btn-spinner"></div>
      ) : isRecording ? (
        <span className="recording-indicator">
          <span className="recording-dot"></span>
          <PhosphorIcon className="bi bi-stop-fill"></PhosphorIcon>
        </span>
      ) : (
        <PhosphorIcon className="bi bi-mic-fill"></PhosphorIcon>
      )}
    </button>
  )
}

export default VoiceRecorder
