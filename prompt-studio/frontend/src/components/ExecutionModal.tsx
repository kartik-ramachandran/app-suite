import PhosphorIcon from './PhosphorIcon'
import { useState, useEffect, useRef, useCallback } from 'react'
import { streamExecution, saveExecution, getExecution } from '../services/api'
import ExecutionHistory from './ExecutionHistory'
import i18n from '../i18n/index'

interface ChatMessage {
  role: 'user' | 'assistant'
  content: string
  timestamp: string
  tokensUsed?: number
}

interface ExecutionPrompt {
  id: string
  name: string
  content: string
  isTemplate?: boolean
}

interface ExecutionModalProps {
  isOpen: boolean
  onClose: () => void
  prompt: ExecutionPrompt
  executionId?: string
}

function ExecutionModal({ isOpen, onClose, prompt, executionId: initialExecutionId }: ExecutionModalProps) {
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [userInput, setUserInput] = useState('')
  const [isStreaming, setIsStreaming] = useState(false)
  const [streamingContent, setStreamingContent] = useState('')
  // Model preference read from localStorage; backend will use its active provider
  const selectedModel = localStorage.getItem('preferred_model') || 'anthropic.claude-3-haiku-20240307-v1:0'
  const [executionId, setExecutionId] = useState<string | null>(initialExecutionId || null)
  const [totalTokens, setTotalTokens] = useState(0)
  const [toast, setToast] = useState<{ message: string; visible: boolean }>({ message: '', visible: false })
  const [showHistory, setShowHistory] = useState(false)
  const [showSystemPrompt, setShowSystemPrompt] = useState(false)

  const [placeholders, setPlaceholders] = useState<string[]>([])
  const [placeholderValues, setPlaceholderValues] = useState<Record<string, string>>({})
  const [placeholdersFilled, setPlaceholdersFilled] = useState(false)

  const messagesEndRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    const matches = prompt.content.match(/\{\{(\w+)\}\}/g)
    if (matches) {
      const names = [...new Set(matches.map(m => m.replace(/\{\{|\}\}/g, '')))]
      setPlaceholders(names)
      setPlaceholdersFilled(false)
    } else {
      setPlaceholders([])
      setPlaceholdersFilled(true)
    }
  }, [prompt.content])

  useEffect(() => {
    if (initialExecutionId) loadExecution(initialExecutionId)
  }, [initialExecutionId])

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, streamingContent])

  useEffect(() => {
    if (placeholdersFilled && !isStreaming) inputRef.current?.focus()
  }, [placeholdersFilled, isStreaming])

  const loadExecution = async (id: string) => {
    try {
      const response = await getExecution(id)
      const exec = response.data
      const parsed: ChatMessage[] = JSON.parse(exec.messagesJson)
      setMessages(parsed)
      setExecutionId(exec.id)
      setTotalTokens(exec.totalTokensUsed)
      setPlaceholdersFilled(true)
    } catch {
      showToast('Failed to load conversation')
    }
  }

  const getResolvedSystemPrompt = useCallback(() => {
    let resolved = prompt.content
    for (const [key, value] of Object.entries(placeholderValues)) {
      resolved = resolved.replace(new RegExp(`\\{\\{${key}\\}\\}`, 'g'), value)
    }
    return resolved
  }, [prompt.content, placeholderValues])

  const showToast = (message: string) => {
    setToast({ message, visible: true })
    setTimeout(() => setToast({ message: '', visible: false }), 2000)
  }

  const handleSend = async () => {
    if (!userInput.trim() || isStreaming) return

    const userMessage: ChatMessage = {
      role: 'user',
      content: userInput.trim(),
      timestamp: new Date().toISOString()
    }

    const updatedMessages = [...messages, userMessage]
    setMessages(updatedMessages)
    setUserInput('')
    setIsStreaming(true)
    setStreamingContent('')

    const systemPrompt = getResolvedSystemPrompt()
    const userApiKey = localStorage.getItem('openai_key') || undefined

    let fullContent = ''
    let tokensUsed = 0

    try {
      await streamExecution(
        {
          systemPrompt,
          messages: updatedMessages.map(m => ({ role: m.role, content: m.content })),
          model: selectedModel,
          userApiKey: userApiKey || undefined,
          language: i18n.language
        },
        (token) => { fullContent += token; setStreamingContent(fullContent) },
        (tokens) => { tokensUsed = tokens },
        (error) => { showToast(error) }
      )

      if (fullContent) {
        const assistantMessage: ChatMessage = {
          role: 'assistant',
          content: fullContent,
          timestamp: new Date().toISOString(),
          tokensUsed
        }
        const finalMessages = [...updatedMessages, assistantMessage]
        setMessages(finalMessages)
        const newTotal = totalTokens + tokensUsed
        setTotalTokens(newTotal)

        try {
          const response = await saveExecution({
            id: executionId || undefined,
            promptId: prompt.id !== 'unsaved' ? prompt.id : undefined,
            promptName: prompt.name,
            systemPrompt,
            model: selectedModel,
            messagesJson: JSON.stringify(finalMessages),
            totalTokensUsed: newTotal
          })
          if (!executionId) setExecutionId(response.data.id)
        } catch { /* silent */ }
      }
    } catch {
      showToast('Connection error. Please try again.')
    } finally {
      setIsStreaming(false)
      setStreamingContent('')
    }
  }

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleSend() }
  }

  const handleCopyMessage = async (content: string) => {
    try { await navigator.clipboard.writeText(content); showToast('Copied!') }
    catch { showToast('Failed to copy') }
  }

  const handleCopyAll = async () => {
    const text = messages.map(m => `${m.role === 'user' ? 'You' : 'Assistant'}: ${m.content}`).join('\n\n')
    try { await navigator.clipboard.writeText(text); showToast('Conversation copied!') }
    catch { showToast('Failed to copy') }
  }

  const handleNewChat = () => {
    setMessages([])
    setExecutionId(null)
    setTotalTokens(0)
    setStreamingContent('')
    setShowHistory(false)
  }

  const handleFillPlaceholders = () => {
    if (!placeholders.every(p => placeholderValues[p]?.trim())) {
      showToast('Please fill all placeholder fields')
      return
    }
    setPlaceholdersFilled(true)
  }

  const handleSelectExecution = (id: string) => {
    loadExecution(id)
    setShowHistory(false)
  }

  if (!isOpen) return null

  return (
    <div className="modal-overlay execution-modal" onClick={onClose}>
      <div className="modal-content execution-modal-content" onClick={(e) => e.stopPropagation()}>

        {/* Header */}
        <div className="em-header">
          <div className="em-header-left">
            <div className="em-prompt-icon">
              <PhosphorIcon className="bi bi-play-fill" />
            </div>
            <div className="em-header-info">
              <div className="em-prompt-name">{prompt.name}</div>
              <span className="em-model-badge"><PhosphorIcon className="bi bi-cpu" /> Claude via Bedrock</span>
            </div>
          </div>
          <div className="em-header-right">
            {totalTokens > 0 && (
              <span className="em-tokens">{totalTokens.toLocaleString()} tokens</span>
            )}
            <button className="em-icon-btn" onClick={() => setShowHistory(!showHistory)} title="History">
              <PhosphorIcon className="bi bi-clock-history" />
            </button>
            <button className="em-icon-btn" onClick={handleNewChat} disabled={isStreaming} title="New chat">
              <PhosphorIcon className="bi bi-plus-lg" />
            </button>
            <button className="em-close-btn" onClick={onClose} title="Close">
              <PhosphorIcon className="bi bi-x-lg" />
            </button>
          </div>
        </div>

        {/* Body */}
        <div className="em-body">
          {showHistory && (
            <ExecutionHistory
              promptId={prompt.id}
              onSelect={handleSelectExecution}
              onClose={() => setShowHistory(false)}
            />
          )}

          {/* System prompt collapsible */}
          <div className="em-system-bar">
            <div className="em-system-toggle" onClick={() => setShowSystemPrompt(!showSystemPrompt)}>
              <PhosphorIcon className={`bi bi-chevron-${showSystemPrompt ? 'down' : 'right'}`} />
              <span>System Prompt</span>
            </div>
            {showSystemPrompt && (
              <pre className="em-system-content">
                {placeholdersFilled ? getResolvedSystemPrompt() : prompt.content}
              </pre>
            )}
          </div>

          {/* Placeholder inputs */}
          {placeholders.length > 0 && !placeholdersFilled && (
            <div className="em-placeholders">
              <p className="em-placeholders-hint">
                <PhosphorIcon className="bi bi-braces" /> This prompt has template variables — fill them in to continue:
              </p>
              {placeholders.map(name => (
                <div key={name} className="em-placeholder-field">
                  <label><code>{`{{${name}}}`}</code></label>
                  <input
                    type="text"
                    value={placeholderValues[name] || ''}
                    onChange={(e) => setPlaceholderValues(prev => ({ ...prev, [name]: e.target.value }))}
                    placeholder={`Enter value for ${name}`}
                  />
                </div>
              ))}
              <button className="em-apply-btn" onClick={handleFillPlaceholders}>
                <PhosphorIcon className="bi bi-check-lg" /> Apply & Start Chat
              </button>
            </div>
          )}

          {/* Messages */}
          <div className="em-messages">
            {messages.length === 0 && !isStreaming && placeholdersFilled && (
              <div className="em-empty">
                <div className="em-empty-icon"><PhosphorIcon className="bi bi-chat-dots" /></div>
                <p className="em-empty-title">Start the conversation</p>
                <p className="em-empty-sub">Type a message below to test your prompt</p>
              </div>
            )}

            {messages.map((msg, i) => (
              <div key={i} className={`em-msg em-msg--${msg.role}`}>
                {msg.role === 'assistant' && (
                  <div className="em-avatar em-avatar--ai"><PhosphorIcon className="bi bi-stars" /></div>
                )}
                <div className={`em-bubble em-bubble--${msg.role}`}>
                  <div className="em-bubble-text">{msg.content}</div>
                  {msg.role === 'assistant' && (
                    <button className="em-copy-btn" onClick={() => handleCopyMessage(msg.content)} title="Copy">
                      <PhosphorIcon className="bi bi-clipboard" />
                    </button>
                  )}
                </div>
              </div>
            ))}

            {isStreaming && streamingContent && (
              <div className="em-msg em-msg--assistant">
                <div className="em-avatar em-avatar--ai"><PhosphorIcon className="bi bi-stars" /></div>
                <div className="em-bubble em-bubble--assistant">
                  <div className="em-bubble-text">
                    {streamingContent}<span className="em-cursor" />
                  </div>
                </div>
              </div>
            )}

            {isStreaming && !streamingContent && (
              <div className="em-msg em-msg--assistant">
                <div className="em-avatar em-avatar--ai"><PhosphorIcon className="bi bi-stars" /></div>
                <div className="em-bubble em-bubble--assistant">
                  <div className="em-bubble-text">
                    <span className="streaming-dots">
                      <span /><span /><span />
                    </span>
                  </div>
                </div>
              </div>
            )}

            <div ref={messagesEndRef} />
          </div>
        </div>

        {/* Footer input */}
        {placeholdersFilled && (
          <div className="em-footer">
            <div className="em-input-pill">
              <textarea
                ref={inputRef}
                className="em-input"
                value={userInput}
                onChange={(e) => setUserInput(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder="Type your message... (Shift+Enter for new line)"
                disabled={isStreaming}
                rows={1}
              />
              <button
                className="em-send-btn"
                onClick={handleSend}
                disabled={!userInput.trim() || isStreaming}
                title="Send"
              >
                <PhosphorIcon className="bi bi-send-fill" />
              </button>
            </div>
            {messages.length > 0 && (
              <button className="em-copy-all-btn" onClick={handleCopyAll} title="Copy full conversation">
                <PhosphorIcon className="bi bi-clipboard2" />
              </button>
            )}
          </div>
        )}

        {toast.visible && (
          <div className="toast-notification execution-toast">{toast.message}</div>
        )}
      </div>
    </div>
  )
}

export default ExecutionModal
