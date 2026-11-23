import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect, useMemo, useCallback } from 'react'
import { useNavigate, useSearchParams, useLocation, Link } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import ReactMarkdown from 'react-markdown'
import api from '../services/api'
import authService from '../services/authService'
import VoiceRecorder from '../components/VoiceRecorder'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import ExecutionModal from '../components/ExecutionModal'
import { getTemplatesByCategory, type MarkdownTemplate } from '../services/markdownTemplates'
import FileUpload from '../components/FileUpload'
import CustomSelect from '../components/CustomSelect'
import Tooltip from '../components/Tooltip'
import { generateSkillMarkdown, PREBUILT_SKILLS } from '../data/skills'
import OpenInAIButtons from '../components/OpenInAIButtons'
import VariantsModal from '../components/VariantsModal'
import VariableFillModal from '../components/VariableFillModal'
import SharePromptModal from '../components/SharePromptModal'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import '../App.css'

interface Persona {
  id: number
  name: string
  systemPrompt: string
  category?: string
  description?: string
  tone?: string
  expertise?: string
  constraints?: string
  icon?: string
  // Enhanced fields
  targetAudience?: string
  communicationStyle?: string
  personalityTraits?: string
  industryFocus?: string
  usageCount?: number
  createdAt: string
}

interface GenerationHistoryItem {
  id: string
  content: string
  timestamp: Date
  type: 'generated' | 'refined'
  style: string
  intent?: string
  selectedGoals?: string[]
}

type PromptStyle = 'professional' | 'creative' | 'technical' | 'conversational' | 'persuasive'
type WizardStep = 1 | 2 | 3 | 4

interface PersonaLens {
  id: string
  title: string
  category: string
  description: string
  systemPrompt: string
}

const PROMPT_STYLES: { value: PromptStyle; label: string; description: string }[] = [
  { value: 'professional', label: 'Professional', description: 'Formal, business-appropriate tone' },
  { value: 'creative', label: 'Creative', description: 'Imaginative, expressive language' },
  { value: 'technical', label: 'Technical', description: 'Precise, detailed, technical focus' },
  { value: 'conversational', label: 'Conversational', description: 'Friendly, natural dialogue style' },
  { value: 'persuasive', label: 'Persuasive', description: 'Compelling, action-oriented tone' }
]

const PERSONA_LENSES: PersonaLens[] = [
  {
    id: 'senior-engineer',
    title: 'Senior Engineer',
    category: 'Code',
    description: 'Practical implementation, clean code, tests, and maintainability.',
    systemPrompt: 'Act as a senior software engineer. Prioritize clean implementation, maintainability, readable code, focused tests, pragmatic tradeoffs, and production readiness.'
  },
  {
    id: 'principal-engineer',
    title: 'Principal Engineer',
    category: 'Code',
    description: 'Deep technical judgment, architecture pressure-testing, and long-term system health.',
    systemPrompt: 'Act as a principal engineer. Evaluate architecture, scalability, reliability, performance, developer ergonomics, risk, and long-term system consequences before recommending implementation details.'
  },
  {
    id: 'solution-architect',
    title: 'Solution Architect',
    category: 'Code',
    description: 'System design, integration boundaries, data flow, security, and deployment.',
    systemPrompt: 'Act as a solution architect. Focus on system boundaries, data flow, integration contracts, security, operations, deployment, observability, and implementation sequencing.'
  },
  {
    id: 'code-reviewer',
    title: 'Code Reviewer',
    category: 'Code',
    description: 'Finds bugs, edge cases, regressions, and missing tests before polish.',
    systemPrompt: 'Act as a rigorous code reviewer. Lead with bugs, behavioral regressions, security risks, missing tests, and maintainability concerns. Be specific and actionable.'
  },
  {
    id: 'technical-writer',
    title: 'Technical Writer',
    category: 'Writing',
    description: 'Clear documentation, structure, examples, and reader-friendly explanations.',
    systemPrompt: 'Act as a technical writer. Optimize for clarity, structure, concrete examples, accurate terminology, concise prose, and reader comprehension.'
  },
  {
    id: 'product-strategist',
    title: 'Product Strategist',
    category: 'Strategy',
    description: 'Frames user value, market fit, prioritization, metrics, and launch risk.',
    systemPrompt: 'Act as a product strategist. Focus on user value, positioning, market fit, prioritization, success metrics, constraints, and launch risks.'
  },
  {
    id: 'research-analyst',
    title: 'Research Analyst',
    category: 'Research',
    description: 'Evidence-led analysis, assumptions, sources, uncertainty, and synthesis.',
    systemPrompt: 'Act as a research analyst. Separate facts from assumptions, weigh evidence, call out uncertainty, compare alternatives, and synthesize into useful recommendations.'
  },
  {
    id: 'creative-director',
    title: 'Creative Director',
    category: 'Creative',
    description: 'Voice, originality, taste, narrative, and brand impact.',
    systemPrompt: 'Act as a creative director. Prioritize originality, voice, narrative shape, visual or brand impact, emotional resonance, and tasteful creative constraints.'
  }
]

function getRecommendedPersonaLenses(intent: string): PersonaLens[] {
  const text = intent.toLowerCase()
  const coding = /\b(code|coding|program|software|app|api|frontend|backend|bug|debug|refactor|typescript|javascript|react|c#|dotnet|python|sql|architecture|system design)\b/.test(text)
  const research = /\b(research|analyse|analyze|study|compare|evidence|report|market|competitor|sources)\b/.test(text)
  const writing = /\b(write|draft|copy|article|blog|email|doc|documentation|proposal|summary)\b/.test(text)
  const strategy = /\b(strategy|roadmap|product|launch|pricing|growth|go-to-market|business|plan)\b/.test(text)
  const creative = /\b(creative|brand|story|video|script|design|campaign|ad|visual)\b/.test(text)

  if (coding) return PERSONA_LENSES.filter(p => ['senior-engineer', 'principal-engineer', 'solution-architect', 'code-reviewer'].includes(p.id))
  if (research) return PERSONA_LENSES.filter(p => ['research-analyst', 'product-strategist', 'technical-writer'].includes(p.id))
  if (writing) return PERSONA_LENSES.filter(p => ['technical-writer', 'creative-director', 'research-analyst'].includes(p.id))
  if (strategy) return PERSONA_LENSES.filter(p => ['product-strategist', 'research-analyst', 'solution-architect'].includes(p.id))
  if (creative) return PERSONA_LENSES.filter(p => ['creative-director', 'product-strategist', 'technical-writer'].includes(p.id))
  return PERSONA_LENSES.filter(p => ['product-strategist', 'research-analyst', 'technical-writer'].includes(p.id))
}

const REFINEMENT_CATEGORIES = [
  {
    category: 'Tone',
    icon: 'bi-chat-heart',
    presets: [
      { id: 'formal', label: 'Formal', icon: 'bi-briefcase', tooltip: 'Rewrites the prompt in a professional, business-appropriate tone. Removes casual language and adds proper structure.', instruction: 'Adjust the tone to be more formal, professional, and business-appropriate. Use proper language and structure.' },
      { id: 'casual', label: 'Casual', icon: 'bi-emoji-smile', tooltip: 'Makes the prompt sound conversational and warm — like talking to a helpful colleague rather than a formal system.', instruction: 'Adjust the tone to be more conversational, friendly, and approachable. Make it feel natural and warm.' },
    ]
  },
  {
    category: 'Length',
    icon: 'bi-arrows-expand',
    presets: [
      { id: 'shorter', label: 'Shorter', icon: 'bi-arrows-collapse', tooltip: 'Trims the prompt to its essentials — removes redundancy while keeping all key instructions intact.', instruction: 'Make this prompt more concise and shorter while preserving all key instructions and intent. Remove redundancy and unnecessary words.' },
      { id: 'detailed', label: 'Detailed', icon: 'bi-text-paragraph', tooltip: 'Expands the prompt with richer context, more specific guidance, and additional depth for complex tasks.', instruction: 'Expand this prompt with more specific details, examples, and comprehensive instructions. Add clarity and depth.' },
    ]
  },
  {
    category: 'Enhancement',
    icon: 'bi-stars',
    presets: [
      { id: 'examples', label: 'Examples', icon: 'bi-lightbulb', tooltip: 'Adds 2–3 concrete input/output examples directly into the prompt so the AI understands exactly what "good" looks like.', instruction: 'Add 2-3 concrete examples of expected input/output or behavior to make the prompt clearer and more actionable.' },
      { id: 'structured', label: 'Structure', icon: 'bi-list-ol', tooltip: 'Reorganises the prompt with numbered steps, clear sections, and logical flow — easier for the AI to follow.', instruction: 'Add clear sections, numbered steps, or bullet points to organize the prompt better. Improve readability and flow.' },
      { id: 'constraints', label: 'Constraints', icon: 'bi-bullseye', tooltip: 'Adds explicit rules and guardrails — what to avoid, word limits, topic boundaries — so the AI stays on task.', instruction: 'Add specific constraints, limitations, or boundaries to make the output more focused and controlled.' },
      { id: 'edge-cases', label: 'Edge Cases', icon: 'bi-exclamation-triangle', tooltip: 'Instructs the AI how to handle ambiguous inputs, errors, and unexpected scenarios gracefully instead of guessing.', instruction: 'Add instructions for handling edge cases, errors, ambiguous inputs, and unexpected scenarios gracefully.' },
    ]
  },
  {
    category: 'Techniques',
    icon: 'bi-cpu',
    presets: [
      { id: 'self-consistency', label: 'Self-Consistency', icon: 'bi-arrow-repeat', tooltip: 'AI generates 3–5 independent answers, then cross-checks and synthesises the most consistent one. Dramatically reduces hallucinations on reasoning tasks. Based on Wang et al. (2022).', instruction: 'Restructure this prompt using the self-consistency technique: instruct the AI to generate 3–5 independent answers, then compare and synthesize the most consistent and accurate response. Add explicit reasoning steps before the final answer.' },
      { id: 'multimodal-cot', label: 'Multimodal CoT', icon: 'bi-image', tooltip: 'Combines visual and text reasoning in one chain-of-thought. Best when your prompt involves images or mixed media — AI describes visual elements before drawing conclusions. Based on Zhang et al. (2023).', instruction: 'Adapt this prompt for multimodal chain-of-thought reasoning: add instructions to consider both visual/image context and textual information together. Guide the AI to describe relevant visual elements and integrate insights from both modalities in its reasoning chain.' },
      { id: 'meta-prompting', label: 'Meta Prompting', icon: 'bi-pencil-square', tooltip: 'AI first analyses the task and designs its own optimal instruction framework, then executes it. Produces sharper outputs for open-ended or complex tasks. Based on IBM Research.', instruction: 'Apply meta-prompting: restructure so the AI first analyzes the task requirements, identifies the optimal strategy, and then generates a refined version of the instructions or a self-designed framework before executing the task.' },
      { id: 'directional-stimulus', label: 'Directional Stimulus', icon: 'bi-signpost-split', tooltip: 'Inserts carefully chosen keywords and nudges at key points to steer tone, style, and format — without over-specifying every detail. Subtle but highly effective. Based on Li et al. (2023).', instruction: 'Apply directional stimulus prompting: add specific keywords, hints, or contextual nudges at strategic points that subtly steer the AI toward the desired output characteristics (tone, style, format, approach) without being overly prescriptive. Include 2–3 carefully chosen stimulus phrases.' },
    ]
  }
]

const DRAFT_KEY = 'genprompt-draft'

function getApiErrorMessage(err: unknown, fallback: string): string {
  if (err && typeof err === 'object' && 'response' in err) {
    const response = (err as { response?: { data?: { error?: string; message?: string } } }).response
    if (response?.data?.error === 'content_violation') {
      return response.data.message || "Sorry, we can't help with that type of content."
    }
  }
  return fallback
}

function CreatePrompt() {
  const { t } = useTranslation()
  const { setBreadcrumbs } = useLayoutControls()
  const navigate = useNavigate()
  const location = useLocation()
  const [searchParams] = useSearchParams()
  const editId = searchParams.get('edit')
  const sampleParam = searchParams.get('sample')
  const locationSkill = (location.state as { skill?: import('../data/skills').Skill } | null)?.skill ?? null

  useLayoutEffect(() => {
    setBreadcrumbs([
      { label: t('nav.dashboard'), path: '/dashboard' },
      { label: editId ? t('createPrompt.editTitle') : t('createPrompt.title') }
    ])
    return () => setBreadcrumbs([])
  }, [editId])

  // Form state
  const [name, setName] = useState('')
  const [content, setContent] = useState('')
  const [category, setCategory] = useState('')
  const [difficulty, setDifficulty] = useState('')
  const [selectedPersonaId, setSelectedPersonaId] = useState<number | null>(null)
  const [selectedPersonaLensId, setSelectedPersonaLensId] = useState<string>('default')
  const [personaLenses, setPersonaLenses] = useState<PersonaLens[]>([])
  const [loadingPersonas, setLoadingPersonas] = useState(false)
  const [personas, setPersonas] = useState<Persona[]>([])
  const [error, setError] = useState<string | null>(null)

  // Intent-based AI generation
  const [intent, setIntent] = useState('')
  const [suggestions, setSuggestions] = useState<string[]>([])
  const [loadingSuggestions, setLoadingSuggestions] = useState(false)
  const [selectedGoals, setSelectedGoals] = useState<string[]>([])
  const [additionalContext, setAdditionalContext] = useState('')
  const [generatingPrompt, setGeneratingPrompt] = useState(false)
  const [isRefining, setIsRefining] = useState(false)
  const [newSuggestionsCount, setNewSuggestionsCount] = useState(0)
  const [activeSkill, setActiveSkill] = useState<import('../data/skills').Skill | null>(locationSkill)

  // Prompting mode (intent vs goal)
  const [promptingMode, setPromptingMode] = useState<'intent' | 'goal'>('intent')

  // Prompt Style
  const [promptStyle, setPromptStyle] = useState<PromptStyle>('professional')
  const [suggestedStyleLabel, setSuggestedStyleLabel] = useState<PromptStyle | null>(null)

  // Generation History
  const [generationHistory, setGenerationHistory] = useState<GenerationHistoryItem[]>([])
  const [historyIndex, setHistoryIndex] = useState(-1)
  const [showHistoryPanel, setShowHistoryPanel] = useState(false)
  const [compareMode, setCompareMode] = useState(false)
  const [compareIndex, setCompareIndex] = useState<number | null>(null)

  // Quick Refinement & Diff View
  const [quickRefining, setQuickRefining] = useState<string | null>(null)
  const [showDiffView, setShowDiffView] = useState(false)
  const [previousContent, setPreviousContent] = useState<string>('')

  // UI state
  const [showMarkdown, setShowMarkdown] = useState(false)
  const [copyConfirmed, setCopyConfirmed] = useState(false)
  const [customRefinement, setCustomRefinement] = useState('')
  const [showVariants, setShowVariants] = useState(false)
  const [showVariableFill, setShowVariableFill] = useState(false)
  const [showShare, setShowShare] = useState(false)

  // Wizard step
  const [step, setStep] = useState<WizardStep>(editId ? 4 : 1)

  // Toast
  const [toast, setToast] = useState<{ message: string; visible: boolean }>({ message: '', visible: false })

  // Execution modal
  const [showExecutionModal, setShowExecutionModal] = useState(false)

  // Markdown Templates
  const [showTemplatesModal, setShowTemplatesModal] = useState(false)
  const [previewTemplate, setPreviewTemplate] = useState<MarkdownTemplate | null>(null)

  const showToast = (message: string) => {
    setToast({ message, visible: true })
    setTimeout(() => setToast({ message: '', visible: false }), 2000)
  }

  // Content stats
  const wordCount = useMemo(() => content.trim() ? content.trim().split(/\s+/).length : 0, [content])
  const charCount = content.length
  const tokenEstimate = useMemo(() => Math.ceil(wordCount * 1.3), [wordCount])
  const fallbackPersonaLensOptions = useMemo(() => getRecommendedPersonaLenses(intent), [intent])
  const personaLensOptions = personaLenses.length > 0 ? personaLenses : fallbackPersonaLensOptions
  const selectedPersonaLens = useMemo(
    () => personaLensOptions.find(p => p.id === selectedPersonaLensId) ?? fallbackPersonaLensOptions.find(p => p.id === selectedPersonaLensId) ?? null,
    [personaLensOptions, fallbackPersonaLensOptions, selectedPersonaLensId]
  )
  const selectedPersonaInstruction = selectedPersonaId ? null : selectedPersonaLens?.systemPrompt ?? null

  // Load current user + personas
  useEffect(() => {
    authService.getUser()
    fetchPersonas()
  }, [])

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph)
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/create-prompt')
    if (handoff?.input) setIntent(handoff.input)
  }, [])

  // Load prompt for editing
  useEffect(() => {
    if (editId) {
      loadPromptForEdit(editId)
    }
  }, [editId])

  // Load sample prompt data
  useEffect(() => {
    if (sampleParam) {
      try {
        const sampleData = JSON.parse(decodeURIComponent(sampleParam))
        setName(sampleData.name || '')
        setContent(sampleData.content || '')
        setCategory(sampleData.category || '')
      } catch (err) {
        console.error('Failed to parse sample data', err)
      }
    }
  }, [sampleParam])

  // Load fork data from localStorage
  useEffect(() => {
    const forkParam = searchParams.get('fork')
    if (forkParam) {
      try {
        const raw = localStorage.getItem('forkData')
        if (raw) {
          const forkData = JSON.parse(raw)
          setName(forkData.name ? `${forkData.name} (fork)` : '')
          setContent(forkData.content || '')
          setStep(3)
          localStorage.removeItem('forkData')
        }
      } catch (err) {
        console.error('Failed to parse fork data', err)
      }
    }
  }, [searchParams])

  // Auto-save draft
  useEffect(() => {
    if (editId) return
    const timer = setTimeout(() => {
      const draft = { intent, content, name, category, promptStyle, selectedPersonaId, selectedPersonaLensId }
      localStorage.setItem(DRAFT_KEY, JSON.stringify(draft))
    }, 1000)
    return () => clearTimeout(timer)
  }, [intent, content, name, category, promptStyle, selectedPersonaId, selectedPersonaLensId, editId])

  // Clear any stale draft when opening a fresh create session
  useEffect(() => {
    if (!editId && !sampleParam) {
      localStorage.removeItem(DRAFT_KEY)
    }
  }, [])

  const loadPromptForEdit = async (id: string) => {
    try {
      const response = await api.get(`/prompts/${id}`)
      const prompt = response.data
      setName(prompt.name)
      setContent(prompt.content)
      setIntent(prompt.content)
      setCategory(prompt.category || '')
      setDifficulty(prompt.difficulty || '')
      setStep(4)
    } catch {
      setError('Failed to load prompt for editing')
    }
  }

  const fetchPersonas = async () => {
    try {
      const response = await api.get<Persona[]>('/personas')
      setPersonas(response.data)
    } catch (err) {
      console.error('Failed to fetch personas', err)
    }
  }

  const handleReviewPersonas = useCallback(async () => {
    if (!intent.trim()) {
      setError('Please describe your intent first')
      return
    }
    try {
      setLoadingPersonas(true)
      const response = await api.post<{ personas: PersonaLens[] }>('/prompts/suggest-personas', {
        intent,
        style: promptStyle,
        category: category || undefined
      })
      const aiPersonas = response.data.personas ?? []
      setPersonaLenses(aiPersonas)
      if (selectedPersonaLensId !== 'default' && !aiPersonas.some(p => p.id === selectedPersonaLensId)) {
        setSelectedPersonaLensId('default')
      }
    } catch (err) {
      console.warn('Falling back to local persona suggestions', err)
      setPersonaLenses([])
      if (selectedPersonaLensId !== 'default' && !getRecommendedPersonaLenses(intent).some(p => p.id === selectedPersonaLensId)) {
        setSelectedPersonaLensId('default')
      }
    } finally {
      setLoadingPersonas(false)
    }
    setError(null)
    setStep(2)
  }, [intent, promptStyle, category, selectedPersonaLensId])

  const doSave = useCallback(async () => {
    if (!name.trim() || !content.trim()) {
      setError('Name and content are required')
      return
    }
    try {
      if (editId) {
        await api.put(`/prompts/${editId}`, { id: editId, name, content, category, difficulty: difficulty || null })
        showToast(t('createPrompt.successUpdated'))
      } else {
        await api.post('/prompts', { name, content, category, difficulty: difficulty || null })
        showToast(t('createPrompt.successSaved'))
        localStorage.removeItem(DRAFT_KEY)
      }
      setTimeout(() => navigate('/dashboard'), 500)
    } catch {
      setError('Failed to save prompt')
    }
  }, [name, content, category, editId, navigate])

  const handleGenerateSuggestions = useCallback(async (keepSelection = false) => {
    if (!intent.trim()) {
      setError('Please describe your intent first')
      return
    }
    try {
      setIsRefining(false)
      setLoadingSuggestions(true)
      if (!keepSelection) {
        setSuggestions([])
        setSelectedGoals([])
        setAdditionalContext('')
        setNewSuggestionsCount(0)
      }
      const response = await api.post<{ goals: string[]; suggestedStyle: string }>('/prompts/generate-suggestions', {
        intent,
        style: promptStyle,
        category: category || undefined,
        mode: promptingMode,
        personaId: selectedPersonaId,
        personaInstruction: selectedPersonaInstruction
      })
      const goals = response.data.goals ?? []
      if (keepSelection) {
        const newItems = goals.filter(s => !suggestions.includes(s))
        setNewSuggestionsCount(newItems.length)
        setSuggestions(prev => [...prev, ...newItems])
      } else {
        setSuggestions(goals)
        if (response.data.suggestedStyle) {
          setPromptStyle(response.data.suggestedStyle as PromptStyle)
          setSuggestedStyleLabel(response.data.suggestedStyle as PromptStyle)
        }
      }
      setStep(3)
      setError(null)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to generate suggestions'))
    } finally {
      setLoadingSuggestions(false)
    }
  }, [intent, promptStyle, category, promptingMode, selectedPersonaId, selectedPersonaInstruction, suggestions])

  const handleRefinePrompt = useCallback(async () => {
    if (!content.trim()) {
      setError('Please generate a prompt first before refining')
      return
    }
    try {
      setIsRefining(true)
      setLoadingSuggestions(true)
      setSuggestions([])
      setSelectedGoals([])
      setAdditionalContext('')
      const response = await api.post<string[]>('/prompts/generate-refinements', {
        currentPrompt: content
      })
      setSuggestions(response.data)
      setStep(3)
      setError(null)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to generate refinements'))
    } finally {
      setLoadingSuggestions(false)
    }
  }, [content])

  const handleToggleGoal = (goal: string) => {
    setSelectedGoals(prev =>
      prev.includes(goal) ? prev.filter(g => g !== goal) : [...prev, goal]
    )
  }

  const addToHistory = (newContent: string, type: 'generated' | 'refined') => {
    const historyItem: GenerationHistoryItem = {
      id: Date.now().toString(),
      content: newContent,
      timestamp: new Date(),
      type,
      style: promptStyle,
      intent: intent || undefined,
      selectedGoals: selectedGoals.length > 0 ? [...selectedGoals] : undefined
    }
    const newHistory = historyIndex >= 0
      ? [...generationHistory.slice(0, historyIndex + 1), historyItem]
      : [...generationHistory, historyItem]
    setGenerationHistory(newHistory)
    setHistoryIndex(newHistory.length - 1)
  }

  const handleGeneratePrompt = async () => {
    if (selectedGoals.length === 0) {
      setError('Please select at least one goal')
      return
    }
    try {
      setGeneratingPrompt(true)
      if (isRefining) {
        const response = await api.post<{ prompt: string }>('/prompts/refine-prompt', {
          currentPrompt: content,
          selectedRefinements: selectedGoals,
          additionalContext: additionalContext.trim() || null,
          style: promptStyle,
          personaId: selectedPersonaId,
          personaInstruction: selectedPersonaInstruction
        })
        setContent(response.data.prompt)
        addToHistory(response.data.prompt, 'refined')
      } else {
        const response = await api.post<{ prompt: string }>('/prompts/generate-prompt', {
          intent,
          selectedGoals,
          additionalContext: activeSkill
            ? `${additionalContext.trim() ? additionalContext.trim() + '\n\n' : ''}Apply the following skill framework:\n${activeSkill.instruction}`
            : (additionalContext.trim() || null),
          style: promptStyle,
          personaId: selectedPersonaId,
          personaInstruction: selectedPersonaInstruction
        })
        setContent(response.data.prompt)
        addToHistory(response.data.prompt, 'generated')
      }
      setSelectedGoals([])
      setAdditionalContext('')
      setError(null)
      setStep(4)
    } catch (err) {
      setError(getApiErrorMessage(err, isRefining ? 'Failed to refine prompt' : 'Failed to generate prompt'))
    } finally {
      setGeneratingPrompt(false)
    }
  }

  const handleGenerateMore = async () => {
    if (isRefining) await handleRefinePrompt()
    else await handleGenerateSuggestions(true)
  }

  const handleUndo = useCallback(() => {
    if (historyIndex > 0) {
      const newIndex = historyIndex - 1
      setHistoryIndex(newIndex)
      setContent(generationHistory[newIndex].content)
    }
  }, [historyIndex, generationHistory])

  const handleRedo = useCallback(() => {
    if (historyIndex < generationHistory.length - 1) {
      const newIndex = historyIndex + 1
      setHistoryIndex(newIndex)
      setContent(generationHistory[newIndex].content)
    }
  }, [historyIndex, generationHistory])

  const handleJumpToHistory = (index: number) => {
    setHistoryIndex(index)
    setContent(generationHistory[index].content)
    if (!compareMode) setShowHistoryPanel(false)
  }

  const handleToggleCompare = (index: number) => {
    if (compareIndex === index) {
      setCompareIndex(null)
      setCompareMode(false)
    } else {
      setCompareIndex(index)
      setCompareMode(true)
    }
  }

  const handleQuickRefine = async (presetId: string) => {
    if (!content.trim()) return
    const allPresets = REFINEMENT_CATEGORIES.flatMap(c => c.presets)
    const preset = allPresets.find(p => p.id === presetId)
    if (!preset) return
    try {
      setQuickRefining(presetId)
      setPreviousContent(content)
      const response = await api.post<{ prompt: string }>('/prompts/quick-refine', {
        currentPrompt: content,
        refinementType: presetId,
        instruction: preset.instruction,
        style: promptStyle
      })
      setContent(response.data.prompt)
      addToHistory(response.data.prompt, 'refined')
      setShowDiffView(true)
      setError(null)
    } catch (err) {
      setError(getApiErrorMessage(err, `Failed to apply "${preset.label}" refinement`))
    } finally {
      setQuickRefining(null)
    }
  }

  const handleCustomRefine = async () => {
    if (!content.trim() || !customRefinement.trim()) return
    try {
      setQuickRefining('custom')
      setPreviousContent(content)
      const response = await api.post<{ prompt: string }>('/prompts/quick-refine', {
        currentPrompt: content,
        refinementType: 'custom',
        instruction: customRefinement,
        style: promptStyle
      })
      setContent(response.data.prompt)
      addToHistory(response.data.prompt, 'refined')
      setShowDiffView(true)
      setCustomRefinement('')
      setError(null)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to apply custom refinement'))
    } finally {
      setQuickRefining(null)
    }
  }

  const handleCopyPrompt = async () => {
    try {
      await navigator.clipboard.writeText(content)
    } catch {
      const ta = document.createElement('textarea')
      ta.value = content
      document.body.appendChild(ta)
      ta.select()
      document.execCommand('copy')
      document.body.removeChild(ta)
    }
    setCopyConfirmed(true)
    setTimeout(() => setCopyConfirmed(false), 2000)
  }

  const handleExport = (format: 'txt' | 'md') => {
    const blob = new Blob([content], { type: format === 'md' ? 'text/markdown' : 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${name || 'prompt'}.${format}`
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  }

  const handleDownloadSkill = () => {
    const matchedSkill = PREBUILT_SKILLS.find(s => s.name.toLowerCase() === name.toLowerCase() || s.tags.some(t => name.toLowerCase().includes(t)))
    const skillMd = generateSkillMarkdown(
      matchedSkill ?? {
        id: name.toLowerCase().replace(/\s+/g, '-'),
        name: name || 'My Skill',
        description: intent.slice(0, 120),
        category: category || 'custom',
        categoryIcon: 'bi-stars',
        icon: 'bi-stars',
        tags: category ? [category] : [],
        instruction: '',
        exampleIntent: intent,
      },
      content
    )
    const blob = new Blob([skillMd], { type: 'text/markdown' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${(name || 'skill').toLowerCase().replace(/\s+/g, '-')}-skill.md`
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    URL.revokeObjectURL(url)
  }

  const computeDiff = (oldText: string, newText: string) => {
    const oldWords = oldText.split(/(\s+)/)
    const newWords = newText.split(/(\s+)/)
    const diff: { type: 'same' | 'added' | 'removed'; text: string }[] = []
    const lcs = (a: string[], b: string[]): string[] => {
      const m = a.length, n = b.length
      const dp: number[][] = Array(m + 1).fill(null).map(() => Array(n + 1).fill(0))
      for (let i = 1; i <= m; i++)
        for (let j = 1; j <= n; j++)
          dp[i][j] = a[i-1] === b[j-1] ? dp[i-1][j-1] + 1 : Math.max(dp[i-1][j], dp[i][j-1])
      const result: string[] = []
      let i = m, j = n
      while (i > 0 && j > 0) {
        if (a[i-1] === b[j-1]) { result.unshift(a[i-1]); i--; j-- }
        else if (dp[i-1][j] > dp[i][j-1]) i--
        else j--
      }
      return result
    }
    const common = new Set(lcs(oldWords, newWords))
    let oldIdx = 0, newIdx = 0
    const commonArr = Array.from(common)
    let commonIdx = 0
    while (oldIdx < oldWords.length || newIdx < newWords.length) {
      const cur = commonArr[commonIdx]
      while (oldIdx < oldWords.length && oldWords[oldIdx] !== cur) {
        diff.push({ type: oldWords[oldIdx].trim() ? 'removed' : 'same', text: oldWords[oldIdx] }); oldIdx++
      }
      while (newIdx < newWords.length && newWords[newIdx] !== cur) {
        diff.push({ type: newWords[newIdx].trim() ? 'added' : 'same', text: newWords[newIdx] }); newIdx++
      }
      if (cur && oldIdx < oldWords.length && newIdx < newWords.length) {
        diff.push({ type: 'same', text: cur }); oldIdx++; newIdx++; commonIdx++
      }
      if (commonIdx >= commonArr.length && oldIdx >= oldWords.length && newIdx >= newWords.length) break
      if (commonIdx >= commonArr.length) {
        while (oldIdx < oldWords.length) diff.push({ type: 'removed', text: oldWords[oldIdx++] })
        while (newIdx < newWords.length) diff.push({ type: 'added', text: newWords[newIdx++] })
        break
      }
    }
    return diff
  }

  const handleCloseDiffView = () => { setShowDiffView(false); setPreviousContent('') }

  const handleRevertChanges = () => {
    if (previousContent) {
      setContent(previousContent)
      if (generationHistory.length > 0) {
        setGenerationHistory(prev => prev.slice(0, -1))
        setHistoryIndex(prev => Math.max(prev - 1, -1))
      }
    }
    handleCloseDiffView()
  }

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 's') {
        e.preventDefault()
        if (content.trim() && name.trim()) doSave()
      }
      if ((e.ctrlKey || e.metaKey) && e.key === 'z' && !e.shiftKey) {
        if (document.activeElement?.tagName !== 'TEXTAREA' && document.activeElement?.tagName !== 'INPUT') {
          e.preventDefault(); handleUndo()
        }
      }
      if ((e.ctrlKey || e.metaKey) && (e.key === 'y' || (e.key === 'z' && e.shiftKey))) {
        if (document.activeElement?.tagName !== 'TEXTAREA' && document.activeElement?.tagName !== 'INPUT') {
          e.preventDefault(); handleRedo()
        }
      }
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
        e.preventDefault()
        if (intent.trim() && !loadingSuggestions) {
          if (step === 1) handleReviewPersonas()
          else if (step === 2) handleGenerateSuggestions()
        }
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [content, name, intent, loadingSuggestions, step, doSave, handleUndo, handleRedo, handleReviewPersonas, handleGenerateSuggestions])

  // Template insertion
  const handleInsertTemplate = (template: MarkdownTemplate) => {
    setContent(template.template)
    setShowTemplatesModal(false)
    setPreviewTemplate(null)
    showToast(`Template "${template.name}" inserted`)
    addToHistory(template.template, 'generated')
  }

  const templatesByCategory = useMemo(() => getTemplatesByCategory(), [])

  return (
    <>
    <div className="playground-scope">

      {/* Error */}
      {error && (
        <div className="pg-error">
          {error}
          <button onClick={() => setError(null)}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>
        </div>
      )}

      {/* Step Indicator */}
      <div className="mobile-prompt-builder-steps" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', marginBottom: '2.5rem', paddingTop: '0.5rem' }}>
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '0.375rem', cursor: 'pointer' }} onClick={() => setStep(1)}>
          <div style={{ width: '2.25rem', height: '2.25rem', borderRadius: '50%', background: step >= 1 ? 'var(--pg-accent)' : 'var(--pg-border)', color: step >= 1 ? 'white' : 'var(--pg-text-light)', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '0.9rem', fontWeight: 600 }}>
            {step > 1 ? <PhosphorIcon className="bi bi-check-lg"></PhosphorIcon> : '1'}
          </div>
          <span style={{ fontSize: '0.75rem', color: step >= 1 ? 'var(--pg-accent)' : 'var(--pg-text-light)', fontWeight: step === 1 ? 600 : 400 }}>Describe</span>
        </div>
        <div style={{ height: '2px', width: '5rem', background: step > 1 ? 'var(--pg-accent)' : 'var(--pg-border)', margin: '0 1rem', marginBottom: '1.25rem' }} />
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '0.375rem', cursor: intent.trim() ? 'pointer' : 'default', opacity: intent.trim() ? 1 : 0.5 }} onClick={() => intent.trim() && setStep(2)}>
          <div style={{ width: '2.25rem', height: '2.25rem', borderRadius: '50%', background: step >= 2 ? 'var(--pg-accent)' : 'var(--pg-border)', color: step >= 2 ? 'white' : 'var(--pg-text-light)', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '0.9rem', fontWeight: 600 }}>
            {step > 2 ? <PhosphorIcon className="bi bi-check-lg"></PhosphorIcon> : '2'}
          </div>
          <span style={{ fontSize: '0.75rem', color: step >= 2 ? 'var(--pg-accent)' : 'var(--pg-text-light)', fontWeight: step === 2 ? 600 : 400 }}>Persona</span>
        </div>
        <div style={{ height: '2px', width: '5rem', background: step > 2 ? 'var(--pg-accent)' : 'var(--pg-border)', margin: '0 1rem', marginBottom: '1.25rem' }} />
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '0.375rem', cursor: suggestions.length > 0 ? 'pointer' : 'default', opacity: suggestions.length > 0 ? 1 : 0.5 }} onClick={() => suggestions.length > 0 && setStep(3)}>
          <div style={{ width: '2.25rem', height: '2.25rem', borderRadius: '50%', background: step >= 3 ? 'var(--pg-accent)' : 'var(--pg-border)', color: step >= 3 ? 'white' : 'var(--pg-text-light)', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '0.9rem', fontWeight: 600 }}>
            {step > 3 ? <PhosphorIcon className="bi bi-check-lg"></PhosphorIcon> : '3'}
          </div>
          <span style={{ fontSize: '0.75rem', color: step >= 3 ? 'var(--pg-accent)' : 'var(--pg-text-light)', fontWeight: step === 3 ? 600 : 400 }}>Goals</span>
        </div>
        <div style={{ height: '2px', width: '5rem', background: step > 3 ? 'var(--pg-accent)' : 'var(--pg-border)', margin: '0 1rem', marginBottom: '1.25rem' }} />
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '0.375rem', cursor: content.trim() ? 'pointer' : 'default', opacity: content.trim() ? 1 : 0.5 }} onClick={() => content.trim() && setStep(4)}>
          <div style={{ width: '2.25rem', height: '2.25rem', borderRadius: '50%', background: step >= 4 ? 'var(--pg-accent)' : 'var(--pg-border)', color: step >= 4 ? 'white' : 'var(--pg-text-light)', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '0.9rem', fontWeight: 600 }}>
            4
          </div>
          <span style={{ fontSize: '0.75rem', color: step >= 4 ? 'var(--pg-accent)' : 'var(--pg-text-light)', fontWeight: step === 4 ? 600 : 400 }}>Prompt</span>
        </div>
      </div>

      {/* Step 1: Describe */}
      {step === 1 && (
        <div className="mobile-prompt-builder-shell" style={{ maxWidth: '820px', margin: '0 auto', width: '100%', padding: '0 1.5rem', display: 'flex', flexDirection: 'column', gap: '1rem' }}>

          {/* Active skill banner */}
          {activeSkill && (
            <div className="pg-skill-banner">
              <div className="pg-skill-banner-info">
                <PhosphorIcon className={`bi ${activeSkill.icon} pg-skill-banner-icon`}></PhosphorIcon>
                <div>
                  <span className="pg-skill-banner-label">Skill active</span>
                  <span className="pg-skill-banner-name">{activeSkill.name}</span>
                </div>
              </div>
              <button className="pg-skill-banner-remove" onClick={() => setActiveSkill(null)} title="Remove skill">
                <PhosphorIcon className="bi bi-x"></PhosphorIcon>
              </button>
            </div>
          )}

          {/* Card 1: Name + Settings accordion */}
          <div className="pg-card">
            <div className="pg-form-group">
              <label className="pg-label" htmlFor="name">{t('createPrompt.promptName')}</label>
              <input id="name" type="text" className="pg-input" value={name} onChange={(e) => setName(e.target.value)} placeholder={t('createPrompt.promptNamePlaceholder')} />
              <p className="pg-input-hint">{t('createPrompt.promptNameHint')}</p>
            </div>
            <div style={{ borderTop: '1px solid var(--pg-border-light)', marginTop: '0.5rem', paddingTop: '1rem' }}>
              <div>
                <div className="pg-form-row">
                  <div className="pg-form-group">
                    <label className="pg-label" htmlFor="category">{t('createPrompt.category')}</label>
                    <input id="category" type="text" className="pg-input" value={category} onChange={(e) => setCategory(e.target.value)} placeholder={t('createPrompt.categoryPlaceholder')} />
                  </div>
                  <div className="pg-form-group">
                    <label className="pg-label" htmlFor="difficulty">Difficulty Level</label>
                    <select id="difficulty" className="pg-input" value={difficulty} onChange={(e) => setDifficulty(e.target.value)} style={{ cursor: 'pointer' }}>
                      <option value="">No level set</option>
                      <option value="beginner">Beginner</option>
                      <option value="intermediate">Intermediate</option>
                      <option value="advanced">Advanced</option>
                    </select>
                  </div>
                  <div className="pg-form-group">
                    <label className="pg-label" htmlFor="style" style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                      {t('createPrompt.style')}
                      {suggestedStyleLabel && (
                        <span style={{ fontSize: '0.7rem', background: '#fff5fb', color: '#d62976', border: '1px solid #f7a8c8', borderRadius: '10px', padding: '0.1rem 0.5rem', fontWeight: 600 }}>
                          ✦ AI suggested
                        </span>
                      )}
                    </label>
                    <CustomSelect
                      id="style"
                      value={promptStyle}
                      onChange={(v) => { setPromptStyle(v as PromptStyle); setSuggestedStyleLabel(null) }}
                      options={PROMPT_STYLES.map(s => ({ value: s.value, label: s.label, description: s.description }))}
                    />
                  </div>
                  <div className="pg-form-group">
                    <label className="pg-label" htmlFor="persona">{t('createPrompt.persona')}</label>
                    <CustomSelect
                      id="persona"
                      value={selectedPersonaId?.toString() || ''}
                      onChange={(v) => {
                        setSelectedPersonaId(v ? parseInt(v) : null)
                        if (v) setSelectedPersonaLensId('default')
                      }}
                      options={[
                        { value: '', label: t('createPrompt.defaultAI') },
                        ...personas.map(p => ({ value: p.id.toString(), label: `${p.icon || ''} ${p.name}`.trim() }))
                      ]}
                    />
                  </div>
                </div>
                {selectedPersonaId && personas.find(p => p.id === selectedPersonaId) && (() => {
                  const persona = personas.find(p => p.id === selectedPersonaId)!
                  return (
                    <div style={{ marginTop: '1rem', padding: '1rem', background: 'linear-gradient(135deg, #fff5fb, #fff5f5)', borderRadius: '8px', border: '1px solid #e0e7ff' }}>
                      <div style={{ display: 'flex', alignItems: 'center', marginBottom: '0.5rem' }}>
                        <span style={{ fontSize: '1.5rem', marginRight: '0.5rem' }}>{persona.icon}</span>
                        <strong style={{ fontSize: '0.95rem' }}>{persona.name}</strong>
                        <span style={{ marginLeft: 'auto', fontSize: '0.75rem', color: '#64748b' }}>
                          <PhosphorIcon className="bi bi-graph-up"></PhosphorIcon> Used {persona.usageCount} times
                        </span>
                      </div>
                      {persona.description && (
                        <p style={{ fontSize: '0.85rem', color: '#475569', margin: '0.5rem 0', lineHeight: '1.4' }}>{persona.description}</p>
                      )}
                      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.5rem', marginTop: '0.5rem' }}>
                        {persona.tone && (
                          <span style={{ fontSize: '0.75rem', padding: '0.25rem 0.5rem', background: 'white', borderRadius: '4px', color: '#833ab4' }}>
                            <PhosphorIcon className="bi bi-chat-heart"></PhosphorIcon> {persona.tone}
                          </span>
                        )}
                        {persona.expertise && (
                          <span style={{ fontSize: '0.75rem', padding: '0.25rem 0.5rem', background: 'white', borderRadius: '4px', color: '#833ab4' }}>
                            <PhosphorIcon className="bi bi-stars"></PhosphorIcon> {persona.expertise.split(',')[0].trim()}
                            {persona.expertise.split(',').length > 1 && ` +${persona.expertise.split(',').length - 1}`}
                          </span>
                        )}
                        {persona.targetAudience && (
                          <span style={{ fontSize: '0.75rem', padding: '0.25rem 0.5rem', background: 'white', borderRadius: '4px', color: '#ec4899' }}>
                            <PhosphorIcon className="bi bi-people"></PhosphorIcon> {persona.targetAudience}
                          </span>
                        )}
                      </div>
                    </div>
                  )
                })()}
              </div>
            </div>
          </div>

          {/* Card 2: Intent */}
          <div className="pg-card">
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: '0.75rem' }}>
              <h3 className="pg-card-title" style={{ margin: 0 }}><PhosphorIcon className="bi bi-chat-text"></PhosphorIcon> {t('createPrompt.describeIntent')}</h3>
              <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                <label style={{ fontSize: '0.78rem', color: 'var(--pg-text-light)', whiteSpace: 'nowrap' }}>Suggestion mode:</label>
                <CustomSelect
                  className="cs-auto"
                  value={promptingMode}
                  onChange={(v) => setPromptingMode(v as 'intent' | 'goal')}
                  options={[
                    { value: 'intent', label: 'Intent', description: 'What the AI should do' },
                    { value: 'goal', label: 'Goal', description: 'Outcomes to achieve' }
                  ]}
                />
              </div>
            </div>
            <div className="pg-intent-wrapper">
              <textarea
                id="intent"
                className="pg-textarea"
                value={intent}
                onChange={(e) => {
                  setIntent(e.target.value)
                  setPersonaLenses([])
                  setSelectedPersonaLensId('default')
                }}
                placeholder={t('createPrompt.intentPlaceholder')}
                rows={3}
              />
              <VoiceRecorder
                onTranscription={(_text, refinedIntent) => setIntent(refinedIntent || _text)}
                onError={(err) => setError(err)}
              />
            </div>
            <button
              type="button"
              className="pg-btn pg-btn-generate"
              onClick={handleReviewPersonas}
              disabled={!intent.trim() || loadingSuggestions || loadingPersonas}
              style={{ marginTop: '0.75rem', width: '100%' }}
            >
              {loadingSuggestions || loadingPersonas ? (
                <><span className="pg-pill-spinner"></span> Finding personas...</>
              ) : (
                <><PhosphorIcon className="bi bi-person-bounding-box"></PhosphorIcon> Choose Persona &rarr;</>
              )}
            </button>
            <p style={{ fontSize: '0.7rem', color: 'var(--pg-text-light)', marginTop: '0.375rem' }}>
              {t('createPrompt.pressToGenerate', { key: 'Ctrl+Enter' })}
            </p>
          </div>

          {content.trim() && (
            <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
              <button type="button" className="pg-btn pg-btn-secondary" onClick={() => setStep(4)} style={{ fontSize: '0.8rem' }}>
                Skip to Prompt &rarr;
              </button>
            </div>
          )}
        </div>
      )}

      {/* Step 2: Persona */}
      {step === 2 && (
        <div className="mobile-prompt-builder-shell" style={{ maxWidth: '920px', margin: '0 auto', width: '100%', padding: '0 1.5rem', display: 'flex', flexDirection: 'column', gap: '1rem' }}>
          <div className="pg-card">
            <div className="pg-persona-gate-head">
              <div>
                <span className="pg-persona-gate-kicker">Choose the working lens</span>
                <h3 className="pg-card-title" style={{ marginTop: '0.25rem' }}>
                  <PhosphorIcon className="bi bi-person-bounding-box"></PhosphorIcon> How should AI approach this?
                </h3>
              </div>
              <button type="button" className="pg-btn pg-btn-secondary" onClick={() => {
                setSelectedPersonaId(null)
                setSelectedPersonaLensId('default')
              }}>
                Default AI
              </button>
            </div>
            <p style={{ margin: '0 0 1rem', color: 'var(--pg-text-light)', fontSize: '0.875rem', lineHeight: 1.55 }}>
              Pick a persona before goals are generated. These options are suggested by AI from your exact intent, with local fallbacks if the service is unavailable.
            </p>

            {personas.length > 0 && (
              <div className="pg-form-group" style={{ marginBottom: '1rem' }}>
                <label className="pg-label" htmlFor="persona-review">Use one of your saved personas</label>
                <CustomSelect
                  id="persona-review"
                  value={selectedPersonaId?.toString() || ''}
                  onChange={(v) => {
                    setSelectedPersonaId(v ? parseInt(v) : null)
                    if (v) setSelectedPersonaLensId('default')
                  }}
                  options={[
                    { value: '', label: 'No saved persona' },
                    ...personas.map(p => ({ value: p.id.toString(), label: `${p.icon || ''} ${p.name}`.trim(), description: p.description || p.expertise || undefined }))
                  ]}
                />
              </div>
            )}

            <div className="pg-persona-lens-grid">
              <button
                type="button"
                className={`pg-persona-lens ${!selectedPersonaId && selectedPersonaLensId === 'default' ? 'selected' : ''}`}
                onClick={() => {
                  setSelectedPersonaId(null)
                  setSelectedPersonaLensId('default')
                }}
              >
                <span>Default AI</span>
                <strong>Balanced assistant</strong>
                <p>Let the goal generator infer the right style and depth from your intent.</p>
              </button>
              {personaLensOptions.map(lens => (
                <button
                  type="button"
                  key={lens.id}
                  className={`pg-persona-lens ${!selectedPersonaId && selectedPersonaLensId === lens.id ? 'selected' : ''}`}
                  onClick={() => {
                    setSelectedPersonaId(null)
                    setSelectedPersonaLensId(lens.id)
                  }}
                >
                  <span>{lens.category}</span>
                  <strong>{lens.title}</strong>
                  <p>{lens.description}</p>
                </button>
              ))}
            </div>

            {(selectedPersonaId || selectedPersonaLens) && (
              <div className="pg-persona-selection-note">
                <PhosphorIcon className="bi bi-stars"></PhosphorIcon>
                {selectedPersonaId
                  ? `Goals will be generated through "${personas.find(p => p.id === selectedPersonaId)?.name ?? 'selected persona'}".`
                  : selectedPersonaLens
                    ? `Goals will be generated through a ${selectedPersonaLens.title} lens.`
                    : 'Goals will be generated with the default assistant.'}
              </div>
            )}

            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: '1.25rem', flexWrap: 'wrap', gap: '0.5rem' }}>
              <button type="button" className="pg-btn pg-btn-secondary" onClick={() => setStep(1)}>
                <PhosphorIcon className="bi bi-arrow-left"></PhosphorIcon> Back
              </button>
              <button type="button" className="pg-btn pg-btn-generate" onClick={() => handleGenerateSuggestions()} disabled={loadingSuggestions}>
                {loadingSuggestions
                  ? <><span className="pg-pill-spinner"></span> Generating goals...</>
                  : <><PhosphorIcon className="bi bi-list-check"></PhosphorIcon> Generate Goals &rarr;</>}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Step 3: Goals */}
      {step === 3 && (
        <div className="mobile-prompt-builder-shell" style={{ maxWidth: '820px', margin: '0 auto', width: '100%', padding: '0 1.5rem' }}>
          <div className="pg-card">
            <h3 className="pg-card-title">
              <PhosphorIcon className="bi bi-list-check"></PhosphorIcon> {isRefining ? 'Select Refinement Options' : promptingMode === 'intent' ? 'Select Your Intents' : 'Select Your Goals'}
            </h3>
            <p style={{ marginBottom: '1rem', color: 'var(--pg-text-light)', fontSize: '0.875rem' }}>
              {t('goalsModal.selectedCount', { count: selectedGoals.length })}
              {newSuggestionsCount > 0 && !loadingSuggestions && (
                <span style={{ marginLeft: '0.5rem', color: 'var(--pg-accent)', fontWeight: 600 }}>+{newSuggestionsCount} new</span>
              )}
            </p>
            <div className="pg-goals-list">
              {suggestions.map((goal, index) => (
                <label key={goal} className={`pg-goal-item ${selectedGoals.includes(goal) ? 'selected' : ''}`}>
                  <input type="checkbox" checked={selectedGoals.includes(goal)} onChange={() => handleToggleGoal(goal)} disabled={generatingPrompt} />
                  <span className="pg-goal-number">{index + 1}.</span>
                  <span className="pg-goal-text">{goal}</span>
                </label>
              ))}
            </div>
            <div className="pg-form-group" style={{ marginTop: '1rem' }}>
              <label className="pg-label">{t('goalsModal.additionalContext')}</label>
              <input
                type="text"
                className="pg-input"
                value={additionalContext}
                onChange={(e) => setAdditionalContext(e.target.value)}
                placeholder={t('createPrompt.additionalContextPlaceholder')}
                disabled={generatingPrompt}
              />
            </div>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: '1.25rem', flexWrap: 'wrap', gap: '0.5rem' }}>
              <div style={{ display: 'flex', gap: '0.5rem' }}>
                <button type="button" className="pg-btn pg-btn-secondary" onClick={() => isRefining ? setStep(4) : setStep(2)}>
                  <PhosphorIcon className="bi bi-arrow-left"></PhosphorIcon> Back
                </button>
                <button type="button" className="btn btn-secondary" onClick={handleGenerateMore} disabled={loadingSuggestions || generatingPrompt}>
                  {loadingSuggestions
                    ? <><span className="pg-pill-spinner"></span> Generating...</>
                    : <><PhosphorIcon className="bi bi-arrow-repeat"></PhosphorIcon> More</>}
                </button>
              </div>
              <button type="button" className="pg-btn pg-btn-generate" onClick={handleGeneratePrompt} disabled={selectedGoals.length === 0 || generatingPrompt}>
                {generatingPrompt
                  ? <><span className="pg-pill-spinner"></span> Generating...</>
                  : <><PhosphorIcon className="bi bi-magic"></PhosphorIcon> {isRefining ? 'Apply Refinements' : 'Generate Prompt'} &rarr;</>}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Step 4: Your Prompt */}
      {step === 4 && (
        <div className="mobile-prompt-builder-shell" style={{ maxWidth: '1100px', margin: '0 auto', width: '100%', padding: '0 1.5rem', display: 'flex', flexDirection: 'column', gap: '1rem' }}>
          <div className="pg-card">
            <div className="pg-content-toolbar">
              <label className="pg-label" style={{ margin: 0 }}>{t('createPrompt.promptContent')}</label>
              <div className="pg-content-actions">
                {/* Group A: Undo/Redo */}
                {generationHistory.length > 0 && (
                  <div className="pg-toolbar-group">
                    <button type="button" className="pg-btn-icon" onClick={handleUndo} disabled={historyIndex <= 0} title="Undo (Ctrl+Z)">
                      <PhosphorIcon className="bi bi-arrow-counterclockwise"></PhosphorIcon>
                    </button>
                    <button type="button" className="pg-btn-icon" onClick={handleRedo} disabled={historyIndex >= generationHistory.length - 1} title="Redo (Ctrl+Y)">
                      <PhosphorIcon className="bi bi-arrow-clockwise"></PhosphorIcon>
                    </button>
                  </div>
                )}

                {/* Group C: Edit/Preview toggle */}
                <div className="pg-toggle-group">
                  <button type="button" className={`pg-toggle-btn ${!showMarkdown ? 'active' : ''}`} onClick={() => setShowMarkdown(false)}>
                    <PhosphorIcon className="bi bi-code"></PhosphorIcon> Edit
                  </button>
                  <button type="button" className={`pg-toggle-btn ${showMarkdown ? 'active' : ''}`} onClick={() => setShowMarkdown(true)}>
                    <PhosphorIcon className="bi bi-eye"></PhosphorIcon> Preview
                  </button>
                </div>

                {/* Copy + Share inline */}
                {content.trim() && (
                  <>
                    <div className="pg-action-bar-sep" />
                    <button type="button" className="pg-btn pg-btn-outline pg-btn-sm" onClick={handleCopyPrompt}>
                      <PhosphorIcon className={copyConfirmed ? 'bi bi-check-lg' : 'bi bi-clipboard'}></PhosphorIcon> {copyConfirmed ? 'Copied!' : 'Copy'}
                    </button>
                    <button type="button" className="pg-btn pg-btn-outline pg-btn-sm" onClick={() => setShowShare(true)}>
                      <PhosphorIcon className="bi bi-share"></PhosphorIcon> Share
                    </button>
                  </>
                )}
              </div>
            </div>

            {/* Empty state */}
            {!content.trim() && !generatingPrompt && !showDiffView && (
              <div className="pg-empty-state">
                <PhosphorIcon className="bi bi-chat-square-text"></PhosphorIcon>
                <h3>Your prompt will appear here</h3>
                <p>Describe your intent, select goals, and generate.</p>
                <div className="pg-shortcuts-hints">
                  <span><kbd>Ctrl</kbd>+<kbd>Enter</kbd> generate</span>
                  <span><kbd>Ctrl</kbd>+<kbd>S</kbd> save</span>
                </div>
              </div>
            )}

            {/* Generating skeleton */}
            {generatingPrompt && !content.trim() && (
              <div className="pg-skeleton-block" style={{ marginTop: '0.5rem' }} />
            )}

            {/* Diff View (inline, replaces editor) */}
            {showDiffView && previousContent ? (
              <>
                <div className="pg-diff-view" style={{ border: 'none', borderRadius: 0 }}>
                  <div className="pg-diff-header">
                    <h4><PhosphorIcon className="bi bi-file-diff"></PhosphorIcon> Changes Made</h4>
                    <div className="pg-diff-actions">
                      <button type="button" className="pg-btn pg-btn-secondary pg-btn-sm" onClick={handleRevertChanges}>
                        <PhosphorIcon className="bi bi-arrow-counterclockwise"></PhosphorIcon> Revert
                      </button>
                      <button type="button" className="pg-btn pg-btn-accent pg-btn-sm" onClick={handleCloseDiffView}>
                        <PhosphorIcon className="bi bi-check"></PhosphorIcon> Accept
                      </button>
                    </div>
                  </div>
                  <div className="pg-diff-content">
                    {computeDiff(previousContent, content).map((part, i) => (
                      <span key={i} className={`pg-diff-${part.type}`}>{part.text}</span>
                    ))}
                  </div>
                </div>
                {compareMode && compareIndex !== null && (
                  <div className="pg-compare-view" style={{ border: 'none', borderTop: '1px solid var(--pg-border-light)', borderRadius: 0 }}>
                    <div className="pg-compare-header">
                      <h4><PhosphorIcon className="bi bi-columns-gap"></PhosphorIcon> Compare Versions</h4>
                      <button type="button" className="pg-btn pg-btn-secondary pg-btn-sm" onClick={() => { setCompareMode(false); setCompareIndex(null) }}>
                        <PhosphorIcon className="bi bi-x"></PhosphorIcon> Close
                      </button>
                    </div>
                    <div className="pg-compare-panels">
                      <div className="pg-compare-panel"><h5>Selected Version</h5><div className="pg-compare-content">{generationHistory[compareIndex].content}</div></div>
                      <div className="pg-compare-panel"><h5>Current Version</h5><div className="pg-compare-content">{content}</div></div>
                    </div>
                  </div>
                )}
              </>
            ) : (
              content.trim() ? (
                showMarkdown ? (
                  <div className="pg-markdown-preview"><ReactMarkdown>{content}</ReactMarkdown></div>
                ) : (
                  <>
                    <textarea
                      id="content"
                      className="pg-textarea"
                      value={content}
                      onChange={(e) => setContent(e.target.value)}
                      placeholder="Your AI-generated prompt will appear here, or write your own..."
                      rows={10}
                      style={{ borderRadius: 'var(--pg-radius-sm) var(--pg-radius-sm) 0 0', marginBottom: 0 }}
                    />
                    <div className="pg-editor-footer">
                      <span>{charCount} chars</span>
                      <span style={{ color: 'var(--pg-border)' }}>|</span>
                      <span>{wordCount} words</span>
                      <span style={{ color: 'var(--pg-border)' }}>|</span>
                      <span>~{tokenEstimate} tokens</span>
                    </div>
                  </>
                )
              ) : null
            )}

            {/* Compare View (when no diff active) */}
            {!showDiffView && compareMode && compareIndex !== null && (
              <div className="pg-compare-view" style={{ border: 'none', borderTop: '1px solid var(--pg-border-light)', borderRadius: 0 }}>
                <div className="pg-compare-header">
                  <h4><PhosphorIcon className="bi bi-columns-gap"></PhosphorIcon> Compare Versions</h4>
                  <button type="button" className="pg-btn pg-btn-secondary pg-btn-sm" onClick={() => { setCompareMode(false); setCompareIndex(null) }}>
                    <PhosphorIcon className="bi bi-x"></PhosphorIcon> Close
                  </button>
                </div>
                <div className="pg-compare-panels">
                  <div className="pg-compare-panel"><h5>Selected Version</h5><div className="pg-compare-content">{generationHistory[compareIndex].content}</div></div>
                  <div className="pg-compare-panel"><h5>Current Version</h5><div className="pg-compare-content">{content}</div></div>
                </div>
              </div>
            )}

            {/* Contextual Action Bar */}
            {content.trim() && !showDiffView && (
              <div className="pg-action-bar">
                <OpenInAIButtons content={content} size="sm" />
                <div className="pg-action-bar-sep" />
                <button type="button" className="pg-btn pg-btn-outline pg-btn-sm" onClick={() => setShowVariants(true)}>
                  <PhosphorIcon className="bi bi-layout-three-columns"></PhosphorIcon> Variants
                </button>
                {/\{\{[^}]+\}\}/.test(content) && (
                  <button type="button" className="pg-btn pg-btn-outline pg-btn-sm" onClick={() => setShowVariableFill(true)}>
                    <PhosphorIcon className="bi bi-input-cursor-text"></PhosphorIcon> Fill Variables
                  </button>
                )}
                <div className="pg-action-bar-sep" />
                <button type="button" className="pg-btn pg-btn-outline pg-btn-sm" onClick={() => handleExport('txt')}>
                  <PhosphorIcon className="bi bi-file-earmark-text"></PhosphorIcon> Export .txt
                </button>
                <button type="button" className="pg-btn pg-btn-outline pg-btn-sm" onClick={() => handleExport('md')}>
                  <PhosphorIcon className="bi bi-markdown"></PhosphorIcon> Export .md
                </button>
                <div className="pg-action-bar-sep" />
                <button type="button" className="pg-btn pg-btn-outline pg-btn-sm" onClick={() => setShowTemplatesModal(true)}>
                  <PhosphorIcon className="bi bi-file-earmark-code"></PhosphorIcon> Templates
                </button>
                {generationHistory.length > 0 && (
                  <button type="button" className="pg-btn pg-btn-outline pg-btn-sm" onClick={() => setShowHistoryPanel(true)}>
                    <PhosphorIcon className="bi bi-clock-history"></PhosphorIcon> History ({generationHistory.length})
                  </button>
                )}
                <div className="pg-action-bar-sep" />
                <button type="button" className="pg-btn pg-btn-outline pg-btn-sm" onClick={handleDownloadSkill}>
                  <PhosphorIcon className="bi bi-lightning-charge"></PhosphorIcon> Download Skill
                </button>
              </div>
            )}

            {/* Quick Refinements (always visible) */}
            {content.trim() && (
              <div className="pg-refine-section">
                <label className="pg-refinements-label"><PhosphorIcon className="bi bi-sliders"></PhosphorIcon> {t('createPrompt.quickRefine')}</label>
                {REFINEMENT_CATEGORIES.map(cat => (
                  <div key={cat.category} className="pg-refinement-category">
                    <span className="pg-refinement-category-label">
                      <PhosphorIcon className={`bi ${cat.icon}`}></PhosphorIcon> {cat.category}
                      {cat.category === 'Techniques' && (
                        <span style={{ fontSize: '0.65rem', color: 'var(--pg-text-light)', fontWeight: 400, marginLeft: '0.4rem', opacity: 0.7 }}>
                          · hover for details
                        </span>
                      )}
                    </span>
                    <div className="pg-refinement-pills">
                      {cat.presets.map(preset => (
                        <Tooltip key={preset.id} text={preset.tooltip ?? ''}>
                          <button
                            type="button"
                            className={`pg-refinement-pill ${quickRefining === preset.id ? 'loading' : ''}`}
                            onClick={() => handleQuickRefine(preset.id)}
                            disabled={quickRefining !== null}
                          >
                            <PhosphorIcon className={`bi ${preset.icon}`}></PhosphorIcon> {preset.label}
                            {quickRefining === preset.id && <span className="pg-pill-spinner"></span>}
                          </button>
                        </Tooltip>
                      ))}
                    </div>
                  </div>
                ))}
                <div className="pg-custom-refine">
                  <div className="pg-custom-refine-row">
                    <input
                      type="text"
                      className="pg-custom-refine-input"
                      value={customRefinement}
                      onChange={(e) => setCustomRefinement(e.target.value)}
                      placeholder="Custom refinement instruction..."
                      onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); handleCustomRefine() } }}
                    />
                    <button
                      type="button"
                      className="pg-btn pg-btn-accent pg-btn-sm"
                      onClick={handleCustomRefine}
                      disabled={!customRefinement.trim() || quickRefining !== null}
                    >
                      <PhosphorIcon className="bi bi-send"></PhosphorIcon> Apply
                    </button>
                  </div>
                </div>
              </div>
            )}
          </div>

          {/* Action bar */}
          <div className="pg-panel-actions">
            <button type="button" className="pg-btn pg-btn-secondary" onClick={() => setStep(3)}>
              <PhosphorIcon className="bi bi-arrow-left"></PhosphorIcon> Back
            </button>
            <button type="button" className="pg-btn pg-btn-secondary" onClick={() => setStep(1)}>
              Start Over
            </button>
            {content.trim() && (
              <button type="button" className="pg-btn pg-btn-secondary" onClick={handleRefinePrompt} disabled={loadingSuggestions}>
                <PhosphorIcon className="bi bi-lightbulb"></PhosphorIcon> Refine
              </button>
            )}
            {content.trim() && (
              <button type="button" className="pg-btn pg-btn-run" onClick={() => setShowExecutionModal(true)}>
                <PhosphorIcon className="bi bi-play-fill"></PhosphorIcon> Run
              </button>
            )}
            <div style={{ flex: 1 }}></div>
            <Link to="/dashboard" className="pg-btn pg-btn-secondary">
              {t('common.cancel')}
            </Link>
            <button type="button" className="pg-btn pg-btn-accent" onClick={doSave} disabled={!content.trim() || !name.trim()}>
              <PhosphorIcon className="bi bi-floppy"></PhosphorIcon> {editId ? t('common.update') : t('common.save')}
            </button>
          </div>

          {/* Context Files */}
          {editId && (
            <FileUpload
              entityType="prompt"
              entityId={editId}
            />
          )}

          {/* History Slide-Over */}
          {showHistoryPanel && generationHistory.length > 0 && (
            <>
              <div className="pg-overlay" onClick={() => setShowHistoryPanel(false)}></div>
              <div className="pg-slide-over">
                <div className="pg-history-header">
                  <h3><PhosphorIcon className="bi bi-clock-history"></PhosphorIcon> History</h3>
                  <button type="button" className="pg-btn-icon" onClick={() => setShowHistoryPanel(false)}>
                    <PhosphorIcon className="bi bi-x"></PhosphorIcon>
                  </button>
                </div>
                <div className="pg-history-list">
                  {generationHistory.map((item, index) => (
                    <div key={item.id} className={`pg-history-item ${index === historyIndex ? 'active' : ''} ${compareIndex === index ? 'comparing' : ''}`}>
                      <div className="pg-history-item-header">
                        <span className="pg-history-type">
                          <PhosphorIcon className={`bi ${item.type === 'generated' ? 'bi-lightning' : 'bi-pencil'}`}></PhosphorIcon>
                          {item.type === 'generated' ? 'Generated' : 'Refined'}
                        </span>
                        <span className="pg-history-time">{new Date(item.timestamp).toLocaleTimeString()}</span>
                      </div>
                      <div className="pg-history-preview">{item.content.substring(0, 120)}...</div>
                      <div className="pg-history-actions">
                        <button type="button" className="pg-btn pg-btn-secondary pg-btn-sm" onClick={() => handleJumpToHistory(index)}>
                          <PhosphorIcon className="bi bi-check"></PhosphorIcon> Use
                        </button>
                        <button type="button" className={`pg-btn pg-btn-sm ${compareIndex === index ? 'pg-btn-accent' : 'pg-btn-secondary'}`} onClick={() => handleToggleCompare(index)}>
                          <PhosphorIcon className="bi bi-columns-gap"></PhosphorIcon> Compare
                        </button>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </>
          )}
        </div>
      )}

      {/* Markdown Templates Modal */}
      {showTemplatesModal && (
        <div className="modal-overlay" onClick={() => setShowTemplatesModal(false)}>
          <div className="modal-content modal-wide" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h3><PhosphorIcon className="bi bi-file-earmark-text"></PhosphorIcon> Markdown Templates</h3>
              <button type="button" className="btn-close" onClick={() => setShowTemplatesModal(false)}>
                <PhosphorIcon className="bi bi-x-lg"></PhosphorIcon>
              </button>
            </div>
            <div className="modal-body" style={{ maxHeight: '70vh', overflow: 'auto', padding: '1.5rem' }}>
              {!previewTemplate ? (
                <div style={{ display: 'flex', flexDirection: 'column', gap: '1.5rem' }}>
                  {Object.entries(templatesByCategory).map(([category, templates]) => (
                    <div key={category}>
                      <h4 style={{ 
                        margin: '0 0 1rem 0', 
                        fontSize: '1rem', 
                        color: 'var(--pg-text)',
                        borderBottom: '2px solid var(--pg-accent)',
                        paddingBottom: '0.5rem'
                      }}>
                        {category}
                      </h4>
                      <div style={{ 
                        display: 'grid', 
                        gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))', 
                        gap: '1rem' 
                      }}>
                        {templates.map((template) => (
                          <div 
                            key={template.id}
                            style={{
                              padding: '1rem',
                              border: '1px solid var(--pg-border)',
                              borderRadius: '8px',
                              cursor: 'pointer',
                              transition: 'all 0.2s',
                              backgroundColor: 'var(--pg-bg-secondary)'
                            }}
                            onMouseEnter={(e) => {
                              e.currentTarget.style.borderColor = 'var(--pg-accent)'
                              e.currentTarget.style.transform = 'translateY(-2px)'
                              e.currentTarget.style.boxShadow = '0 4px 12px rgba(0,0,0,0.1)'
                            }}
                            onMouseLeave={(e) => {
                              e.currentTarget.style.borderColor = 'var(--pg-border)'
                              e.currentTarget.style.transform = 'none'
                              e.currentTarget.style.boxShadow = 'none'
                            }}
                            onClick={() => setPreviewTemplate(template)}
                          >
                            <div style={{ 
                              fontSize: '2rem', 
                              marginBottom: '0.5rem',
                              textAlign: 'center'
                            }}>
                              {template.icon}
                            </div>
                            <h5 style={{ 
                              margin: '0 0 0.5rem 0', 
                              fontSize: '0.95rem',
                              color: 'var(--pg-text)',
                              fontWeight: 600
                            }}>
                              {template.name}
                            </h5>
                            <p style={{ 
                              margin: 0, 
                              fontSize: '0.8rem',
                              color: 'var(--pg-text-light)',
                              lineHeight: 1.4
                            }}>
                              {template.description}
                            </p>
                          </div>
                        ))}
                      </div>
                    </div>
                  ))}
                </div>
              ) : (
                <div style={{ display: 'flex', flexDirection: 'column', gap: '1rem' }}>
                  <button 
                    type="button" 
                    className="pg-btn pg-btn-outline pg-btn-sm"
                    onClick={() => setPreviewTemplate(null)}
                    style={{ alignSelf: 'flex-start' }}
                  >
                    <PhosphorIcon className="bi bi-arrow-left"></PhosphorIcon> Back to Templates
                  </button>
                  <div style={{ 
                    padding: '1rem', 
                    backgroundColor: 'var(--pg-bg-secondary)', 
                    borderRadius: '8px',
                    marginBottom: '1rem'
                  }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: '0.75rem', marginBottom: '0.5rem' }}>
                      <span style={{ fontSize: '2rem' }}>{previewTemplate.icon}</span>
                      <div>
                        <h4 style={{ margin: 0, fontSize: '1.1rem', color: 'var(--pg-text)' }}>
                          {previewTemplate.name}
                        </h4>
                        <p style={{ margin: '0.25rem 0 0 0', fontSize: '0.85rem', color: 'var(--pg-text-light)' }}>
                          {previewTemplate.description}
                        </p>
                      </div>
                    </div>
                  </div>
                  <div style={{ 
                    backgroundColor: 'var(--pg-bg)',
                    border: '1px solid var(--pg-border)',
                    borderRadius: '8px',
                    padding: '1rem',
                    maxHeight: '50vh',
                    overflow: 'auto'
                  }}>
                    <pre style={{ 
                      margin: 0, 
                      fontSize: '0.85rem',
                      lineHeight: 1.6,
                      whiteSpace: 'pre-wrap',
                      wordBreak: 'break-word',
                      color: 'var(--pg-text)'
                    }}>
                      {previewTemplate.template}
                    </pre>
                  </div>
                  <button 
                    type="button"
                    className="pg-btn pg-btn-primary"
                    onClick={() => handleInsertTemplate(previewTemplate)}
                    style={{ alignSelf: 'flex-end' }}
                  >
                    <PhosphorIcon className="bi bi-plus-circle"></PhosphorIcon> Insert Template
                  </button>
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Toast */}
      {toast.visible && <div className="pg-toast">{toast.message}</div>}

      {/* Execution Modal */}
      {showExecutionModal && (
        <ExecutionModal
          isOpen={showExecutionModal}
          onClose={() => setShowExecutionModal(false)}
          prompt={{
            id: editId || 'unsaved',
            name: name || 'Untitled Prompt',
            content: content,
            isTemplate: content.includes('{{'),
          }}
        />
      )}
    </div>
      <VariantsModal
        isOpen={showVariants}
        onClose={() => setShowVariants(false)}
        onUseVariant={(text) => setContent(text)}
        request={{ intent, selectedGoals, additionalContext, style: promptStyle, personaId: selectedPersonaId ?? undefined }}
      />
      <VariableFillModal
        isOpen={showVariableFill}
        content={content}
        onClose={() => setShowVariableFill(false)}
        onApply={(filled) => setContent(filled)}
      />
      <SharePromptModal
        isOpen={showShare}
        content={content}
        onClose={() => setShowShare(false)}
      />
    </>
  )
}

export default CreatePrompt
