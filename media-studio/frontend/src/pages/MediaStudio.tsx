import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect, useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import VoiceRecorder from '../components/VoiceRecorder'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import '../App.css'

type MediaTab = 'image' | 'video' | 'voice'
type PromptStyle = 'professional' | 'creative' | 'technical' | 'conversational' | 'persuasive'
type ImagePlatform = 'dalle3' | 'midjourney' | 'stable-diffusion'
type ImageStyle = 'photorealistic' | 'digital-art' | 'anime' | '3d-render' | 'oil-painting' | 'watercolor' | 'sketch' | 'cinematic'
type AspectRatio = '1:1' | '16:9' | '9:16' | '4:3' | '3:4' | '21:9'
type VideoType = 'explainer' | 'tutorial' | 'advertisement' | 'documentary' | 'social-media' | 'testimonial'

interface ImagePromptSettings {
  platform: ImagePlatform
  subject: string
  style: ImageStyle
  aspectRatio: AspectRatio
  lighting: string
  cameraAngle: string
  mood: string
  details: string
}

interface VideoScriptSettings {
  videoType: VideoType
  topic: string
  duration: string
  targetAudience: string
  tone: string
  keyPoints: string
}

const PLATFORMS = [
  { value: 'dalle3' as ImagePlatform,           label: 'ChatGPT (OpenAI)', icon: 'bi-cpu',        desc: 'Best for realism & creativity' },
  { value: 'midjourney' as ImagePlatform,        label: 'Midjourney',       icon: 'bi-palette2',   desc: 'Best for stylised art' },
  { value: 'stable-diffusion' as ImagePlatform,  label: 'Stable Diffusion', icon: 'bi-stars',      desc: 'Best for customisation' },
]

const STYLES = [
  { value: 'photorealistic' as ImageStyle, label: 'Photorealistic', icon: 'bi-camera' },
  { value: 'digital-art'    as ImageStyle, label: 'Digital Art',    icon: 'bi-display' },
  { value: 'anime'          as ImageStyle, label: 'Anime',          icon: 'bi-star' },
  { value: '3d-render'      as ImageStyle, label: '3D Render',      icon: 'bi-box' },
  { value: 'oil-painting'   as ImageStyle, label: 'Oil Painting',   icon: 'bi-brush' },
  { value: 'watercolor'     as ImageStyle, label: 'Watercolor',     icon: 'bi-droplet' },
  { value: 'sketch'         as ImageStyle, label: 'Sketch',         icon: 'bi-pencil' },
  { value: 'cinematic'      as ImageStyle, label: 'Cinematic',      icon: 'bi-film' },
]

const RATIOS: { value: AspectRatio; w: number; h: number }[] = [
  { value: '1:1',  w: 20, h: 20 },
  { value: '16:9', w: 28, h: 16 },
  { value: '9:16', w: 16, h: 28 },
  { value: '4:3',  w: 24, h: 18 },
  { value: '3:4',  w: 18, h: 24 },
  { value: '21:9', w: 32, h: 13 },
]

const VIDEO_TYPES = [
  { value: 'explainer'     as VideoType, label: 'Explainer',   icon: 'bi-lightbulb' },
  { value: 'tutorial'      as VideoType, label: 'Tutorial',    icon: 'bi-mortarboard' },
  { value: 'advertisement' as VideoType, label: 'Ad',          icon: 'bi-megaphone' },
  { value: 'documentary'   as VideoType, label: 'Documentary', icon: 'bi-camera-video' },
  { value: 'social-media'  as VideoType, label: 'Social',      icon: 'bi-phone' },
  { value: 'testimonial'   as VideoType, label: 'Testimonial', icon: 'bi-chat-quote' },
]

const PROMPT_STYLES: { value: PromptStyle; label: string }[] = [
  { value: 'professional',  label: 'Professional' },
  { value: 'creative',      label: 'Creative' },
  { value: 'technical',     label: 'Technical' },
  { value: 'conversational',label: 'Conversational' },
  { value: 'persuasive',    label: 'Persuasive' },
]

interface Persona { id: number; name: string; icon: string }

const REFINEMENT_CATEGORIES = [
  {
    category: 'Tone', icon: 'bi-chat-heart',
    presets: [
      { id: 'formal',   label: 'Formal',   icon: 'bi-briefcase', instruction: 'Adjust the tone to be more formal, professional, and business-appropriate.' },
      { id: 'casual',   label: 'Casual',   icon: 'bi-emoji-smile', instruction: 'Adjust the tone to be more conversational, friendly, and approachable.' },
    ]
  },
  {
    category: 'Length', icon: 'bi-arrows-expand',
    presets: [
      { id: 'shorter',  label: 'Shorter',  icon: 'bi-arrows-collapse', instruction: 'Make this more concise while preserving all key intent. Remove redundancy.' },
      { id: 'detailed', label: 'Detailed', icon: 'bi-text-paragraph',  instruction: 'Expand with more specific details and comprehensive instructions.' },
    ]
  },
  {
    category: 'Enhancement', icon: 'bi-stars',
    presets: [
      { id: 'examples',    label: 'Examples',    icon: 'bi-lightbulb',          instruction: 'Add 2-3 concrete examples to make it clearer and more actionable.' },
      { id: 'structured',  label: 'Structure',   icon: 'bi-list-ol',            instruction: 'Add clear sections or numbered steps to improve readability.' },
      { id: 'constraints', label: 'Constraints', icon: 'bi-bullseye',           instruction: 'Add specific constraints to make the output more focused.' },
      { id: 'edge-cases',  label: 'Edge Cases',  icon: 'bi-exclamation-triangle', instruction: 'Add handling for edge cases and unexpected scenarios.' },
    ]
  }
]

function MediaStudio() {
  const { t } = useTranslation()
  const [searchParams] = useSearchParams()
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('media-studio')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Media Studio' }])
    return () => setBreadcrumbs([])
  }, [])

  const categoryToTab = (cat: string): MediaTab => {
    if (cat === 'Video Script') return 'video'
    if (cat === 'Voice Prompt') return 'voice'
    return 'image'
  }

  const [activeTab, setActiveTab] = useState<MediaTab>(() => {
    const cat = searchParams.get('category') ?? ''
    return categoryToTab(cat)
  })
  const [toast, setToast] = useState<{ message: string; visible: boolean }>({ message: '', visible: false })

  // Global settings
  const [mediaStyle, setMediaStyle] = useState<PromptStyle>('professional')
  const [selectedPersonaId, setSelectedPersonaId] = useState<number | null>(null)
  const [personas, setPersonas] = useState<Persona[]>([])
  const [mediaPersonaLenses, setMediaPersonaLenses] = useState<{ id: string; title: string; category: string; description: string; systemPrompt: string }[]>([])
  const [mediaSelectedPersonaId, setMediaSelectedPersonaId] = useState<string>('default')

  // Quick refinements
  const [mediaQuickRefining, setMediaQuickRefining] = useState<string | null>(null)
  const [imageCustomRefinement, setImageCustomRefinement] = useState('')
  const [videoCustomRefinement, setVideoCustomRefinement] = useState('')
  const [voiceCustomRefinement, setVoiceCustomRefinement] = useState('')

  const [imageSettings, setImageSettings] = useState<ImagePromptSettings>({
    platform: 'dalle3', subject: '', style: 'photorealistic',
    aspectRatio: '1:1', lighting: '', cameraAngle: '', mood: '', details: ''
  })
  const [generatedImagePrompt, setGeneratedImagePrompt] = useState('')
  const [generatingImage, setGeneratingImage] = useState(false)

  const [videoSettings, setVideoSettings] = useState<VideoScriptSettings>({
    videoType: 'explainer', topic: '', duration: '60',
    targetAudience: '', tone: 'professional', keyPoints: ''
  })
  const [generatedVideoScript, setGeneratedVideoScript] = useState('')
  const [generatingVideo, setGeneratingVideo] = useState(false)

  const [voiceTranscript, setVoiceTranscript] = useState('')
  const [enhancedPrompt, setEnhancedPrompt] = useState('')
  const [processingVoice] = useState(false)

  // Image goals modal
  const [imageSuggestions, setImageSuggestions] = useState<string[]>([])
  const [imageSelectedGoals, setImageSelectedGoals] = useState<string[]>([])
  const [imageLoadingSuggestions, setImageLoadingSuggestions] = useState(false)
  const [showImageGoalsModal, setShowImageGoalsModal] = useState(false)
  const [imageAdditionalContext, setImageAdditionalContext] = useState('')

  // Video goals modal
  const [videoSuggestions, setVideoSuggestions] = useState<string[]>([])
  const [videoSelectedGoals, setVideoSelectedGoals] = useState<string[]>([])
  const [videoLoadingSuggestions, setVideoLoadingSuggestions] = useState(false)
  const [showVideoGoalsModal, setShowVideoGoalsModal] = useState(false)
  const [videoAdditionalContext, setVideoAdditionalContext] = useState('')

  // Voice goals modal
  const [voiceSuggestions, setVoiceSuggestions] = useState<string[]>([])
  const [voiceSelectedGoals, setVoiceSelectedGoals] = useState<string[]>([])
  const [, setVoiceLoadingSuggestions] = useState(false)
  const [showVoiceGoalsModal, setShowVoiceGoalsModal] = useState(false)
  const [voiceAdditionalContext, setVoiceAdditionalContext] = useState('')
  const [enhancingVoice, setEnhancingVoice] = useState(false)
  const [isSaving, setIsSaving] = useState(false)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setShowImageGoalsModal(false)
        setShowVideoGoalsModal(false)
        setShowVoiceGoalsModal(false)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  useEffect(() => {
    const loadId = searchParams.get('load')
    if (!loadId) return
    api.get<{ content: string; category: string }>(`/prompts/${loadId}`)
      .then(res => {
        const { content, category } = res.data
        const tab = categoryToTab(category)
        setActiveTab(tab)
        if (tab === 'image') setGeneratedImagePrompt(content)
        else if (tab === 'video') setGeneratedVideoScript(content)
        else { setEnhancedPrompt(content); setVoiceTranscript(content) }
        setTimeout(() => document.querySelector('.ms-result')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 150)
      })
      .catch(() => {})
  }, [searchParams])

  const showToast = (message: string) => {
    setToast({ message, visible: true })
    setTimeout(() => setToast({ message: '', visible: false }), 2000)
  }

  const doSave = async (content: string, category: string, name: string) => {
    if (isSaving) return
    setIsSaving(true)
    try {
      await api.post('/prompts', { name: name.slice(0, 100), content, category, isPublic: false })
      showToast(`Saved to your library!`)
    } catch {
      showToast('Failed to save — please try again')
    } finally {
      setIsSaving(false)
    }
  }

  const [loadingMediaPersonas, setLoadingMediaPersonas] = useState(false)

  const handleChooseImagePersona = async () => {
    if (!imageSettings.subject.trim()) { showToast('Please describe what you want to generate'); return }
    setLoadingMediaPersonas(true)
    setMediaPersonaLenses([])
    setMediaSelectedPersonaId('default')
    try {
      const res = await api.post<{ personas: { id: string; title: string; category: string; description: string; systemPrompt: string }[] }>('/prompts/suggest-personas', {
        intent: `Create an image: ${imageSettings.subject}`, style: 'creative', category: 'image generation'
      })
      setMediaPersonaLenses(res.data.personas ?? [])
    } catch { /* silently fall through — user can still generate goals */ }
    finally { setLoadingMediaPersonas(false) }
  }

  const handleGetImageSuggestions = async () => {
    if (!imageSettings.subject.trim()) { showToast('Please describe what you want to generate'); return }
    setImageLoadingSuggestions(true)
    setImageSuggestions([])
    setImageSelectedGoals([])
    const selectedLens = mediaPersonaLenses.find(l => l.id === mediaSelectedPersonaId)
    try {
      const res = await api.post<{ goals: string[]; suggestedStyle: string }>('/prompts/generate-suggestions', {
        intent: `Create an image: ${imageSettings.subject}`, style: 'creative', category: 'image generation',
        personaInstruction: selectedLens?.systemPrompt ?? null,
      })
      setImageSuggestions(res.data.goals)
      setShowImageGoalsModal(true)
    } catch { showToast('Failed to get suggestions') }
    finally { setImageLoadingSuggestions(false) }
  }

  const handleChooseVideoPersona = async () => {
    if (!videoSettings.topic.trim()) { showToast('Please enter a video topic'); return }
    setLoadingMediaPersonas(true)
    setMediaPersonaLenses([])
    setMediaSelectedPersonaId('default')
    try {
      const res = await api.post<{ personas: { id: string; title: string; category: string; description: string; systemPrompt: string }[] }>('/prompts/suggest-personas', {
        intent: `Create a ${videoSettings.videoType} video: ${videoSettings.topic}`, style: videoSettings.tone, category: 'video script'
      })
      setMediaPersonaLenses(res.data.personas ?? [])
    } catch { /* silently fall through */ }
    finally { setLoadingMediaPersonas(false) }
  }

  const handleGetVideoSuggestions = async () => {
    if (!videoSettings.topic.trim()) { showToast('Please enter a video topic'); return }
    setVideoLoadingSuggestions(true)
    setVideoSuggestions([])
    setVideoSelectedGoals([])
    const selectedLens = mediaPersonaLenses.find(l => l.id === mediaSelectedPersonaId)
    try {
      const res = await api.post<{ goals: string[]; suggestedStyle: string }>('/prompts/generate-suggestions', {
        intent: `Create a ${videoSettings.videoType} video: ${videoSettings.topic}`, style: videoSettings.tone, category: 'video script',
        personaInstruction: selectedLens?.systemPrompt ?? null,
      })
      setVideoSuggestions(res.data.goals)
      setShowVideoGoalsModal(true)
    } catch { showToast('Failed to get suggestions') }
    finally { setVideoLoadingSuggestions(false) }
  }

  const handleGenerateImagePrompt = async (goals?: string[], additionalCtx?: string) => {
    if (!imageSettings.subject.trim()) { showToast('Please describe what you want to generate'); return }
    setGeneratingImage(true)
    const selectedLens = mediaPersonaLenses.find(l => l.id === mediaSelectedPersonaId)
    try {
      const response = await api.post('/media/image-prompt', {
        ...imageSettings,
        selectedGoals: goals ?? imageSelectedGoals,
        additionalContext: additionalCtx ?? imageAdditionalContext,
        personaInstruction: selectedLens?.systemPrompt ?? null,
      })
      setGeneratedImagePrompt(response.data.prompt)
      showToast('Image prompt generated!')
    } catch (error) {
      showToast('Failed to generate image prompt')
      console.error(error)
    } finally { setGeneratingImage(false) }
  }

  const handleGenerateVideoScript = async (goals?: string[], additionalCtx?: string) => {
    if (!videoSettings.topic.trim()) { showToast('Please enter a video topic'); return }
    setGeneratingVideo(true)
    const selectedLens = mediaPersonaLenses.find(l => l.id === mediaSelectedPersonaId)
    try {
      const response = await api.post('/media/video-script', {
        ...videoSettings,
        selectedGoals: goals ?? videoSelectedGoals,
        additionalContext: additionalCtx ?? videoAdditionalContext,
        personaInstruction: selectedLens?.systemPrompt ?? null,
      })
      setGeneratedVideoScript(response.data.script)
      showToast('Video script generated!')
    } catch (error) {
      showToast('Failed to generate video script')
      console.error(error)
    } finally { setGeneratingVideo(false) }
  }

  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const handleVoiceTranscription = async (transcript: string, _refinedIntent: string) => {
    setVoiceTranscript(transcript)
    setEnhancedPrompt('')
    setVoiceLoadingSuggestions(true)
    setVoiceSuggestions([])
    setVoiceSelectedGoals([])
    try {
      const res = await api.post<{ goals: string[]; suggestedStyle: string }>('/prompts/generate-suggestions', {
        intent: transcript,
        style: 'structured',
        category: 'voice prompt enhancement'
      })
      setVoiceSuggestions(res.data.goals)
      setShowVoiceGoalsModal(true)
    } catch { showToast('Failed to get suggestions') }
    finally { setVoiceLoadingSuggestions(false) }
  }

  const handleEnhanceVoicePrompt = async (goals: string[], additionalCtx: string) => {
    setEnhancingVoice(true)
    try {
      const response = await api.post('/media/enhance-transcript', {
        transcript: voiceTranscript,
        selectedGoals: goals,
        additionalContext: additionalCtx
      })
      setEnhancedPrompt(response.data.enhancedPrompt)
      showToast('Voice converted to prompt!')
    } catch (error) {
      showToast('Failed to enhance transcript')
      console.error(error)
    } finally { setEnhancingVoice(false) }
  }

  useEffect(() => {
    api.get<Persona[]>('/personas').then(r => setPersonas(r.data)).catch(() => {})
  }, [])

  const handleMediaRefine = async (
    currentText: string,
    setter: (v: string) => void,
    presetId: string
  ) => {
    if (!currentText.trim()) return
    const preset = REFINEMENT_CATEGORIES.flatMap(c => c.presets).find(p => p.id === presetId)
    if (!preset) return
    setMediaQuickRefining(presetId)
    try {
      const r = await api.post<{ prompt: string }>('/prompts/quick-refine', {
        currentPrompt: currentText, refinementType: presetId,
        instruction: preset.instruction, style: mediaStyle
      })
      setter(r.data.prompt)
      showToast(`"${preset.label}" applied!`)
    } catch { showToast('Refinement failed') }
    finally { setMediaQuickRefining(null) }
  }

  const handleMediaCustomRefine = async (
    currentText: string,
    setter: (v: string) => void,
    customInstruction: string,
    clearCustom: () => void
  ) => {
    if (!currentText.trim() || !customInstruction.trim()) return
    setMediaQuickRefining('custom')
    try {
      const r = await api.post<{ prompt: string }>('/prompts/quick-refine', {
        currentPrompt: currentText, refinementType: 'custom',
        instruction: customInstruction, style: mediaStyle
      })
      setter(r.data.prompt)
      clearCustom()
      showToast('Custom refinement applied!')
    } catch { showToast('Refinement failed') }
    finally { setMediaQuickRefining(null) }
  }

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text)
    showToast('Copied to clipboard!')
  }

  const platformLabel = PLATFORMS.find(p => p.value === imageSettings.platform)?.label ?? ''

  const QuickRefinements = ({
    currentText,
    setter,
    customValue,
    setCustomValue,
  }: {
    currentText: string
    setter: (v: string) => void
    customValue: string
    setCustomValue: (v: string) => void
  }) => (
    <div className="ms-refinements">
      <span className="ms-refinements-label"><PhosphorIcon className="bi bi-sliders" /> {t('mediaStudio.quickRefinements')}</span>
      {REFINEMENT_CATEGORIES.map(cat => (
        <div key={cat.category} className="ms-refinement-category">
          <span className="ms-refinement-category-label"><PhosphorIcon className={`bi ${cat.icon}`} /> {cat.category}</span>
          <div className="ms-refinement-pills">
            {cat.presets.map(p => (
              <button
                key={p.id}
                type="button"
                className={`ms-refinement-pill${mediaQuickRefining === p.id ? ' loading' : ''}`}
                onClick={() => handleMediaRefine(currentText, setter, p.id)}
                disabled={mediaQuickRefining !== null}
              >
                <PhosphorIcon className={`bi ${p.icon}`} />
                {p.label}
                {mediaQuickRefining === p.id && <span className="pg-pill-spinner" />}
              </button>
            ))}
          </div>
        </div>
      ))}
      <div className="ms-custom-refine-row">
        <input
          type="text"
          className="ms-custom-refine-input"
          value={customValue}
          onChange={e => setCustomValue(e.target.value)}
          placeholder="Your own refinement instruction..."
          onKeyDown={e => { if (e.key === 'Enter') handleMediaCustomRefine(currentText, setter, customValue, () => setCustomValue('')) }}
          disabled={mediaQuickRefining !== null}
        />
        <button
          type="button"
          className="btn btn-primary btn-sm"
          onClick={() => handleMediaCustomRefine(currentText, setter, customValue, () => setCustomValue(''))}
          disabled={!customValue.trim() || mediaQuickRefining !== null}
        >
          {mediaQuickRefining === 'custom' ? <span className="pg-pill-spinner" /> : <><PhosphorIcon className="bi bi-send" /> Apply</>}
        </button>
      </div>
    </div>
  )

  // Gamefield: three benches — stills, motion and voice — under a studio lead.
  const field = useMemo<FieldModel>(() => {
    const benches = [
      { id: 'image', name: 'Stills', role: imageSettings.platform, busy: generatingImage, out: generatedImagePrompt },
      { id: 'video', name: 'Motion', role: videoSettings.videoType, busy: generatingVideo, out: generatedVideoScript },
      { id: 'voice', name: 'Voice', role: 'transcript to prompt', busy: enhancingVoice, out: voiceTranscript },
    ]

    const desks = benches.map(b => ({
      id: b.id,
      name: b.name,
      role: b.role,
      status: (b.busy ? 'working' : b.out ? 'done' : 'idle') as 'working' | 'done' | 'idle',
      detail: b.busy ? 'Drafting the prompt…' : bubble(b.out),
      output: b.out || undefined,
    }))

    const busy = benches.some(b => b.busy)
    return {
      title: 'Media Studio',
      subtitle: `${activeTab} bench open`,
      running: busy,
      agents: [
        ...desks,
        {
          id: 'lead',
          name: 'Studio Lead',
          role: mediaStyle,
          status: (busy ? 'working' : desks.some(d => d.status === 'done') ? 'done' : 'idle') as 'working' | 'done' | 'idle',
          detail: busy ? 'Keeping the benches fed.' : undefined,
          boss: true,
        },
      ],
      messages: desks.filter(d => d.status === 'done').map(d => ({ id: `up-${d.id}`, from: d.id, to: 'lead' })),
      events: desks.filter(d => d.status === 'done').map(d => ({ id: `ev-${d.id}`, text: `${d.name} bench has a draft ready.`, tone: 'good' as const })),
    }
  }, [generatingImage, generatedImagePrompt, generatingVideo, generatedVideoScript, enhancingVoice, voiceTranscript, imageSettings, videoSettings, activeTab, mediaStyle])

  return (
    <>
      <div className="ms-root">

        {/* Hero */}
        <div className="ms-hero">
          <div className="ms-hero-icon">
            <PhosphorIcon className="bi bi-collection-play" />
          </div>
          <div>
            <h1 className="ms-hero-title">{t('mediaStudio.title')}</h1>
            <p className="ms-hero-sub">Generate optimised prompts for images, craft video scripts, and convert voice ideas into structured prompts</p>
          </div>
        </div>

        <AgentStage {...stageProps} model={field} />

        {/* Segment tabs */}
        <div className="ms-tabs">
          {([
            { key: 'image' as MediaTab, icon: 'bi-image',        label: t('mediaStudio.imageTab') },
            { key: 'video' as MediaTab, icon: 'bi-camera-video', label: t('mediaStudio.videoTab') },
            { key: 'voice' as MediaTab, icon: 'bi-mic',          label: t('mediaStudio.voiceTab') },
          ]).map(t => (
            <button
              key={t.key}
              className={`ms-tab-btn${activeTab === t.key ? ' active' : ''}`}
              onClick={() => setActiveTab(t.key)}
            >
              <PhosphorIcon className={`bi ${t.icon}`} />
              {t.label}
            </button>
          ))}
        </div>

        {/* ── IMAGE TAB ── */}
        {activeTab === 'image' && (
          <div className="ms-panel">
            <div className="ms-panel-header">
              <div className="ms-panel-icon ms-panel-icon--image">
                <PhosphorIcon className="bi bi-image" />
              </div>
              <div>
                <div className="ms-panel-title">Image Prompt Generator</div>
                <div className="ms-panel-sub">Craft the perfect prompt for any AI image platform</div>
              </div>
              <span className="ms-badge">Free</span>
            </div>

            <div className="ms-panel-body">
              {/* Platform */}
              <div>
                <div className="ms-label">{t('mediaStudio.platform')}</div>
                <div className="ms-platform-grid">
                  {PLATFORMS.map(p => (
                    <button
                      key={p.value}
                      data-platform={p.value}
                      className={`ms-platform-card${imageSettings.platform === p.value ? ' selected' : ''}`}
                      onClick={() => setImageSettings({ ...imageSettings, platform: p.value })}
                    >
                      <PhosphorIcon className={`bi ${p.icon} ms-platform-icon`} />
                      <div className="ms-platform-name">{p.label}</div>
                      <div className="ms-platform-desc">{p.desc}</div>
                    </button>
                  ))}
                </div>
              </div>

              {/* Subject */}
              <div>
                <div className="ms-label">What do you want to create? <span className="ms-required">*</span></div>
                <textarea
                  className="ms-textarea ms-textarea-lg"
                  value={imageSettings.subject}
                  onChange={(e) => setImageSettings({ ...imageSettings, subject: e.target.value })}
                  placeholder="E.g., A majestic dragon soaring over ancient snow-capped mountains at golden hour..."
                  rows={3}
                />
                <button
                  type="button"
                  className="ms-generate-btn"
                  style={{ marginTop: '0.5rem' }}
                  onClick={handleChooseImagePersona}
                  disabled={loadingMediaPersonas || !imageSettings.subject.trim()}
                >
                  {loadingMediaPersonas
                    ? <><div className="btn-spinner" /> Suggesting personas...</>
                    : <><PhosphorIcon className="bi bi-person-bounding-box" /> Choose Persona →</>}
                </button>
              </div>

              {/* Style chips */}
              <div>
                <div className="ms-label">{t('mediaStudio.style')}</div>
                <div className="ms-chip-row">
                  {STYLES.map(s => (
                    <button
                      key={s.value}
                      className={`ms-chip${imageSettings.style === s.value ? ' selected' : ''}`}
                      onClick={() => setImageSettings({ ...imageSettings, style: s.value })}
                    >
                      <PhosphorIcon className={`bi ${s.icon}`} />
                      {s.label}
                    </button>
                  ))}
                </div>
              </div>

              {/* Aspect ratio */}
              <div>
                <div className="ms-label">{t('mediaStudio.aspectRatio')}</div>
                <div className="ms-ratio-grid">
                  {RATIOS.map(r => (
                    <button
                      key={r.value}
                      className={`ms-ratio-btn${imageSettings.aspectRatio === r.value ? ' selected' : ''}`}
                      onClick={() => setImageSettings({ ...imageSettings, aspectRatio: r.value })}
                    >
                      <div className="ms-ratio-preview" style={{ width: r.w, height: r.h }} />
                      <span className="ms-ratio-label">{r.value}</span>
                    </button>
                  ))}
                </div>
              </div>

              {/* Advanced row */}
              <div className="ms-row-3">
                <div>
                  <div className="ms-label">{t('mediaStudio.lighting')}</div>
                  <input className="ms-input" type="text" value={imageSettings.lighting}
                    onChange={(e) => setImageSettings({ ...imageSettings, lighting: e.target.value })}
                    placeholder={t('mediaStudio.lightingPlaceholder')} />
                </div>
                <div>
                  <div className="ms-label">{t('mediaStudio.cameraAngle')}</div>
                  <input className="ms-input" type="text" value={imageSettings.cameraAngle}
                    onChange={(e) => setImageSettings({ ...imageSettings, cameraAngle: e.target.value })}
                    placeholder={t('mediaStudio.cameraAnglePlaceholder')} />
                </div>
                <div>
                  <div className="ms-label">{t('mediaStudio.mood')}</div>
                  <input className="ms-input" type="text" value={imageSettings.mood}
                    onChange={(e) => setImageSettings({ ...imageSettings, mood: e.target.value })}
                    placeholder={t('mediaStudio.moodPlaceholder')} />
                </div>
              </div>

              {/* Style + Persona */}
              <div className="ms-row-2">
                <div className="pg-form-group">
                  <label className="pg-label">Style</label>
                  <select className="ms-select" value={mediaStyle} onChange={e => setMediaStyle(e.target.value as PromptStyle)}>
                    {PROMPT_STYLES.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
                  </select>
                </div>
              </div>

              {/* Persona cards + Generate Goals button */}
              {mediaPersonaLenses.length > 0 && activeTab === 'image' && (
                <div className="pg-form-group">
                  <label className="pg-label" style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                    <PhosphorIcon className="bi bi-person-bounding-box" style={{ color: '#833ab4' }} /> Choose a Persona
                  </label>
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(155px, 1fr))', gap: 8, marginTop: 2, marginBottom: 12 }}>
                    <button type="button" className="pg-persona-card" data-selected={mediaSelectedPersonaId === 'default'} onClick={() => setMediaSelectedPersonaId('default')}
                      style={{ padding: '9px 11px', borderRadius: 8, cursor: 'pointer', border: `1.5px solid ${mediaSelectedPersonaId === 'default' ? '#833ab4' : '#d1d5db'}`, background: mediaSelectedPersonaId === 'default' ? 'rgba(131,58,180,0.07)' : '#fff' }}>
                      <div style={{ textAlign: 'center' }}>
                        <div style={{ fontSize: '0.65rem', fontWeight: 700, color: '#833ab4', textTransform: 'uppercase', marginBottom: 2 }}>Default</div>
                        <div style={{ fontWeight: 700, fontSize: '0.8rem', color: '#000', marginBottom: 2 }}>Balanced AI</div>
                      </div>
                    </button>
                    {mediaPersonaLenses.map(lens => (
                      <button key={lens.id} type="button" className="pg-persona-card" data-selected={mediaSelectedPersonaId === lens.id} onClick={() => setMediaSelectedPersonaId(lens.id)}
                        style={{ padding: '9px 11px', borderRadius: 8, cursor: 'pointer', border: `1.5px solid ${mediaSelectedPersonaId === lens.id ? '#833ab4' : '#d1d5db'}`, background: mediaSelectedPersonaId === lens.id ? 'rgba(131,58,180,0.07)' : '#fff' }}>
                        <div style={{ textAlign: 'center' }}>
                          <div style={{ fontSize: '0.65rem', fontWeight: 700, color: '#833ab4', textTransform: 'uppercase', marginBottom: 2 }}>{lens.category}</div>
                          <div style={{ fontWeight: 700, fontSize: '0.8rem', color: '#000', marginBottom: 4 }}>{lens.title}</div>
                        </div>
                        <div style={{ fontSize: '0.72rem', color: '#555', lineHeight: 1.35, textAlign: 'left' }}>{lens.description}</div>
                      </button>
                    ))}
                  </div>
                  <button
                    type="button"
                    className="ms-generate-btn"
                    onClick={handleGetImageSuggestions}
                    disabled={imageLoadingSuggestions}
                  >
                    {imageLoadingSuggestions
                      ? <><div className="btn-spinner" /> Getting goals...</>
                      : <><PhosphorIcon className="bi bi-list-check" /> Generate Goals →</>}
                  </button>
                </div>
              )}

              {/* Additional details */}
              <div>
                <div className="ms-label">{t('mediaStudio.additionalDetails')} <span className="ms-optional">(optional)</span></div>
                <textarea
                  className="ms-textarea"
                  value={imageSettings.details}
                  onChange={(e) => setImageSettings({ ...imageSettings, details: e.target.value })}
                  placeholder={t('mediaStudio.additionalDetailsPlaceholder')}
                  rows={2}
                />
              </div>

              {imageSelectedGoals.length > 0 && (
                <button
                  className="ms-generate-btn"
                  onClick={() => handleGenerateImagePrompt(imageSelectedGoals, imageAdditionalContext)}
                  disabled={generatingImage}
                >
                  {generatingImage
                    ? <><div className="btn-spinner" /> {t('mediaStudio.generating')}</>
                    : <><PhosphorIcon className="bi bi-magic" /> {t('mediaStudio.generate')}</>}
                </button>
              )}

              {generatedImagePrompt && (
                <>
                  <div className="ms-result">
                    <div className="ms-result-header">
                      <span className="ms-result-title"><PhosphorIcon className="bi bi-stars" /> Optimised for {platformLabel}</span>
                      <div style={{ display: 'flex', gap: '0.5rem' }}>
                        <button className="ms-copy-btn" onClick={() => copyToClipboard(generatedImagePrompt)}>
                          <PhosphorIcon className="bi bi-clipboard" /> {t('mediaStudio.copyPrompt')}
                        </button>
                        <button className="ms-copy-btn" disabled={isSaving} onClick={() => doSave(generatedImagePrompt, 'Image Generation', imageSettings.subject || 'Image Prompt')}>
                          <PhosphorIcon className="bi bi-bookmark" /> {isSaving ? 'Saving…' : 'Save'}
                        </button>
                      </div>
                    </div>
                    <pre className="ms-result-body">{generatedImagePrompt}</pre>
                  </div>
                  <QuickRefinements
                    currentText={generatedImagePrompt}
                    setter={setGeneratedImagePrompt}
                    customValue={imageCustomRefinement}
                    setCustomValue={setImageCustomRefinement}
                  />
                </>
              )}
            </div>
          </div>
        )}

        {/* ── VIDEO TAB ── */}
        {activeTab === 'video' && (
          <div className="ms-panel">
            <div className="ms-panel-header">
              <div className="ms-panel-icon ms-panel-icon--video">
                <PhosphorIcon className="bi bi-camera-video" />
              </div>
              <div>
                <div className="ms-panel-title">Video Script Generator</div>
                <div className="ms-panel-sub">Create structured, engaging scripts for any video format</div>
              </div>
              <span className="ms-badge">Free</span>
            </div>

            <div className="ms-panel-body">
              {/* Video type */}
              <div>
                <div className="ms-label">Video Type</div>
                <div className="ms-vtype-grid">
                  {VIDEO_TYPES.map(t => (
                    <button
                      key={t.value}
                      className={`ms-vtype-card${videoSettings.videoType === t.value ? ' selected' : ''}`}
                      onClick={() => setVideoSettings({ ...videoSettings, videoType: t.value })}
                    >
                      <PhosphorIcon className={`bi ${t.icon} ms-vtype-icon`} />
                      <span>{t.label}</span>
                    </button>
                  ))}
                </div>
              </div>

              {/* Topic */}
              <div>
                <div className="ms-label">{t('mediaStudio.videoTopic')} <span className="ms-required">*</span></div>
                <input className="ms-input" type="text" value={videoSettings.topic}
                  onChange={(e) => setVideoSettings({ ...videoSettings, topic: e.target.value })}
                  placeholder={t('mediaStudio.videoTopicPlaceholder')} />
                <button
                  type="button"
                  className="ms-generate-btn ms-generate-btn--video"
                  style={{ marginTop: '0.5rem' }}
                  onClick={handleChooseVideoPersona}
                  disabled={loadingMediaPersonas || !videoSettings.topic.trim()}
                >
                  {loadingMediaPersonas
                    ? <><div className="btn-spinner" /> Suggesting personas...</>
                    : <><PhosphorIcon className="bi bi-person-bounding-box" /> Choose Persona →</>}
                </button>
                {videoSelectedGoals.length > 0 && (
                  <div style={{ marginTop: '0.5rem', display: 'flex', flexWrap: 'wrap', gap: '0.4rem' }}>
                    {videoSelectedGoals.map((g, i) => (
                      <span key={i} className="ms-goal-tag">
                        <PhosphorIcon className="bi bi-check2" /> {g}
                        <button type="button" onClick={() => setVideoSelectedGoals(prev => prev.filter((_, j) => j !== i))} aria-label="Remove goal"><PhosphorIcon className="bi bi-x" /></button>
                      </span>
                    ))}
                  </div>
                )}
              </div>

              {/* Duration + Tone */}
              <div className="ms-row-2">
                <div>
                  <div className="ms-label">{t('mediaStudio.duration')}</div>
                  <input className="ms-input" type="number" value={videoSettings.duration}
                    onChange={(e) => setVideoSettings({ ...videoSettings, duration: e.target.value })}
                    placeholder="60" />
                </div>
                <div>
                  <div className="ms-label">{t('mediaStudio.tone')}</div>
                  <select className="ms-select" value={videoSettings.tone}
                    onChange={(e) => setVideoSettings({ ...videoSettings, tone: e.target.value })}>
                    <option value="professional">Professional</option>
                    <option value="casual">Casual</option>
                    <option value="enthusiastic">Enthusiastic</option>
                    <option value="educational">Educational</option>
                    <option value="humorous">Humorous</option>
                    <option value="inspirational">Inspirational</option>
                  </select>
                </div>
              </div>

              {/* Audience */}
              <div>
                <div className="ms-label">{t('mediaStudio.targetAudience')}</div>
                <input className="ms-input" type="text" value={videoSettings.targetAudience}
                  onChange={(e) => setVideoSettings({ ...videoSettings, targetAudience: e.target.value })}
                  placeholder={t('mediaStudio.targetAudiencePlaceholder')} />
              </div>

              {/* Style */}
              <div className="pg-form-group">
                <label className="pg-label">Style</label>
                <select className="ms-select" value={mediaStyle} onChange={e => setMediaStyle(e.target.value as PromptStyle)}>
                  {PROMPT_STYLES.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
                </select>
              </div>

              {/* Persona cards + Generate Goals button (video) */}
              {mediaPersonaLenses.length > 0 && activeTab === 'video' && (
                <div className="pg-form-group">
                  <label className="pg-label" style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                    <PhosphorIcon className="bi bi-person-bounding-box" style={{ color: '#833ab4' }} /> Choose a Persona
                  </label>
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(155px, 1fr))', gap: 8, marginTop: 2, marginBottom: 12 }}>
                    <button type="button" className="pg-persona-card" data-selected={mediaSelectedPersonaId === 'default'} onClick={() => setMediaSelectedPersonaId('default')}
                      style={{ padding: '9px 11px', borderRadius: 8, cursor: 'pointer', border: `1.5px solid ${mediaSelectedPersonaId === 'default' ? '#833ab4' : '#d1d5db'}`, background: mediaSelectedPersonaId === 'default' ? 'rgba(131,58,180,0.07)' : '#fff' }}>
                      <div style={{ textAlign: 'center' }}>
                        <div style={{ fontSize: '0.65rem', fontWeight: 700, color: '#833ab4', textTransform: 'uppercase', marginBottom: 2 }}>Default</div>
                        <div style={{ fontWeight: 700, fontSize: '0.8rem', color: '#000', marginBottom: 2 }}>Balanced AI</div>
                      </div>
                    </button>
                    {mediaPersonaLenses.map(lens => (
                      <button key={lens.id} type="button" className="pg-persona-card" data-selected={mediaSelectedPersonaId === lens.id} onClick={() => setMediaSelectedPersonaId(lens.id)}
                        style={{ padding: '9px 11px', borderRadius: 8, cursor: 'pointer', border: `1.5px solid ${mediaSelectedPersonaId === lens.id ? '#833ab4' : '#d1d5db'}`, background: mediaSelectedPersonaId === lens.id ? 'rgba(131,58,180,0.07)' : '#fff' }}>
                        <div style={{ textAlign: 'center' }}>
                          <div style={{ fontSize: '0.65rem', fontWeight: 700, color: '#833ab4', textTransform: 'uppercase', marginBottom: 2 }}>{lens.category}</div>
                          <div style={{ fontWeight: 700, fontSize: '0.8rem', color: '#000', marginBottom: 4 }}>{lens.title}</div>
                        </div>
                        <div style={{ fontSize: '0.72rem', color: '#555', lineHeight: 1.35, textAlign: 'left' }}>{lens.description}</div>
                      </button>
                    ))}
                  </div>
                  <button
                    type="button"
                    className="ms-generate-btn ms-generate-btn--video"
                    onClick={handleGetVideoSuggestions}
                    disabled={videoLoadingSuggestions}
                  >
                    {videoLoadingSuggestions
                      ? <><div className="btn-spinner" /> Getting goals...</>
                      : <><PhosphorIcon className="bi bi-list-check" /> Generate Goals →</>}
                  </button>
                </div>
              )}

              {/* Key points */}
              <div>
                <div className="ms-label">{t('mediaStudio.keyPoints')} <span className="ms-optional">(optional)</span></div>
                <textarea className="ms-textarea" value={videoSettings.keyPoints}
                  onChange={(e) => setVideoSettings({ ...videoSettings, keyPoints: e.target.value })}
                  placeholder={t('mediaStudio.keyPointsPlaceholder')}
                  rows={4} />
              </div>

              {videoSelectedGoals.length > 0 && (
                <button
                  className="ms-generate-btn ms-generate-btn--video"
                  onClick={() => handleGenerateVideoScript(videoSelectedGoals, videoAdditionalContext)}
                  disabled={generatingVideo}
                >
                  {generatingVideo
                    ? <><div className="btn-spinner" /> {t('mediaStudio.generating')}</>
                    : <><PhosphorIcon className="bi bi-magic" /> {t('mediaStudio.generate')}</>}
                </button>
              )}

              {generatedVideoScript && (
                <>
                  <div className="ms-result">
                    <div className="ms-result-header">
                      <span className="ms-result-title"><PhosphorIcon className="bi bi-camera-video" /> Your Video Script</span>
                      <div style={{ display: 'flex', gap: '0.5rem' }}>
                        <button className="ms-copy-btn" onClick={() => copyToClipboard(generatedVideoScript)}>
                          <PhosphorIcon className="bi bi-clipboard" /> {t('mediaStudio.copyPrompt')}
                        </button>
                        <button className="ms-copy-btn" disabled={isSaving} onClick={() => doSave(generatedVideoScript, 'Video Script', videoSettings.topic || 'Video Script')}>
                          <PhosphorIcon className="bi bi-bookmark" /> {isSaving ? 'Saving…' : 'Save'}
                        </button>
                      </div>
                    </div>
                    <div className="ms-result-body ms-result-body--prose">{generatedVideoScript}</div>
                  </div>
                  <QuickRefinements
                    currentText={generatedVideoScript}
                    setter={setGeneratedVideoScript}
                    customValue={videoCustomRefinement}
                    setCustomValue={setVideoCustomRefinement}
                  />
                </>
              )}
            </div>
          </div>
        )}

        {/* ── VOICE TAB ── */}
        {activeTab === 'voice' && (
          <div className="ms-panel">
            <div className="ms-panel-header">
              <div className="ms-panel-icon ms-panel-icon--voice">
                <PhosphorIcon className="bi bi-mic" />
              </div>
              <div>
                <div className="ms-panel-title">Voice to Prompt</div>
                <div className="ms-panel-sub">Speak your idea and we'll turn it into a structured AI prompt</div>
              </div>
              <span className="ms-badge">Free</span>
            </div>

            <div className="ms-panel-body">
              {/* Style + Persona */}
              <div className="ms-row-2" style={{ marginBottom: '1rem' }}>
                <div className="pg-form-group">
                  <label className="pg-label">Style</label>
                  <select className="ms-select" value={mediaStyle} onChange={e => setMediaStyle(e.target.value as PromptStyle)}>
                    {PROMPT_STYLES.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
                  </select>
                </div>
                <div className="pg-form-group">
                  <label className="pg-label">Persona</label>
                  <select className="ms-select" value={selectedPersonaId ?? ''} onChange={e => setSelectedPersonaId(e.target.value ? parseInt(e.target.value) : null)}>
                    <option value="">Default AI</option>
                    {personas.map(p => <option key={p.id} value={p.id}>{p.icon} {p.name}</option>)}
                  </select>
                </div>
              </div>

              <div className="ms-voice-tip">
                <PhosphorIcon className="bi bi-info-circle" />
                Record yourself describing what you want to create. We'll transcribe and refine it into a ready-to-use prompt automatically.
              </div>

              <div className="ms-voice-recorder-wrap">
                <VoiceRecorder onTranscription={handleVoiceTranscription} />
              </div>

              {processingVoice && (
                <div className="ms-processing">
                  <div className="ms-processing-spinner">
                    <div className="btn-spinner" />
                  </div>
                  <p>Processing your recording...</p>
                </div>
              )}

              {voiceTranscript && enhancedPrompt && (
                <>
                  <div className="ms-voice-results">
                    <div className="ms-result">
                      <div className="ms-result-header">
                        <span className="ms-result-title"><PhosphorIcon className="bi bi-file-text" /> Transcript</span>
                      </div>
                      <div className="ms-result-body ms-result-body--prose">{voiceTranscript}</div>
                    </div>
                    <div className="ms-result ms-result--enhanced">
                      <div className="ms-result-header ms-result-header--enhanced">
                        <span className="ms-result-title"><PhosphorIcon className="bi bi-stars" /> Enhanced Prompt</span>
                        <div style={{ display: 'flex', gap: '0.5rem' }}>
                          <button className="ms-copy-btn" onClick={() => copyToClipboard(enhancedPrompt)}>
                            <PhosphorIcon className="bi bi-clipboard" /> Copy
                          </button>
                          <button className="ms-copy-btn" disabled={isSaving} onClick={() => doSave(enhancedPrompt, 'Voice Prompt', voiceTranscript.slice(0, 60) || 'Voice Prompt')}>
                            <PhosphorIcon className="bi bi-bookmark" /> {isSaving ? 'Saving…' : 'Save'}
                          </button>
                        </div>
                      </div>
                      <div className="ms-result-body ms-result-body--prose ms-result-body--green">{enhancedPrompt}</div>
                    </div>
                  </div>
                  <QuickRefinements
                    currentText={enhancedPrompt}
                    setter={setEnhancedPrompt}
                    customValue={voiceCustomRefinement}
                    setCustomValue={setVoiceCustomRefinement}
                  />
                </>
              )}
            </div>
          </div>
        )}

        {/* Toast */}
        {toast.visible && (
          <div className="ms-toast">
            <PhosphorIcon className="bi bi-check-circle" /> {toast.message}
          </div>
        )}
      </div>

      {/* Image Goals Modal */}
      {showImageGoalsModal && (
        <div className="modal-overlay" onClick={() => !generatingImage && setShowImageGoalsModal(false)}>
          <div className="modal-content" onClick={(e) => e.stopPropagation()} style={{ maxWidth: 'min(600px, calc(100% - 1rem))' }}>
            <div className="modal-header">
              <h2><PhosphorIcon className="bi bi-list-check"></PhosphorIcon> Select Style & Composition Goals</h2>
              <button className="modal-close" onClick={() => setShowImageGoalsModal(false)} disabled={generatingImage}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>
            </div>
            <div className="modal-body">
              <p style={{ marginBottom: '1rem', color: 'var(--pg-text-light)' }}>
                {imageSelectedGoals.length} goal{imageSelectedGoals.length !== 1 ? 's' : ''} selected — these will guide your optimised prompt
              </p>
              <div className="pg-goals-list" style={{ marginBottom: '1rem' }}>
                {imageSuggestions.map((goal, i) => (
                  <label key={i} className={`pg-goal-item ${imageSelectedGoals.includes(goal) ? 'selected' : ''}`}>
                    <input type="checkbox" checked={imageSelectedGoals.includes(goal)}
                      onChange={() => setImageSelectedGoals(prev => prev.includes(goal) ? prev.filter(g => g !== goal) : [...prev, goal])}
                      disabled={generatingImage} />
                    <span className="pg-goal-number">{i + 1}.</span>
                    <span className="pg-goal-text">{goal}</span>
                  </label>
                ))}
              </div>
              <div className="pg-form-group">
                <label className="pg-label">Additional Context <span style={{ fontWeight: 400, opacity: 0.6 }}>(optional)</span></label>
                <input type="text" className="pg-input" value={imageAdditionalContext}
                  onChange={(e) => setImageAdditionalContext(e.target.value)}
                  placeholder="Any extra style details or constraints..."
                  disabled={generatingImage} />
              </div>
            </div>
            <div className="modal-footer" style={{ display: 'flex', gap: '0.5rem', justifyContent: 'flex-end' }}>
              <button type="button" className="btn btn-secondary" onClick={() => setShowImageGoalsModal(false)} disabled={generatingImage}>{t('common.cancel')}</button>
              <button
                type="button"
                className="btn btn-primary"
                disabled={imageSelectedGoals.length === 0 || generatingImage}
                onClick={async () => {
                  setShowImageGoalsModal(false)
                  await handleGenerateImagePrompt(imageSelectedGoals, imageAdditionalContext)
                }}
              >
                {generatingImage
                  ? <><span className="pg-pill-spinner"></span> {t('mediaStudio.generating')}</>
                  : <><PhosphorIcon className="bi bi-magic"></PhosphorIcon> {t('mediaStudio.generate')}</>}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Voice Goals Modal */}
      {showVoiceGoalsModal && (
        <div className="modal-overlay" onClick={() => !enhancingVoice && setShowVoiceGoalsModal(false)}>
          <div className="modal-content" onClick={(e) => e.stopPropagation()} style={{ maxWidth: 'min(600px, calc(100% - 1rem))' }}>
            <div className="modal-header">
              <h2><PhosphorIcon className="bi bi-list-check"></PhosphorIcon> Select Enhancement Goals</h2>
              <button className="modal-close" onClick={() => setShowVoiceGoalsModal(false)} disabled={enhancingVoice}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>
            </div>
            <div className="modal-body">
              <p style={{ marginBottom: '1rem', color: 'var(--pg-text-light)' }}>
                {voiceSelectedGoals.length} goal{voiceSelectedGoals.length !== 1 ? 's' : ''} selected — these will shape how your voice is structured into a prompt
              </p>
              <div className="pg-goals-list" style={{ marginBottom: '1rem' }}>
                {voiceSuggestions.map((goal, i) => (
                  <label key={i} className={`pg-goal-item ${voiceSelectedGoals.includes(goal) ? 'selected' : ''}`}>
                    <input type="checkbox" checked={voiceSelectedGoals.includes(goal)}
                      onChange={() => setVoiceSelectedGoals(prev => prev.includes(goal) ? prev.filter(g => g !== goal) : [...prev, goal])}
                      disabled={enhancingVoice} />
                    <span className="pg-goal-number">{i + 1}.</span>
                    <span className="pg-goal-text">{goal}</span>
                  </label>
                ))}
              </div>
              <div className="pg-form-group">
                <label className="pg-label">Additional Context <span style={{ fontWeight: 400, opacity: 0.6 }}>(optional)</span></label>
                <input type="text" className="pg-input" value={voiceAdditionalContext}
                  onChange={(e) => setVoiceAdditionalContext(e.target.value)}
                  placeholder="Any extra instructions or context..."
                  disabled={enhancingVoice} />
              </div>
            </div>
            <div className="modal-footer" style={{ display: 'flex', gap: '0.5rem', justifyContent: 'flex-end' }}>
              <button type="button" className="btn btn-secondary" onClick={() => setShowVoiceGoalsModal(false)} disabled={enhancingVoice}>{t('common.cancel')}</button>
              <button
                type="button"
                className="btn btn-primary"
                disabled={voiceSelectedGoals.length === 0 || enhancingVoice}
                onClick={async () => {
                  setShowVoiceGoalsModal(false)
                  await handleEnhanceVoicePrompt(voiceSelectedGoals, voiceAdditionalContext)
                }}
              >
                {enhancingVoice
                  ? <><span className="pg-pill-spinner"></span> {t('mediaStudio.generating')}</>
                  : <><PhosphorIcon className="bi bi-magic"></PhosphorIcon> {t('mediaStudio.generate')}</>}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Video Goals Modal */}
      {showVideoGoalsModal && (
        <div className="modal-overlay" onClick={() => !generatingVideo && setShowVideoGoalsModal(false)}>
          <div className="modal-content" onClick={(e) => e.stopPropagation()} style={{ maxWidth: 'min(600px, calc(100% - 1rem))' }}>
            <div className="modal-header">
              <h2><PhosphorIcon className="bi bi-list-check"></PhosphorIcon> Select Script Goals</h2>
              <button className="modal-close" onClick={() => setShowVideoGoalsModal(false)} disabled={generatingVideo}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>
            </div>
            <div className="modal-body">
              <p style={{ marginBottom: '1rem', color: 'var(--pg-text-light)' }}>
                {videoSelectedGoals.length} goal{videoSelectedGoals.length !== 1 ? 's' : ''} selected — these will shape your video script
              </p>
              <div className="pg-goals-list" style={{ marginBottom: '1rem' }}>
                {videoSuggestions.map((goal, i) => (
                  <label key={i} className={`pg-goal-item ${videoSelectedGoals.includes(goal) ? 'selected' : ''}`}>
                    <input type="checkbox" checked={videoSelectedGoals.includes(goal)}
                      onChange={() => setVideoSelectedGoals(prev => prev.includes(goal) ? prev.filter(g => g !== goal) : [...prev, goal])}
                      disabled={generatingVideo} />
                    <span className="pg-goal-number">{i + 1}.</span>
                    <span className="pg-goal-text">{goal}</span>
                  </label>
                ))}
              </div>
              <div className="pg-form-group">
                <label className="pg-label">Additional Context <span style={{ fontWeight: 400, opacity: 0.6 }}>(optional)</span></label>
                <input type="text" className="pg-input" value={videoAdditionalContext}
                  onChange={(e) => setVideoAdditionalContext(e.target.value)}
                  placeholder="Any extra instructions or context..."
                  disabled={generatingVideo} />
              </div>
            </div>
            <div className="modal-footer" style={{ display: 'flex', gap: '0.5rem', justifyContent: 'flex-end' }}>
              <button type="button" className="btn btn-secondary" onClick={() => setShowVideoGoalsModal(false)} disabled={generatingVideo}>{t('common.cancel')}</button>
              <button
                type="button"
                className="btn btn-primary"
                disabled={videoSelectedGoals.length === 0 || generatingVideo}
                onClick={async () => {
                  setShowVideoGoalsModal(false)
                  await handleGenerateVideoScript(videoSelectedGoals, videoAdditionalContext)
                }}
              >
                {generatingVideo
                  ? <><span className="pg-pill-spinner"></span> {t('mediaStudio.generating')}</>
                  : <><PhosphorIcon className="bi bi-magic"></PhosphorIcon> {t('mediaStudio.generate')}</>}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}

export default MediaStudio
