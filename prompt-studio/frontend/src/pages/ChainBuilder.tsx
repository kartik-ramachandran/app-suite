import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useRef, useCallback, useLayoutEffect, useMemo } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import PromptPicker, { type PromptPickerPrompt } from '../components/PromptPicker'
import CopyTextDialog from '../components/CopyTextDialog'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import '../App.css'

type PortPosition = 'top' | 'right' | 'bottom' | 'left'
type InteractionMode = 'select' | 'pan'

type NodeType =
  // Core
  | 'input' | 'agent' | 'classify' | 'note' | 'output'
  // Tools
  | 'prompt' | 'web_search' | 'http_request' | 'file_search' | 'code' | 'mcp'
  // Logic
  | 'condition' | 'switch' | 'loop' | 'user_approval' | 'merge' | 'delay'
  // Data
  | 'transform' | 'set_variable' | 'format' | 'filter' | 'aggregate'

interface ChainNodeData {
  tempId: number
  nodeType: NodeType
  label: string
  promptId?: string
  inlinePrompt?: string
  personaId?: number
  conditionExpression?: string
  transformExpression?: string
  // new fields
  systemPrompt?: string
  classifyLabels?: string
  switchExpression?: string
  loopCondition?: string
  loopMax?: number
  httpUrl?: string
  httpMethod?: string
  httpBody?: string
  codeSnippet?: string
  variableName?: string
  variableValue?: string
  formatTemplate?: string
  filterExpression?: string
  delaySeconds?: number
  noteText?: string
  // MCP
  mcpServerUrl?: string
  mcpToolName?: string
  mcpArguments?: string
  // File Search
  fileSearchQuery?: string
  fileSearchTypes?: string
  fileSearchMaxResults?: number
  // Merge
  mergeStrategy?: string
  mergeSeparator?: string
  // User Approval
  approvalMessage?: string
  approvalTimeout?: number
  approvalButtonLabel?: string
  positionX: number
  positionY: number
  sortOrder: number
}

interface ChainEdgeData {
  sourceTempId: number
  targetTempId: number
  sourcePort?: PortPosition
  targetPort?: PortPosition
  label?: string
  conditionBranch?: string
}

interface ChainApiNode {
  id: number
  nodeType: NodeType
  label?: string
  promptId?: string
  inlinePrompt?: string
  personaId?: number
  conditionExpression?: string
  transformExpression?: string
  systemPrompt?: string
  classifyLabels?: string
  switchExpression?: string
  loopCondition?: string
  loopMax?: number
  httpUrl?: string
  httpMethod?: string
  httpBody?: string
  codeSnippet?: string
  variableName?: string
  variableValue?: string
  formatTemplate?: string
  filterExpression?: string
  delaySeconds?: number
  noteText?: string
  mergeStrategy?: string
  mergeSeparator?: string
  fileSearchQuery?: string
  fileSearchTypes?: string
  fileSearchMaxResults?: number
  mcpServerUrl?: string
  mcpToolName?: string
  mcpArguments?: string
  positionX: number
  positionY: number
  sortOrder: number
}

interface ChainApiEdge {
  sourceNodeId: number
  targetNodeId: number
  label?: string
  conditionBranch?: string
}

interface ChainApiResponse {
  name: string
  description?: string
  category?: string
  nodes?: ChainApiNode[]
  edges?: ChainApiEdge[]
}

interface PortDragState {
  sourceNodeId: number
  sourcePort: PortPosition
  mouseX: number
  mouseY: number
}

interface ExecutionResult {
  finalOutput: string
  stepResults: { nodeId: number; nodeType: string; label: string; input: string; output: string; tokensUsed: number; error?: string }[]
  totalTokens: number
  executionTimeMs: number
}

interface AiSuggestedNode {
  nodeType: string; label?: string; inlinePrompt?: string; systemPrompt?: string
  conditionExpression?: string; transformExpression?: string; classifyLabels?: string
  switchExpression?: string; loopCondition?: string; loopMax?: number
  httpUrl?: string; httpMethod?: string; httpBody?: string; codeSnippet?: string
  variableName?: string; variableValue?: string; formatTemplate?: string; filterExpression?: string
  delaySeconds?: number; noteText?: string; mergeStrategy?: string; mergeSeparator?: string
  fileSearchQuery?: string; fileSearchTypes?: string; fileSearchMaxResults?: number
  mcpServerUrl?: string; mcpToolName?: string; mcpArguments?: string
  approvalMessage?: string; approvalTimeout?: number; approvalButtonLabel?: string
}
interface AiSuggestedEdge { sourceIndex: number; targetIndex: number; label?: string; conditionBranch?: string }

const NODE_CATEGORIES: { category: string; nodes: { type: string; label: string; icon: string; color: string }[] }[] = [
  {
    category: 'Core',
    nodes: [
      { type: 'input',        label: 'Start',         icon: 'bi-play-circle-fill',     color: '#22c55e' },
      { type: 'agent',        label: 'Agent',         icon: 'bi-cpu',                  color: '#833ab4' },
      { type: 'classify',     label: 'Classify',      icon: 'bi-tags',                 color: '#f97316' },
      { type: 'note',         label: 'Note',          icon: 'bi-sticky',               color: '#eab308' },
      { type: 'output',       label: 'Output',        icon: 'bi-stop-circle-fill',     color: '#ef4444' },
    ],
  },
  {
    category: 'Tools',
    nodes: [
      { type: 'prompt',       label: 'Prompt',        icon: 'bi-chat-dots',            color: '#3b82f6' },
      { type: 'web_search',   label: 'Web Search',    icon: 'bi-search',               color: '#d62976' },
      { type: 'http_request', label: 'HTTP Request',  icon: 'bi-send',                 color: '#fa7e1e' },
      { type: 'file_search',  label: 'File Search',   icon: 'bi-folder-symlink',       color: '#833ab4' },
      { type: 'code',         label: 'Code',          icon: 'bi-code-slash',           color: '#64748b' },
      { type: 'mcp',          label: 'MCP',           icon: 'bi-plug',                 color: '#ec4899' },
    ],
  },
  {
    category: 'Logic',
    nodes: [
      { type: 'condition',    label: 'If / Else',     icon: 'bi-signpost-split',       color: '#f59e0b' },
      { type: 'switch',       label: 'Switch',        icon: 'bi-diagram-3',            color: '#d97706' },
      { type: 'loop',         label: 'While',         icon: 'bi-arrow-repeat',         color: '#b45309' },
      { type: 'user_approval',label: 'User Approval', icon: 'bi-person-check',         color: '#d62976' },
      { type: 'merge',        label: 'Merge',         icon: 'bi-union',                color: '#833ab4' },
      { type: 'delay',        label: 'Delay',         icon: 'bi-hourglass-split',      color: '#94a3b8' },
    ],
  },
  {
    category: 'Data',
    nodes: [
      { type: 'transform',    label: 'Transform',     icon: 'bi-arrow-left-right',     color: '#a855f7' },
      { type: 'set_variable', label: 'Set Variable',  icon: 'bi-braces',               color: '#fa7e1e' },
      { type: 'format',       label: 'Format',        icon: 'bi-filetype-json',        color: '#10b981' },
      { type: 'filter',       label: 'Filter',        icon: 'bi-funnel',               color: '#f43f5e' },
      { type: 'aggregate',    label: 'Aggregate',     icon: 'bi-collection',           color: '#833ab4' },
    ],
  },
]

// Flatten for easy lookup
type NodeDef = { type: string; label: string; icon: string; color: string }
const ALL_NODES: NodeDef[] = NODE_CATEGORIES.flatMap(c => c.nodes)
const NODE_COLORS: Record<string, string> = Object.fromEntries(ALL_NODES.map(n => [n.type, n.color]))
const NODE_ICONS: Record<string, string> = Object.fromEntries(ALL_NODES.map(n => [n.type, n.icon]))
const NODE_LABELS: Record<string, string> = Object.fromEntries(ALL_NODES.map(n => [n.type, n.label]))

const NODE_WIDTH = 160
const PORTS: PortPosition[] = ['top', 'right', 'bottom', 'left']

function getPortPosition(
  node: ChainNodeData,
  port: PortPosition,
  nodeEl?: HTMLDivElement | null
): { x: number; y: number } {
  const w = nodeEl ? nodeEl.offsetWidth : NODE_WIDTH
  const h = nodeEl ? nodeEl.offsetHeight : 70
  switch (port) {
    case 'top':    return { x: node.positionX + w / 2, y: node.positionY }
    case 'bottom': return { x: node.positionX + w / 2, y: node.positionY + h }
    case 'left':   return { x: node.positionX, y: node.positionY + h / 2 }
    case 'right':  return { x: node.positionX + w, y: node.positionY + h / 2 }
  }
}

function getBestPorts(source: ChainNodeData, target: ChainNodeData): { sourcePort: PortPosition; targetPort: PortPosition } {
  const dx = target.positionX - source.positionX
  const dy = target.positionY - source.positionY
  if (Math.abs(dx) > Math.abs(dy)) {
    return dx > 0 ? { sourcePort: 'right', targetPort: 'left' } : { sourcePort: 'left', targetPort: 'right' }
  }
  return dy > 0 ? { sourcePort: 'bottom', targetPort: 'top' } : { sourcePort: 'top', targetPort: 'bottom' }
}

const PORT_DIR: Record<PortPosition, { dx: number; dy: number }> = {
  top:    { dx: 0,  dy: -1 },
  right:  { dx: 1,  dy: 0 },
  bottom: { dx: 0,  dy: 1 },
  left:   { dx: -1, dy: 0 },
}

function getBezierControlPoints(
  from: { x: number; y: number },
  to: { x: number; y: number },
  sourcePort: PortPosition | null,
  targetPort: PortPosition | null
) {
  const dist = Math.sqrt((to.x - from.x) ** 2 + (to.y - from.y) ** 2)
  const offset = Math.min(Math.max(dist * 0.4, 30), 120)
  const srcDir = sourcePort ? PORT_DIR[sourcePort] : { dx: 0, dy: 0 }
  const tgtDir = targetPort ? PORT_DIR[targetPort] : { dx: 0, dy: 0 }
  return {
    cp1x: from.x + srcDir.dx * offset,
    cp1y: from.y + srcDir.dy * offset,
    cp2x: to.x + tgtDir.dx * offset,
    cp2y: to.y + tgtDir.dy * offset,
  }
}

function bezierPointAt(
  from: { x: number; y: number },
  cp1: { x: number; y: number },
  cp2: { x: number; y: number },
  to: { x: number; y: number },
  t: number
) {
  const mt = 1 - t
  return {
    x: mt ** 3 * from.x + 3 * mt ** 2 * t * cp1.x + 3 * mt * t ** 2 * cp2.x + t ** 3 * to.x,
    y: mt ** 3 * from.y + 3 * mt ** 2 * t * cp1.y + 3 * mt * t ** 2 * cp2.y + t ** 3 * to.y,
  }
}

function ChainBuilder() {
  const { t } = useTranslation()
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()

  const [chainName, setChainName] = useState('')
  const [chainDescription, setChainDescription] = useState('')
  const [chainCategory, setChainCategory] = useState('')
  const [nodes, setNodes] = useState<ChainNodeData[]>([])
  const [edges, setEdges] = useState<ChainEdgeData[]>([])
  const [selectedNode, setSelectedNode] = useState<number | null>(null)
  const [selectedEdge, setSelectedEdge] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [toast, setToast] = useState<{ message: string; visible: boolean }>({ message: '', visible: false })
  const [shareDialogUrl, setShareDialogUrl] = useState('')
  const [saving, setSaving] = useState(false)
  const [commandPrompt, setCommandPrompt] = useState('')
  const [canvasZoom, setCanvasZoom] = useState(100)
  const [interactionMode, setInteractionMode] = useState<InteractionMode>('select')
  const [showCanvasGrid, setShowCanvasGrid] = useState(true)
  const [workflowInsight, setWorkflowInsight] = useState<string>('Select a node or use Explain to generate a quick workflow summary.')

  // Port drag state
  const [portDrag, setPortDrag] = useState<PortDragState | null>(null)
  const [hoveredPort, setHoveredPort] = useState<{ nodeId: number; port: PortPosition } | null>(null)

  // Execution
  const [showExecutePanel, setShowExecutePanel] = useState(false)
  const [executeInput, setExecuteInput] = useState('')
  const [executing, setExecuting] = useState(false)
  const [executionResult, setExecutionResult] = useState<ExecutionResult | null>(null)

  // AI command
  const [aiLoading, setAiLoading] = useState(false)

  // Undo history
  const [undoStack, setUndoStack] = useState<{ nodes: ChainNodeData[]; edges: ChainEdgeData[] }[]>([])

  const pushUndo = (prevNodes: ChainNodeData[], prevEdges: ChainEdgeData[]) => {
    setUndoStack(s => [...s.slice(-19), { nodes: prevNodes, edges: prevEdges }])
  }

  const handleUndo = () => {
    if (undoStack.length === 0) return
    const prev = undoStack[undoStack.length - 1]
    setUndoStack(s => s.slice(0, -1))
    setNodes(prev.nodes)
    setEdges(prev.edges)
  }

  // Edit node modal
  const [editNode, setEditNode] = useState<ChainNodeData | null>(null)

  // Collapsible sidebar sections
  const [expanded, setExpanded] = useState<Record<string, boolean>>({
    info: true, Core: true, Tools: true, Logic: true, Data: true, connections: false,
  })
  const toggleSection = (key: string) =>
    setExpanded(prev => ({ ...prev, [key]: !prev[key] }))

  // Drag state
  const [draggingNode, setDraggingNode] = useState<number | null>(null)
  const [dragOffset, setDragOffset] = useState({ x: 0, y: 0 })
  const [isPanning, setIsPanning] = useState(false)
  const [pan, setPan] = useState({ x: 300, y: 200 })
  const panRef = useRef({ x: 300, y: 200 })
  const [panOrigin, setPanOrigin] = useState({ x: 0, y: 0, panX: 0, panY: 0 })
  const canvasRef = useRef<HTMLDivElement>(null)
  const nodeRefs = useRef<Map<number, HTMLDivElement>>(new Map())
  const hasPositionedViewportRef = useRef(false)
  const nextTempIdRef = useRef(1)
  const draggedNodeTypeRef = useRef<string | null>(null)
  // Ref mirrors for values read in native event handlers to avoid stale closures
  const hoveredPortRef = useRef<{ nodeId: number; port: PortPosition } | null>(null)
  const portDragRef = useRef<PortDragState | null>(null)
  const selectedNodeRef = useRef<number | null>(null)
  const selectedEdgeRef = useRef<number | null>(null)

  const showToast = (message: string) => {
    setToast({ message, visible: true })
    setTimeout(() => setToast({ message: '', visible: false }), 2000)
  }

  const canvasScale = canvasZoom / 100

  useEffect(() => {
    if (id) loadChain(id)
  }, [id])

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph). The
  // brief lands in the command bar so one Enter builds the workflow from it.
  useEffect(() => {
    if (id) return // only on a fresh canvas, never over a loaded workflow
    const handoff = consumeCosmosHandoff('/chain-builder')
    if (!handoff?.input) return
    setCommandPrompt(handoff.input)
    if (handoff.title) setChainName(current => current || handoff.title!)
  }, [id])

  useEffect(() => {
    nextTempIdRef.current = Math.max(nextTempIdRef.current, ...nodes.map((node) => node.tempId + 1), 1)
  }, [nodes])

  useEffect(() => { selectedNodeRef.current = selectedNode }, [selectedNode])
  useEffect(() => { selectedEdgeRef.current = selectedEdge }, [selectedEdge])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setEditNode(null)
      if ((e.ctrlKey || e.metaKey) && e.key === 'z') { e.preventDefault(); handleUndo() }
      if (e.key === 'Delete' || e.key === 'Backspace') {
        const active = document.activeElement
        const isTyping = active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement || active instanceof HTMLSelectElement || (active as HTMLElement)?.isContentEditable
        if (isTyping) return
        if (selectedEdgeRef.current !== null) {
          deleteEdge(selectedEdgeRef.current)
          return
        }
        if (selectedNodeRef.current !== null) deleteNode(selectedNodeRef.current)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [undoStack, nodes, edges])

  const loadChain = async (chainId: string) => {
    try {
      const response = await api.get<ChainApiResponse>(`/chains/${chainId}`)
      const chain = response.data
      setChainName(chain.name)
      setChainDescription(chain.description || '')
      setChainCategory(chain.category || '')

      const loadedNodeIdMap = new Map<number, number>()
      let nextLoadedTempId = 1
      const loadedNodes = (chain.nodes || []).map((n) => ({
        tempId: (() => {
          const clientTempId = nextLoadedTempId++
          loadedNodeIdMap.set(n.id, clientTempId)
          return clientTempId
        })(),
        nodeType: n.nodeType,
        label: n.label || n.nodeType,
        promptId: n.promptId,
        inlinePrompt: n.inlinePrompt,
        personaId: n.personaId,
        conditionExpression: n.conditionExpression,
        transformExpression: n.transformExpression,
        systemPrompt: n.systemPrompt,
        classifyLabels: n.classifyLabels,
        switchExpression: n.switchExpression,
        loopCondition: n.loopCondition,
        loopMax: n.loopMax,
        httpUrl: n.httpUrl,
        httpMethod: n.httpMethod,
        httpBody: n.httpBody,
        codeSnippet: n.codeSnippet,
        variableName: n.variableName,
        variableValue: n.variableValue,
        formatTemplate: n.formatTemplate,
        filterExpression: n.filterExpression,
        delaySeconds: n.delaySeconds,
        noteText: n.noteText,
        mergeStrategy: n.mergeStrategy,
        mergeSeparator: n.mergeSeparator,
        fileSearchQuery: n.fileSearchQuery,
        fileSearchTypes: n.fileSearchTypes,
        fileSearchMaxResults: n.fileSearchMaxResults,
        mcpServerUrl: n.mcpServerUrl,
        mcpToolName: n.mcpToolName,
        mcpArguments: n.mcpArguments,
        positionX: n.positionX,
        positionY: n.positionY,
        sortOrder: n.sortOrder
      }))
      setNodes(loadedNodes)
      nextTempIdRef.current = nextLoadedTempId

      const loadedEdges = (chain.edges || []).map((e) => ({
        sourceTempId: loadedNodeIdMap.get(e.sourceNodeId) ?? e.sourceNodeId,
        targetTempId: loadedNodeIdMap.get(e.targetNodeId) ?? e.targetNodeId,
        label: e.label,
        conditionBranch: e.conditionBranch
      }))
      setEdges(loadedEdges)
    } catch {
      setError('Failed to load workflow')
    }
  }

  const addNode = (nodeType: NodeType) => {
    const canvas = canvasRef.current
    const centerX = canvas ? (canvas.clientWidth / 2 - panRef.current.x) / canvasScale - 75 : 200
    const centerY = canvas ? (canvas.clientHeight / 2 - panRef.current.y) / canvasScale - 30 : 150
    const newNode: ChainNodeData = {
      tempId: nextTempIdRef.current++,
      nodeType,
      label: nodeType.charAt(0).toUpperCase() + nodeType.slice(1),
      positionX: centerX + (Math.random() - 0.5) * 100,
      positionY: centerY + (Math.random() - 0.5) * 80,
      sortOrder: nodes.length
    }
    setNodes([...nodes, newNode])
  }

  const addNodeByType = (nodeType: NodeType) => {
    addNode(nodeType)
    const definition = ALL_NODES.find((node) => node.type === nodeType)
    showToast(`${definition?.label || 'Node'} added to the canvas`)
  }

  const deleteNode = (tempId: number) => {
    pushUndo(nodes, edges)
    setNodes(nodes.filter(n => n.tempId !== tempId))
    setEdges(edges.filter(e => e.sourceTempId !== tempId && e.targetTempId !== tempId))
    if (selectedNode === tempId) setSelectedNode(null)
    setSelectedEdge(null)
  }

  const deleteEdge = (edgeIndex: number) => {
    pushUndo(nodes, edges)
    setEdges(edges.filter((_, index) => index !== edgeIndex))
    setSelectedEdge(null)
  }

  // Port drag handlers
  const handlePortMouseDown = (nodeId: number, port: PortPosition, e: React.MouseEvent) => {
    if (interactionMode !== 'select') return
    e.stopPropagation()
    e.preventDefault()
    if (!canvasRef.current) return
    const rect = canvasRef.current.getBoundingClientRect()
    const pd: PortDragState = {
      sourceNodeId: nodeId,
      sourcePort: port,
      mouseX: (e.clientX - rect.left - panRef.current.x) / canvasScale,
      mouseY: (e.clientY - rect.top - panRef.current.y) / canvasScale,
    }
    portDragRef.current = pd
    setPortDrag(pd)
  }

  // Node drag handlers
  const handleMouseDown = (tempId: number, e: React.MouseEvent) => {
    if (interactionMode !== 'select') return
    if (e.button !== 0) return
    e.stopPropagation()
    const node = nodes.find(n => n.tempId === tempId)
    if (!node || !canvasRef.current) return
    const rect = canvasRef.current.getBoundingClientRect()
    setDraggingNode(tempId)
    setDragOffset({
      x: (e.clientX - rect.left - pan.x) / canvasScale - node.positionX,
      y: (e.clientY - rect.top - pan.y) / canvasScale - node.positionY,
    })
    e.preventDefault()
  }

  const handleCanvasMouseDown = (e: React.MouseEvent<HTMLDivElement>) => {
    if (!canvasRef.current || e.button !== 0) return

    const target = e.target as HTMLElement
    const clickedNode = target.closest('.cb-node')
    const clickedPort = target.closest('.cb-port')
    const shouldPan = interactionMode === 'pan' || (!clickedNode && !clickedPort)

    if (!shouldPan) return

    setIsPanning(true)
    setPanOrigin({
      x: e.clientX,
      y: e.clientY,
      panX: panRef.current.x,
      panY: panRef.current.y,
    })
    e.preventDefault()
  }

  const handleMouseMove = useCallback((e: MouseEvent) => {
    if (!canvasRef.current) return
    const rect = canvasRef.current.getBoundingClientRect()

    if (isPanning) {
      const newPan = {
        x: panOrigin.panX + (e.clientX - panOrigin.x),
        y: panOrigin.panY + (e.clientY - panOrigin.y),
      }
      panRef.current = newPan
      setPan(newPan)
      return
    }

    // Node dragging
    if (draggingNode !== null) {
      const x = (e.clientX - rect.left - panRef.current.x) / canvasScale - dragOffset.x
      const y = (e.clientY - rect.top - panRef.current.y) / canvasScale - dragOffset.y
      setNodes(prev => prev.map(n =>
        n.tempId === draggingNode ? { ...n, positionX: x, positionY: y } : n
      ))
      return
    }

    // Port dragging
    if (portDragRef.current) {
      const updated = {
        ...portDragRef.current,
        mouseX: (e.clientX - rect.left - panRef.current.x) / canvasScale,
        mouseY: (e.clientY - rect.top - panRef.current.y) / canvasScale,
      }
      portDragRef.current = updated
      setPortDrag(updated)
    }
  }, [canvasScale, dragOffset, draggingNode, isPanning, panOrigin])

  const handleMouseUp = useCallback(() => {
    if (isPanning) {
      setIsPanning(false)
    }

    if (draggingNode !== null) {
      setDraggingNode(null)
      return
    }

    const currentPortDrag = portDragRef.current
    const currentHoveredPort = hoveredPortRef.current
    if (currentPortDrag && currentHoveredPort) {
      const { sourceNodeId, sourcePort } = currentPortDrag
      const { nodeId: targetNodeId, port: targetPort } = currentHoveredPort

      if (sourceNodeId !== targetNodeId) {
        setEdges(prev => {
          const exists = prev.some(e => e.sourceTempId === sourceNodeId && e.targetTempId === targetNodeId)
          return exists ? prev : [...prev, { sourceTempId: sourceNodeId, targetTempId: targetNodeId, sourcePort, targetPort }]
        })
      }
    }

    portDragRef.current = null
    setPortDrag(null)
  }, [draggingNode, isPanning])

  const handleFitCanvas = () => {
    if (!canvasRef.current || nodes.length === 0) {
      setCanvasZoom(100)
      return
    }

    const minX = Math.min(...nodes.map((node) => node.positionX))
    const minY = Math.min(...nodes.map((node) => node.positionY))
    const maxX = Math.max(...nodes.map((node) => node.positionX + 240))
    const maxY = Math.max(...nodes.map((node) => node.positionY + 180))
    const contentWidth = maxX - minX
    const contentHeight = maxY - minY
    const nextZoom = Math.max(50, Math.min(150, Math.floor(Math.min(
      (canvasRef.current.clientWidth - 160) / Math.max(contentWidth, 1),
      (canvasRef.current.clientHeight - 160) / Math.max(contentHeight, 1)
    ) * 100)))

    setCanvasZoom(nextZoom)

    requestAnimationFrame(() => {
      if (!canvasRef.current) return
      const scale = nextZoom / 100
      const { clientWidth, clientHeight } = canvasRef.current
      const newPan = {
        x: (clientWidth - contentWidth * scale) / 2 - minX * scale,
        y: (clientHeight - contentHeight * scale) / 2 - minY * scale,
      }
      panRef.current = newPan
      setPan(newPan)
    })
  }

  const handleCenterCanvas = () => {
    if (!canvasRef.current) return
    const { clientWidth, clientHeight } = canvasRef.current
    const targetNode = selectedNode ? nodes.find((node) => node.tempId === selectedNode) : null
    const newPan = targetNode
      ? { x: clientWidth / 2 - (targetNode.positionX + 80) * canvasScale, y: clientHeight / 2 - (targetNode.positionY + 35) * canvasScale }
      : { x: clientWidth / 2 - 900 * canvasScale, y: clientHeight / 2 - 600 * canvasScale }
    panRef.current = newPan
    setPan(newPan)
  }

  const canvasZoomRef = useRef(canvasZoom)
  useEffect(() => { canvasZoomRef.current = canvasZoom }, [canvasZoom])

  // Attach wheel with { passive: false } so preventDefault() actually works
  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return

    const handler = (e: WheelEvent) => {
      if (!canvasRef.current) return
      e.preventDefault()
      e.stopPropagation()

      const currentZoom = canvasZoomRef.current
      const currentScale = currentZoom / 100
      const rect = canvasRef.current.getBoundingClientRect()
      const worldX = (e.clientX - rect.left - panRef.current.x) / currentScale
      const worldY = (e.clientY - rect.top - panRef.current.y) / currentScale

      const delta = e.deltaY < 0 ? 10 : -10
      const nextZoom = Math.max(50, Math.min(150, currentZoom + delta))
      if (nextZoom === currentZoom) return

      const nextScale = nextZoom / 100
      canvasZoomRef.current = nextZoom
      setCanvasZoom(nextZoom)

      const newPan = {
        x: (e.clientX - rect.left) - worldX * nextScale,
        y: (e.clientY - rect.top) - worldY * nextScale,
      }
      panRef.current = newPan
      setPan(newPan)
    }

    canvas.addEventListener('wheel', handler, { passive: false })
    return () => canvas.removeEventListener('wheel', handler)
  }, [])

  useEffect(() => {
    if (!canvasRef.current || hasPositionedViewportRef.current) return

    requestAnimationFrame(() => {
      if (!canvasRef.current) return
      const { clientWidth, clientHeight } = canvasRef.current
      const newPan = {
        x: clientWidth / 2 - 900 * canvasScale,
        y: clientHeight / 2 - 600 * canvasScale,
      }
      panRef.current = newPan
      setPan(newPan)
      hasPositionedViewportRef.current = true
    })
  }, [canvasScale])

  const handleExportWorkflow = () => {
    const snapshot = {
      name: chainName,
      description: chainDescription,
      category: chainCategory,
      zoom: canvasZoom,
      nodes,
      edges,
    }

    const blob = new Blob([JSON.stringify(snapshot, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `${(chainName || 'workflow').replace(/\s+/g, '-').toLowerCase()}-snapshot.json`
    link.click()
    URL.revokeObjectURL(url)
    showToast('Workflow snapshot downloaded')
  }

  useEffect(() => {
    window.addEventListener('mousemove', handleMouseMove)
    window.addEventListener('mouseup', handleMouseUp)
    return () => {
      window.removeEventListener('mousemove', handleMouseMove)
      window.removeEventListener('mouseup', handleMouseUp)
    }
  }, [handleMouseMove, handleMouseUp])

  const handleSave = async () => {
    setError(null)
    if (!chainName.trim()) {
      setError('Workflow name is required')
      return
    }
    try {
      setSaving(true)
      const data = {
        name: chainName,
        description: chainDescription,
        category: chainCategory,
        nodes: nodes.map(n => ({
          tempId: n.tempId,
          nodeType: n.nodeType,
          label: n.label,
          promptId: n.promptId,
          inlinePrompt: n.inlinePrompt,
          personaId: n.personaId,
          conditionExpression: n.conditionExpression,
          transformExpression: n.transformExpression,
          systemPrompt: n.systemPrompt,
          classifyLabels: n.classifyLabels,
          switchExpression: n.switchExpression,
          loopCondition: n.loopCondition,
          loopMax: n.loopMax,
          httpUrl: n.httpUrl,
          httpMethod: n.httpMethod,
          httpBody: n.httpBody,
          codeSnippet: n.codeSnippet,
          variableName: n.variableName,
          variableValue: n.variableValue,
          formatTemplate: n.formatTemplate,
          filterExpression: n.filterExpression,
          delaySeconds: n.delaySeconds,
          noteText: n.noteText,
          mergeStrategy: n.mergeStrategy,
          mergeSeparator: n.mergeSeparator,
          fileSearchQuery: n.fileSearchQuery,
          fileSearchTypes: n.fileSearchTypes,
          fileSearchMaxResults: n.fileSearchMaxResults,
          mcpServerUrl: n.mcpServerUrl,
          mcpToolName: n.mcpToolName,
          mcpArguments: n.mcpArguments,
          positionX: n.positionX,
          positionY: n.positionY,
          sortOrder: n.sortOrder
        })),
        edges: edges.map(e => ({
          sourceTempId: e.sourceTempId,
          targetTempId: e.targetTempId,
          label: e.label,
          conditionBranch: e.conditionBranch
        }))
      }

      if (id) {
        await api.put(`/chains/${id}`, data)
        showToast(t('chains.saved'))
      } else {
        const response = await api.post('/chains', data)
        showToast(t('chains.saved'))
        navigate(`/chain-builder/${response.data.id}`)
      }
    } catch {
      setError('Failed to save workflow')
    } finally {
      setSaving(false)
    }
  }

  const handleExecute = async () => {
    if (!id) {
      setError('Save the workflow first before executing')
      return
    }
    try {
      setExecuting(true)
      setExecutionResult(null)
      const response = await api.post<ExecutionResult>(`/chains/${id}/execute`, { input: executeInput })
      setExecutionResult(response.data)
    } catch (err: unknown) {
      const errorMessage = err && typeof err === 'object' && 'response' in err
        ? (err as { response?: { data?: { message?: string } } }).response?.data?.message
        : undefined
      setError(errorMessage || 'Failed to execute workflow')
    } finally {
      setExecuting(false)
    }
  }

  const buildWorkflowSummary = () => {
    if (nodes.length === 0) {
      return 'This workflow is empty. Add a start node, then chain agent, logic, or output nodes to build a flow.'
    }

    const selected = selectedNode ? nodes.find((node) => node.tempId === selectedNode) : null
    if (selected) {
      const outgoing = edges.filter((edge) => edge.sourceTempId === selected.tempId).length
      const incoming = edges.filter((edge) => edge.targetTempId === selected.tempId).length
      const details = [
        `${selected.label} is a ${NODE_LABELS[selected.nodeType] || selected.nodeType} node.`,
        incoming > 0 ? `It receives ${incoming} connection${incoming === 1 ? '' : 's'}.` : 'It does not receive any upstream input yet.',
        outgoing > 0 ? `It sends output to ${outgoing} node${outgoing === 1 ? '' : 's'}.` : 'It does not forward output to any downstream node yet.',
      ]

      if (selected.inlinePrompt) {
        details.push(`Configured prompt: ${selected.inlinePrompt.substring(0, 120)}${selected.inlinePrompt.length > 120 ? '...' : ''}`)
      } else if (selected.conditionExpression) {
        details.push(`Condition: ${selected.conditionExpression}`)
      } else if (selected.httpUrl) {
        details.push(`Calls ${selected.httpMethod || 'GET'} ${selected.httpUrl}`)
      }

      return details.join(' ')
    }

    const types = Array.from(new Set(nodes.map((node) => NODE_LABELS[node.nodeType] || node.nodeType)))
    const starts = nodes.filter((node) => node.nodeType === 'input').length
    const outputs = nodes.filter((node) => node.nodeType === 'output').length

    return `This workflow has ${nodes.length} node${nodes.length === 1 ? '' : 's'} across ${types.join(', ')}. It currently has ${edges.length} connection${edges.length === 1 ? '' : 's'}, ${starts} start node${starts === 1 ? '' : 's'}, and ${outputs} output node${outputs === 1 ? '' : 's'}.`
  }

  const handleExplainWorkflow = () => {
    const summary = buildWorkflowSummary()
    setWorkflowInsight(summary)
    showToast(selectedNode ? 'Node explanation updated' : 'Workflow summary updated')
  }

  const handleShareWorkflow = async () => {
    if (!id) {
      showToast('Save the workflow first to share it')
      return
    }

    const shareUrl = `${window.location.origin}/chain-builder/${id}`

    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(shareUrl)
        showToast('Workflow link copied to clipboard')
        return
      }
    } catch {
      // Fall through to the custom copy dialog when clipboard access is unavailable.
    }

    setShareDialogUrl(shareUrl)
  }

  const handleCommandSubmit = async () => {
    const normalized = commandPrompt.trim().toLowerCase()

    if (!normalized) {
      showToast('Type a command to guide your workflow')
      return
    }

    const addMatch = normalized.match(/^\/?(?:add|insert|create)\s+(.+)$/)
    if (addMatch) {
      const requestedType = addMatch[1].replace(/[-\s]+/g, '_')
      const nodeDef = ALL_NODES.find((node) => {
        const normalizedLabel = node.label.toLowerCase().replace(/[\s/-]+/g, '_')
        return node.type === requestedType || normalizedLabel === requestedType
      })

      if (nodeDef) {
        addNodeByType(nodeDef.type as NodeType)
        setCommandPrompt('')
        return
      }
    }

    if (normalized === '/connect' || normalized === 'connect' || normalized === 'auto-connect') {
      handleAutoConnect()
      setCommandPrompt('')
      return
    }

    if (normalized === '/optimize' || normalized === 'optimize' || normalized === 'layout') {
      handleOptimizeLayout()
      setCommandPrompt('')
      return
    }

    if (normalized === '/run' || normalized === 'run' || normalized === 'test') {
      setShowExecutePanel(true)
      showToast('Execution panel opened')
      setCommandPrompt('')
      return
    }

    if (normalized === '/explain' || normalized === 'explain') {
      handleExplainWorkflow()
      setCommandPrompt('')
      return
    }

    const matchingNode = ALL_NODES.find((node) => normalized.includes(node.label.toLowerCase()) || normalized.includes(node.type.replace('_', ' ')))
    if (matchingNode && /(add|insert|create|new)/.test(normalized)) {
      addNodeByType(matchingNode.type as NodeType)
      setCommandPrompt('')
      return
    }

    // Fall through to AI — send the prompt to the backend AI command endpoint
    setAiLoading(true)
    showToast('Asking AI...')
    const prompt = commandPrompt.trim()
    setCommandPrompt('')
    try {
      const response = await api.post('/chains/ai-command', {
        prompt,
        chainName,
        chainDescription,
        nodes: nodes.map(n => ({
          tempId: n.tempId, nodeType: n.nodeType, label: n.label,
          positionX: n.positionX, positionY: n.positionY,
          inlinePrompt: n.inlinePrompt, systemPrompt: n.systemPrompt,
          conditionExpression: n.conditionExpression, transformExpression: n.transformExpression,
          httpUrl: n.httpUrl, httpMethod: n.httpMethod, noteText: n.noteText,
          variableName: n.variableName, variableValue: n.variableValue, classifyLabels: n.classifyLabels,
        })),
        edges: edges.map(e => ({ sourceTempId: e.sourceTempId, targetTempId: e.targetTempId, label: e.label })),
        selectedNodeTempId: selectedNode,
      })

      const { message, shouldConnectFromAnchor, nodes: aiNodes, edges: aiEdges } = response.data

      if (!aiNodes || aiNodes.length === 0) {
        setWorkflowInsight(message || 'AI did not suggest any changes.')
        showToast(message || 'No changes suggested')
        return
      }

      const canvas = canvasRef.current
      const maxX = nodes.length > 0 ? Math.max(...nodes.map(n => n.positionX)) + 240 : (canvas ? (canvas.clientWidth / 2 - panRef.current.x) / canvasScale : 300)
      const baseY = canvas ? (canvas.clientHeight / 2 - panRef.current.y) / canvasScale : 200

      const newTempIds: number[] = []
      const addedNodes: ChainNodeData[] = (aiNodes as AiSuggestedNode[]).map((n, i) => {
        const tempId = nextTempIdRef.current++
        newTempIds.push(tempId)
        return {
          tempId, nodeType: n.nodeType as NodeType,
          label: n.label || NODE_LABELS[n.nodeType] || n.nodeType,
          inlinePrompt: n.inlinePrompt, systemPrompt: n.systemPrompt,
          conditionExpression: n.conditionExpression, transformExpression: n.transformExpression,
          classifyLabels: n.classifyLabels, switchExpression: n.switchExpression,
          loopCondition: n.loopCondition, loopMax: n.loopMax,
          httpUrl: n.httpUrl, httpMethod: n.httpMethod, httpBody: n.httpBody,
          codeSnippet: n.codeSnippet, variableName: n.variableName, variableValue: n.variableValue,
          formatTemplate: n.formatTemplate, filterExpression: n.filterExpression,
          delaySeconds: n.delaySeconds, noteText: n.noteText,
          mergeStrategy: n.mergeStrategy, mergeSeparator: n.mergeSeparator,
          fileSearchQuery: n.fileSearchQuery, fileSearchTypes: n.fileSearchTypes, fileSearchMaxResults: n.fileSearchMaxResults,
          mcpServerUrl: n.mcpServerUrl, mcpToolName: n.mcpToolName, mcpArguments: n.mcpArguments,
          approvalMessage: n.approvalMessage, approvalTimeout: n.approvalTimeout, approvalButtonLabel: n.approvalButtonLabel,
          positionX: maxX + i * 230, positionY: baseY + (i % 2) * 110,
          sortOrder: nodes.length + i,
        }
      })

      const addedEdges: ChainEdgeData[] = ((aiEdges || []) as AiSuggestedEdge[])
        .filter(e => e.sourceIndex >= 0 && e.sourceIndex < newTempIds.length && e.targetIndex >= 0 && e.targetIndex < newTempIds.length)
        .map(e => ({ sourceTempId: newTempIds[e.sourceIndex], targetTempId: newTempIds[e.targetIndex], label: e.label, conditionBranch: e.conditionBranch }))

      if (shouldConnectFromAnchor && newTempIds.length > 0) {
        const anchorId = selectedNode ?? (nodes.length > 0 ? nodes[nodes.length - 1].tempId : null)
        if (anchorId) addedEdges.push({ sourceTempId: anchorId, targetTempId: newTempIds[0] })
      }

      pushUndo(nodes, edges)
      setNodes(prev => [...prev, ...addedNodes])
      setEdges(prev => [...prev, ...addedEdges])
      setWorkflowInsight(message || `Added ${addedNodes.length} node${addedNodes.length !== 1 ? 's' : ''}`)
      showToast(message || `Added ${addedNodes.length} AI node${addedNodes.length !== 1 ? 's' : ''}`)
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message || 'AI command failed. Check your API key in Settings.'
      setWorkflowInsight(msg)
      showToast('AI command failed')
    } finally {
      setAiLoading(false)
    }
  }

  const handleAutoConnect = async () => {
    if (nodes.length < 2) {
      showToast('Add at least two nodes to auto-connect them')
      return
    }

    setAiLoading(true)
    showToast('AI is connecting nodes…')
    try {
      const response = await api.post('/chains/ai-connect', {
        chainName,
        nodes: nodes.map(n => ({ tempId: n.tempId, nodeType: n.nodeType, label: n.label })),
      })
      const { message, edges: suggested } = response.data as { message: string; edges: { sourceTempId: number; targetTempId: number; label?: string; conditionBranch?: string }[] }
      if (suggested && suggested.length > 0) {
        const newEdges: ChainEdgeData[] = suggested
          .filter(e => nodes.some(n => n.tempId === e.sourceTempId) && nodes.some(n => n.tempId === e.targetTempId))
          .map(e => {
            const src = nodes.find(n => n.tempId === e.sourceTempId)!
            const tgt = nodes.find(n => n.tempId === e.targetTempId)!
            const ports = getBestPorts(src, tgt)
            return { sourceTempId: e.sourceTempId, targetTempId: e.targetTempId, label: e.label, conditionBranch: e.conditionBranch, sourcePort: ports.sourcePort, targetPort: ports.targetPort }
          })
        pushUndo(nodes, edges)
        setEdges(newEdges)
        setWorkflowInsight(message || 'Nodes connected by AI.')
        showToast(message || 'Workflow auto-connected by AI')
      } else {
        showToast('AI returned no connections')
      }
    } catch {
      showToast('AI connect failed — check API key in Settings')
    } finally {
      setAiLoading(false)
    }
  }

  const handleOptimizeLayout = async () => {
    if (nodes.length === 0) {
      showToast('Add nodes to optimize the layout')
      return
    }

    setAiLoading(true)
    showToast('AI is optimising layout…')
    try {
      const response = await api.post('/chains/ai-layout', {
        chainName,
        nodes: nodes.map(n => ({ tempId: n.tempId, nodeType: n.nodeType, label: n.label, positionX: n.positionX, positionY: n.positionY })),
        edges: edges.map(e => ({ sourceTempId: e.sourceTempId, targetTempId: e.targetTempId })),
      })
      const { message, positions } = response.data as { message: string; positions: { tempId: number; positionX: number; positionY: number }[] }
      if (positions && positions.length > 0) {
        const posMap = new Map(positions.map(p => [p.tempId, { positionX: p.positionX, positionY: p.positionY }]))
        pushUndo(nodes, edges)
        setNodes(prev => prev.map(n => posMap.has(n.tempId) ? { ...n, ...posMap.get(n.tempId) } : n))

        // Fit viewport to the new positions (computed here to avoid stale closure)
        if (canvasRef.current) {
          const allX = nodes.map(n => posMap.get(n.tempId)?.positionX ?? n.positionX)
          const allY = nodes.map(n => posMap.get(n.tempId)?.positionY ?? n.positionY)
          const minX = Math.min(...allX), minY = Math.min(...allY)
          const maxX = Math.max(...allX) + 240, maxY = Math.max(...allY) + 180
          const { clientWidth, clientHeight } = canvasRef.current
          const nextZoom = Math.max(50, Math.min(150, Math.floor(Math.min(
            (clientWidth - 160) / Math.max(maxX - minX, 1),
            (clientHeight - 160) / Math.max(maxY - minY, 1)
          ) * 100)))
          const scale = nextZoom / 100
          setCanvasZoom(nextZoom)
          canvasZoomRef.current = nextZoom
          const newPan = {
            x: (clientWidth - (maxX - minX) * scale) / 2 - minX * scale,
            y: (clientHeight - (maxY - minY) * scale) / 2 - minY * scale,
          }
          panRef.current = newPan
          setPan(newPan)
        }

        setWorkflowInsight(message || 'Layout optimised by AI.')
        showToast(message || 'Layout optimised by AI')
      } else {
        showToast('AI returned no positions')
      }
    } catch {
      showToast('AI layout failed — check API key in Settings')
    } finally {
      setAiLoading(false)
    }
  }


  const updateEditNode = <K extends keyof ChainNodeData>(field: K, value: ChainNodeData[K]) => {
    if (!editNode) return
    setEditNode({ ...editNode, [field]: value })
  }

  const applySavedPromptToEditNode = (prompt: PromptPickerPrompt) => {
    if (!editNode) return

    const shouldReplaceGenericLabel = !editNode.label || editNode.label === NODE_LABELS[editNode.nodeType]
    setEditNode({
      ...editNode,
      label: shouldReplaceGenericLabel ? prompt.name : editNode.label,
      promptId: prompt.id,
      inlinePrompt: prompt.content,
    })
    showToast(`Loaded prompt: ${prompt.name}`)
  }

  const clearSavedPromptFromEditNode = () => {
    if (!editNode) return
    setEditNode({
      ...editNode,
      promptId: undefined,
    })
  }

  const saveEditNode = () => {
    if (!editNode) return
    setNodes(nodes.map(n => n.tempId === editNode.tempId ? editNode : n))
    setEditNode(null)
  }

  // Edge rendering helpers
  const getEdgePorts = (edge: ChainEdgeData) => {
    if (edge.sourcePort && edge.targetPort) {
      return { sourcePort: edge.sourcePort, targetPort: edge.targetPort }
    }
    const src = nodes.find(n => n.tempId === edge.sourceTempId)
    const tgt = nodes.find(n => n.tempId === edge.targetTempId)
    if (!src || !tgt) return { sourcePort: 'right' as PortPosition, targetPort: 'left' as PortPosition }
    return getBestPorts(src, tgt)
  }

  const { setBreadcrumbs, setHeaderActions } = useLayoutControls()
  const { stageProps } = useFieldView('chain-builder')

  useLayoutEffect(() => {
    setBreadcrumbs([
      { label: t('nav.dashboard'), path: '/dashboard' },
      { label: t('chains.title'), path: '/chains' },
      { label: id ? chainName || t('chains.title') : t('chains.createNew') }
    ])
    return () => { setBreadcrumbs([]) }
  }, [id, chainName])

  useEffect(() => {
    setHeaderActions(null)
    return () => { setHeaderActions(null) }
  }, [])

  const historyItems = executionResult
    ? [
        { label: 'Workflow executed', time: 'Just now', status: 'success' },
        { label: 'Agent completed', time: `${executionResult.stepResults.length} steps`, status: 'success' },
        { label: 'Output generated', time: `${Math.round(executionResult.executionTimeMs)}ms`, status: 'success' },
      ]
    : [
        { label: 'Workflow ready', time: 'No run yet', status: 'idle' },
        { label: 'Undo available', time: `${undoStack.length} change${undoStack.length === 1 ? '' : 's'}`, status: undoStack.length > 0 ? 'success' : 'idle' },
        { label: 'Nodes on canvas', time: `${nodes.length} total`, status: nodes.length > 0 ? 'success' : 'idle' },
      ]

  // Gamefield: one desk per chain node. Before a run they sit idle; a run seats
  // each node that reported a step result and posts its output downstream.
  const field = useMemo<FieldModel>(() => {
    const byNode = new Map((executionResult?.stepResults ?? []).map(r => [r.nodeId, r]))

    const desks = nodes.map(n => {
      const step = byNode.get(n.tempId)
      return {
        id: `node-${n.tempId}`,
        name: n.label || n.nodeType,
        role: n.nodeType,
        status: (step?.error ? 'failed' : step ? 'done' : executing ? 'working' : 'idle') as 'failed' | 'done' | 'working' | 'idle',
        detail: step?.error ? bubble(step.error) : executing ? 'Running this step…' : bubble(step?.output),
        output: step ? step.output || step.error : undefined,
      }
    })

    return {
      title: chainName || 'Chain',
      subtitle: `${nodes.length} nodes · ${edges.length} links`,
      running: executing,
      agents: [
        ...desks,
        {
          id: 'runner',
          name: 'Runner',
          role: executionResult ? `${executionResult.totalTokens} tokens` : 'chain executor',
          status: (error ? 'failed' : executing ? 'working' : executionResult ? 'done' : 'idle') as 'failed' | 'working' | 'done' | 'idle',
          detail: error ? bubble(error) : executing ? 'Walking the graph…' : bubble(executionResult?.finalOutput),
          output: executionResult?.finalOutput || undefined,
          boss: true,
        },
      ],
      messages: edges
        .filter(e => byNode.has(e.sourceTempId) && byNode.has(e.targetTempId))
        .map(e => ({ id: `edge-${e.sourceTempId}-${e.targetTempId}`, from: `node-${e.sourceTempId}`, to: `node-${e.targetTempId}` })),
      events: (executionResult?.stepResults ?? []).map(r => ({
        id: `step-${r.nodeId}`,
        text: r.error ? `${r.label} failed: ${r.error}` : `${r.label} produced ${r.tokensUsed} tokens.`,
        tone: (r.error ? 'bad' : 'good') as 'bad' | 'good',
      })),
    }
  }, [nodes, edges, executionResult, executing, error, chainName])

  const stageWidth = Math.max(1800, ...nodes.map((node) => node.positionX + 360))
  const stageHeight = Math.max(1200, ...nodes.map((node) => node.positionY + 260))

  return (
    <>
      <CopyTextDialog
        open={!!shareDialogUrl}
        title="Copy workflow link"
        description="Clipboard access was unavailable. Copy the link below to share this workflow."
        value={shareDialogUrl}
        onClose={() => setShareDialogUrl('')}
      />
      <AgentStage {...stageProps} model={field} />

      <div className="cb-layout cb-layout-v2">
        {/* Left Sidebar */}
        <div className="cb-sidebar cb-sidebar-v2">
          {/* Workflow Info */}
          <div className="cb-sidebar-section">
            <div className="cb-section-header cb-section-header-rich" onClick={() => toggleSection('info')}>
              <div className="cb-section-heading-block">
                <div className="cb-section-badge cb-section-badge-primary">
                  <PhosphorIcon className="bi bi-diagram-3"></PhosphorIcon>
                </div>
                <div>
                  <span className="cb-sidebar-eyebrow">Builder</span>
                  <h4 className="cb-sidebar-title">Workflow</h4>
                  <p className="cb-sidebar-subtitle">Set the workflow identity before wiring the canvas.</p>
                </div>
              </div>
              <PhosphorIcon className={`bi bi-chevron-${expanded.info ? 'up' : 'down'} cb-section-chevron`}></PhosphorIcon>
            </div>
            {!expanded.info && chainName && (
              <div className="cb-section-peek">
                <PhosphorIcon className="bi bi-diagram-3" style={{ color: '#833ab4', fontSize: '0.75rem' }}></PhosphorIcon>
                <span className="cb-peek-name">{chainName}</span>
              </div>
            )}
            {expanded.info && (
              <div className="cb-section-body">
                <div className="cb-form-group cb-form-group-modern">
                  <label>Name <span className="cb-required-mark">*</span></label>
                  <input type="text" value={chainName} onChange={(e) => { setChainName(e.target.value); if (error === 'Workflow name is required') setError(null) }} placeholder="My Workflow" />
                </div>
                <div className="cb-form-group cb-form-group-modern">
                  <label>Description</label>
                  <input type="text" value={chainDescription} onChange={(e) => setChainDescription(e.target.value)} placeholder="What does this workflow do?" />
                </div>
                <div className="cb-form-group cb-form-group-modern">
                  <label>Category</label>
                  <input type="text" value={chainCategory} onChange={(e) => setChainCategory(e.target.value)} placeholder="e.g., Content Pipeline" />
                </div>
              </div>
            )}
          </div>

          {/* Node palette — collapsible categories */}
          {NODE_CATEGORIES.map(({ category, nodes: catNodes }) => (
            <div key={category} className="cb-sidebar-section cb-palette-category">
              <div className="cb-section-header cb-section-header-rich" onClick={() => toggleSection(category)}>
                <div className="cb-section-heading-block cb-section-heading-inline">
                  <div className="cb-section-badge">
                    <PhosphorIcon className="bi bi-plus-square"></PhosphorIcon>
                  </div>
                  <div>
                    <h5 className="cb-palette-category-label">{category}</h5>
                    <span className="cb-section-count">{catNodes.length} blocks</span>
                  </div>
                </div>
                <PhosphorIcon className={`bi bi-chevron-${expanded[category] ? 'up' : 'down'} cb-section-chevron`}></PhosphorIcon>
              </div>
              {!expanded[category] && (
                <div className="cb-section-peek cb-palette-peek">
                  {catNodes.slice(0, 5).map(n => (
                    <span key={n.type} className="cb-peek-icon" style={{ color: n.color }} title={n.label}>
                      <PhosphorIcon className={`bi ${n.icon}`}></PhosphorIcon>
                    </span>
                  ))}
                  {catNodes.length > 5 && <span className="cb-peek-more">+{catNodes.length - 5}</span>}
                </div>
              )}
              {expanded[category] && (
                <div className="cb-node-palette">
                  {catNodes.map(({ type, label, icon, color }) => (
                    <button
                      key={type}
                      className="cb-palette-btn"
                      draggable
                      onDragStart={(e) => {
                        draggedNodeTypeRef.current = type
                        e.dataTransfer.effectAllowed = 'copy'
                        e.dataTransfer.setData('text/plain', type)
                      }}
                      onClick={() => addNode(type as NodeType)}
                      style={{ '--node-color': color } as React.CSSProperties}
                      title={label}
                    >
                      <PhosphorIcon className={`bi ${icon}`}></PhosphorIcon>
                      <span>{label}</span>
                    </button>
                  ))}
                </div>
              )}
            </div>
          ))}

          {/* Connections */}
          {edges.length > 0 && (
            <div className="cb-sidebar-section">
              <div className="cb-section-header" onClick={() => toggleSection('connections')}>
                <h4 className="cb-sidebar-title">Connections ({edges.length})</h4>
                <PhosphorIcon className={`bi bi-chevron-${expanded.connections ? 'up' : 'down'} cb-section-chevron`}></PhosphorIcon>
              </div>
              {!expanded.connections && (
                <div className="cb-section-peek">
                  <span className="cb-peek-name">{edges.length} connection{edges.length !== 1 ? 's' : ''}</span>
                </div>
              )}
              {expanded.connections && (
                <div className="cb-edge-list">
                  {edges.map((edge, i) => {
                    const src = nodes.find(n => n.tempId === edge.sourceTempId)
                    const tgt = nodes.find(n => n.tempId === edge.targetTempId)
                    return (
                      <div key={`${edge.sourceTempId}-${edge.targetTempId}-${edge.label || 'edge'}-${i}`} className="cb-edge-item">
                        <span>{src?.label || '?'} → {tgt?.label || '?'}</span>
                        <button className="cb-edge-delete" onClick={() => deleteEdge(i)} title="Remove connection">
                          <PhosphorIcon className="bi bi-x"></PhosphorIcon>
                        </button>
                      </div>
                    )
                  })}
                </div>
              )}
            </div>
          )}
        </div>

        <div className="cb-workspace">
          <div className="cb-builder-topbar">
            <div className="cb-builder-identity">
              <div className="cb-builder-name-wrap">
                <input
                  className="cb-builder-name"
                  type="text"
                  value={chainName}
                  onChange={(e) => { setChainName(e.target.value); if (error === 'Workflow name is required') setError(null) }}
                  placeholder="My Workflow"
                />
                <button type="button" className="cb-topbar-icon-btn" title="Rename workflow">
                  <PhosphorIcon className="bi bi-pencil"></PhosphorIcon>
                </button>
              </div>
              <span className="cb-status-pill">{id ? 'Saved' : 'Draft'}</span>
              <span className="cb-save-status">{saving ? 'Saving changes...' : 'All changes saved'}</span>
            </div>

            <div className="cb-builder-actions">
              <button type="button" className="cb-topbar-icon-btn" onClick={handleUndo} disabled={undoStack.length === 0} title="Undo">
                <PhosphorIcon className="bi bi-arrow-counterclockwise"></PhosphorIcon>
              </button>
              <button type="button" className="cb-topbar-primary-btn" onClick={handleSave} disabled={saving}>
                <PhosphorIcon className="bi bi-floppy"></PhosphorIcon>
                <span>{saving ? 'Saving...' : 'Save'}</span>
              </button>
              <button type="button" className="cb-topbar-secondary-btn" onClick={() => setShowExecutePanel(current => !current)}>
                <PhosphorIcon className="bi bi-beaker"></PhosphorIcon>
                <span>Test</span>
              </button>
              <button type="button" className="cb-topbar-secondary-btn" onClick={handleShareWorkflow}>
                <PhosphorIcon className="bi bi-share"></PhosphorIcon>
                <span>Share</span>
              </button>
            </div>
          </div>

          <div className="cb-canvas-shell">
            <div className="cb-canvas-top-tools">
              <div className="cb-command-stack">
                <div className="cb-command-bar">
                  <PhosphorIcon className="bi bi-stars"></PhosphorIcon>
                  <input
                    type="text"
                    value={commandPrompt}
                    onChange={(e) => setCommandPrompt(e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Enter') handleCommandSubmit() }}
                    placeholder="Type / to add nodes, ask AI, or automate..."
                  />
                  <span className="cb-command-hint">Ctrl+K</span>
                  <button type="button" className="cb-command-submit" onClick={handleCommandSubmit} disabled={aiLoading}>
                    {aiLoading ? <div className="btn-spinner" style={{ width: 14, height: 14, borderWidth: 2 }}></div> : <PhosphorIcon className="bi bi-arrow-right"></PhosphorIcon>}
                  </button>
                </div>

                <div className="cb-quick-actions">
                  <button type="button" className="cb-quick-action" onClick={handleAutoConnect} disabled={aiLoading}>{aiLoading ? <div className="btn-spinner" style={{ width: 12, height: 12, borderWidth: 2 }}></div> : <PhosphorIcon className="bi bi-magic"></PhosphorIcon>} Auto-connect</button>
                  <button type="button" className="cb-quick-action" onClick={handleOptimizeLayout} disabled={aiLoading}>{aiLoading ? <div className="btn-spinner" style={{ width: 12, height: 12, borderWidth: 2 }}></div> : <PhosphorIcon className="bi bi-stars"></PhosphorIcon>} Optimize</button>
                  <button type="button" className="cb-quick-action" onClick={handleExplainWorkflow}><PhosphorIcon className="bi bi-lightbulb"></PhosphorIcon> Explain</button>
                  <button type="button" className="cb-quick-action cb-quick-action-primary" onClick={() => setShowExecutePanel(current => !current)}><PhosphorIcon className="bi bi-play-circle"></PhosphorIcon> Run test</button>
                </div>
              </div>

              <div className="cb-zoom-panel">
                <PhosphorIcon className="bi bi-search"></PhosphorIcon>
                <input type="range" min="50" max="150" value={canvasZoom} onChange={(e) => setCanvasZoom(parseInt(e.target.value))} />
                <span>{canvasZoom}%</span>
                <button type="button" className="cb-topbar-icon-btn" onClick={handleFitCanvas} title="Fit workflow to canvas"><PhosphorIcon className="bi bi-bounding-box"></PhosphorIcon></button>
                <button type="button" className="cb-topbar-icon-btn" onClick={handleCenterCanvas} title="Center canvas"><PhosphorIcon className="bi bi-aspect-ratio"></PhosphorIcon></button>
                <button type="button" className="cb-topbar-icon-btn" onClick={handleExportWorkflow} title="Download workflow snapshot"><PhosphorIcon className="bi bi-camera"></PhosphorIcon></button>
              </div>
            </div>

            <div className="cb-canvas-body">
              {/* Canvas */}
              <div
                ref={canvasRef}
                className={`cb-canvas ${portDrag ? 'port-dragging' : ''} ${interactionMode === 'pan' ? 'cb-canvas-pan-mode' : ''} ${showCanvasGrid ? 'cb-canvas-grid-on' : 'cb-canvas-grid-off'}`}
                onClick={() => {
                  if (interactionMode === 'select') {
                    setSelectedNode(null)
                    setSelectedEdge(null)
                  }
                }}
                onMouseDown={handleCanvasMouseDown}
                onDragOver={(e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy' }}
                onDrop={(e) => {
                  e.preventDefault()
                  const nodeType = draggedNodeTypeRef.current || e.dataTransfer.getData('text/plain')
                  draggedNodeTypeRef.current = null
                  if (!nodeType || !canvasRef.current) return
                  const rect = canvasRef.current.getBoundingClientRect()
                  const x = (e.clientX - rect.left - panRef.current.x) / canvasScale - 80
                  const y = (e.clientY - rect.top - panRef.current.y) / canvasScale - 35
                  const definition = ALL_NODES.find(n => n.type === nodeType)
                  const newNode: ChainNodeData = {
                    tempId: nextTempIdRef.current++,
                    nodeType: nodeType as NodeType,
                    label: definition?.label || nodeType,
                    positionX: x, positionY: y,
                    sortOrder: nodes.length,
                  }
                  setNodes(prev => [...prev, newNode])
                  showToast(`${definition?.label || 'Node'} dropped onto canvas`)
                }}
              >
                {error && (
                  <div className="cb-canvas-error">
                    {error}
                    <button onClick={() => setError(null)}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>
                  </div>
                )}

                <div className="cb-canvas-stage" style={{ transform: `translate(${pan.x}px, ${pan.y}px) scale(${canvasScale})`, transformOrigin: '0 0' }}>
                {/* SVG Edges */}
                <svg className="cb-edges-svg" width={stageWidth} height={stageHeight} style={{ position: 'absolute', left: 0, top: 0, overflow: 'visible' }}>
                <defs>
                  <marker id="arrowhead" markerWidth="10" markerHeight="7" refX="8" refY="3.5" orient="auto">
                    <polygon points="0 0, 10 3.5, 0 7" fill="#111111" />
                  </marker>
                  <marker id="arrowhead-preview" markerWidth="10" markerHeight="7" refX="8" refY="3.5" orient="auto">
                    <polygon points="0 0, 10 3.5, 0 7" fill="#111111" />
                  </marker>
                </defs>

                {/* Rendered edges as bezier curves */}
                {edges.map((edge, i) => {
              const sourceNode = nodes.find(n => n.tempId === edge.sourceTempId)
              const targetNode = nodes.find(n => n.tempId === edge.targetTempId)
              if (!sourceNode || !targetNode) return null

              const ports = getEdgePorts(edge)
              const from = getPortPosition(sourceNode, ports.sourcePort, nodeRefs.current.get(edge.sourceTempId))
              const to = getPortPosition(targetNode, ports.targetPort, nodeRefs.current.get(edge.targetTempId))
              const cp = getBezierControlPoints(from, to, ports.sourcePort, ports.targetPort)

              const labelPos = edge.label
                ? bezierPointAt(from, { x: cp.cp1x, y: cp.cp1y }, { x: cp.cp2x, y: cp.cp2y }, to, 0.5)
                : null

                  return (
                    <g key={`${edge.sourceTempId}-${edge.targetTempId}-${edge.label || 'edge'}-${i}`}>
                      <path
                        d={`M ${from.x} ${from.y} C ${cp.cp1x} ${cp.cp1y}, ${cp.cp2x} ${cp.cp2y}, ${to.x} ${to.y}`}
                        stroke="transparent"
                        strokeWidth="16"
                        fill="none"
                        className="cb-edge-hitbox"
                        onClick={(event) => {
                          event.stopPropagation()
                          setSelectedNode(null)
                          setSelectedEdge(i)
                        }}
                      />
                      <path
                        d={`M ${from.x} ${from.y} C ${cp.cp1x} ${cp.cp1y}, ${cp.cp2x} ${cp.cp2y}, ${to.x} ${to.y}`}
                        stroke="#111111"
                        strokeWidth="3"
                        fill="none"
                        className={selectedEdge === i ? 'cb-edge-path is-selected' : 'cb-edge-path'}
                        markerEnd="url(#arrowhead)"
                      />
                  {edge.label && labelPos && (
                    <text
                      x={labelPos.x}
                      y={labelPos.y - 8}
                      textAnchor="middle"
                      fontSize="11"
                      fill="#111111"
                      className={selectedEdge === i ? 'cb-edge-label is-selected' : 'cb-edge-label'}
                      onClick={(event) => {
                        event.stopPropagation()
                        setSelectedNode(null)
                        setSelectedEdge(i)
                      }}
                    >
                      {edge.label}
                    </text>
                      )}
                    </g>
                  )
                })}

                {/* Preview line during port drag */}
                {portDrag && (() => {
              const sourceNode = nodes.find(n => n.tempId === portDrag.sourceNodeId)
              if (!sourceNode) return null
              const from = getPortPosition(sourceNode, portDrag.sourcePort, nodeRefs.current.get(portDrag.sourceNodeId))
              const to = { x: portDrag.mouseX, y: portDrag.mouseY }
              const cp = getBezierControlPoints(from, to, portDrag.sourcePort, null)

                  return (
                    <path
                      d={`M ${from.x} ${from.y} C ${cp.cp1x} ${cp.cp1y}, ${cp.cp2x} ${cp.cp2y}, ${to.x} ${to.y}`}
                      stroke="#111111"
                      strokeWidth="2"
                      strokeDasharray="6 3"
                      fill="none"
                      opacity="0.8"
                      markerEnd="url(#arrowhead-preview)"
                    />
                  )
                })()}
              </svg>

              {/* Nodes */}
              {nodes.map(node => (
            <div
              key={node.tempId}
              ref={(el) => { if (el) nodeRefs.current.set(node.tempId, el); else nodeRefs.current.delete(node.tempId); }}
              className={`cb-node cb-node-${node.nodeType} ${selectedNode === node.tempId ? 'selected' : ''} ${draggingNode === node.tempId ? 'dragging' : ''}`}
              style={{ left: node.positionX, top: node.positionY, '--node-color': NODE_COLORS[node.nodeType] } as React.CSSProperties}
              onClick={(e) => {
                e.stopPropagation()
                if (interactionMode === 'select') {
                  setSelectedEdge(null)
                  setSelectedNode(node.tempId)
                }
              }}
              onMouseDown={(e) => handleMouseDown(node.tempId, e)}
              onDoubleClick={(e) => { if (interactionMode !== 'select') return; e.stopPropagation(); setEditNode({ ...node }) }}
            >
              <div className="cb-node-inner">
                <div className="cb-node-icon-wrap" style={{ background: NODE_COLORS[node.nodeType] + '22', borderColor: NODE_COLORS[node.nodeType] + '44' }}>
                  <PhosphorIcon className={`bi ${NODE_ICONS[node.nodeType]} cb-node-icon`} style={{ color: NODE_COLORS[node.nodeType] }}></PhosphorIcon>
                </div>
                <div className="cb-node-body">
                  <div className="cb-node-label">{node.label}</div>
                  {(node.nodeType === 'prompt' || node.nodeType === 'agent') && node.inlinePrompt && (
                    <div className="cb-node-preview">{node.inlinePrompt.substring(0, 40)}…</div>
                  )}
                  {node.nodeType === 'condition' && node.conditionExpression && (
                    <div className="cb-node-preview">{node.conditionExpression}</div>
                  )}
                  {node.nodeType === 'transform' && node.transformExpression && (
                    <div className="cb-node-preview">{node.transformExpression}</div>
                  )}
                  {node.nodeType === 'http_request' && node.httpUrl && (
                    <div className="cb-node-preview">{node.httpMethod || 'GET'} {node.httpUrl.substring(0, 24)}…</div>
                  )}
                  {node.nodeType === 'web_search' && (
                    <div className="cb-node-preview">{node.inlinePrompt ? node.inlinePrompt.substring(0, 36) + '…' : '{{input}}'}</div>
                  )}
                  {node.nodeType === 'set_variable' && node.variableName && (
                    <div className="cb-node-preview">{node.variableName} = {node.variableValue?.substring(0, 16)}</div>
                  )}
                  {node.nodeType === 'delay' && node.delaySeconds && (
                    <div className="cb-node-preview">{node.delaySeconds}s delay</div>
                  )}
                  {node.nodeType === 'note' && node.noteText && (
                    <div className="cb-node-preview">{node.noteText.substring(0, 40)}…</div>
                  )}
                  {node.nodeType === 'classify' && node.classifyLabels && (
                    <div className="cb-node-preview">{node.classifyLabels.substring(0, 30)}…</div>
                  )}
                  {node.nodeType === 'loop' && node.loopCondition && (
                    <div className="cb-node-preview">{node.loopCondition}</div>
                  )}
                  {node.nodeType === 'mcp' && node.mcpToolName && (
                    <div className="cb-node-preview">{node.mcpToolName}</div>
                  )}
                </div>
              </div>

              {/* Connector port dots — notes are informational, no connectors */}
              {node.nodeType !== 'note' && PORTS.map(port => (
                <div
                  key={port}
                  className={`cb-port cb-port-${port} ${
                    hoveredPort?.nodeId === node.tempId && hoveredPort?.port === port ? 'cb-port-hover' : ''
                  }`}
                  onMouseDown={(e) => handlePortMouseDown(node.tempId, port, e)}
                  onMouseEnter={() => { hoveredPortRef.current = { nodeId: node.tempId, port }; setHoveredPort({ nodeId: node.tempId, port }) }}
                  onMouseLeave={() => { hoveredPortRef.current = null; setHoveredPort(null) }}
                />
              ))}

              <div className="cb-node-actions">
                <button onClick={(e) => { e.stopPropagation(); setEditNode({ ...node }) }} className="cb-node-action-btn edit" title="Edit">
                  <PhosphorIcon className="bi bi-pencil"></PhosphorIcon>
                </button>
                <button onClick={(e) => { e.stopPropagation(); deleteNode(node.tempId) }} className="cb-node-action-btn delete" title="Delete">
                  <PhosphorIcon className="bi bi-trash"></PhosphorIcon>
                </button>
              </div>
            </div>
              ))}

              </div>
              </div>

              {/* Empty state — outside transform stage so 50%/50% is relative to the canvas container */}
              {nodes.length === 0 && (
                <div className="cb-canvas-empty">
                  <PhosphorIcon className="bi bi-diagram-3"></PhosphorIcon>
                  <h3>Start building your workflow</h3>
                  <p>Add nodes from the sidebar, then drag between the connector dots to link them</p>
                </div>
              )}

              <div className="cb-canvas-overlay">
                <div className="cb-history-card">
                  <div className="cb-history-header">
                    <h4>Run History</h4>
                    <span className="cb-history-status">{executionResult ? 'All systems go' : 'Awaiting run'}</span>
                  </div>
                  <div className="cb-history-list">
                    {historyItems.map(item => (
                      <div key={item.label} className="cb-history-item">
                        <span className={`cb-history-dot ${item.status}`}></span>
                        <div>
                          <strong>{item.label}</strong>
                          <span>{item.time}</span>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>

                <div className="cb-insight-card">
                  <div className="cb-insight-header">
                    <h4>Workflow Insight</h4>
                    <button type="button" className="cb-insight-refresh" onClick={handleExplainWorkflow} title="Refresh explanation">
                      <PhosphorIcon className="bi bi-arrow-repeat"></PhosphorIcon>
                    </button>
                  </div>
                  <p>{workflowInsight}</p>
                </div>

                <div className="cb-bottom-toolbar">
                  <button type="button" className={interactionMode === 'select' ? 'active' : ''} onClick={() => setInteractionMode('select')} title="Select mode"><PhosphorIcon className="bi bi-cursor"></PhosphorIcon></button>
                  <button type="button" className={interactionMode === 'pan' ? 'active' : ''} onClick={() => setInteractionMode('pan')} title="Pan mode"><PhosphorIcon className="bi bi-hand-index"></PhosphorIcon></button>
                  <button type="button" onClick={() => setCanvasZoom((current) => Math.min(150, current + 10))} title="Zoom in"><PhosphorIcon className="bi bi-zoom-in"></PhosphorIcon></button>
                  <button type="button" className={showCanvasGrid ? 'active' : ''} onClick={() => setShowCanvasGrid((current) => !current)} title="Toggle grid"><PhosphorIcon className="bi bi-grid-3x3-gap"></PhosphorIcon></button>
                  <button type="button" onClick={handleUndo} disabled={undoStack.length === 0}><PhosphorIcon className="bi bi-arrow-counterclockwise"></PhosphorIcon></button>
                  <button type="button" onClick={handleOptimizeLayout}><PhosphorIcon className="bi bi-arrow-repeat"></PhosphorIcon></button>
                </div>
              </div>

              {/* Execution Panel */}
              {showExecutePanel && (
                <div className="cb-exec-panel cb-exec-panel-floating">
                  <div className="cb-exec-header">
                    <h4><PhosphorIcon className="bi bi-play-fill"></PhosphorIcon> Execute Workflow</h4>
                    <button className="cb-exec-close" onClick={() => setShowExecutePanel(false)}><PhosphorIcon className="bi bi-x-lg"></PhosphorIcon></button>
                  </div>

                  <div className="cb-exec-body">
                    <div className="cb-form-group">
                      <label>Input</label>
                      <textarea value={executeInput} onChange={(e) => setExecuteInput(e.target.value)} rows={4} placeholder="Enter text to process through the workflow..." />
                    </div>
                    <button className="btn btn-primary cb-exec-run-btn" onClick={handleExecute} disabled={executing || !id}>
                      {executing ? <><div className="btn-spinner"></div> {t('chains.running')}</> : <><PhosphorIcon className="bi bi-play-fill"></PhosphorIcon> {t('chains.run')}</>}
                    </button>

                    {!id && <p className="cb-exec-hint">Save the workflow first to enable execution.</p>}

                    {executionResult && (
                      <div className="cb-exec-results">
                        <div className="cb-exec-result-card cb-exec-final">
                          <h5>Final Output</h5>
                          <pre>{executionResult.finalOutput}</pre>
                        </div>
                        <div className="cb-exec-meta">
                          <span><PhosphorIcon className="bi bi-coin"></PhosphorIcon> {executionResult.totalTokens} tokens</span>
                          <span><PhosphorIcon className="bi bi-stopwatch"></PhosphorIcon> {Math.round(executionResult.executionTimeMs)}ms</span>
                        </div>

                        <h5 className="cb-exec-steps-title">Steps ({executionResult.stepResults.length})</h5>
                        {executionResult.stepResults.map((step, i) => (
                          <div key={i} className={`cb-exec-step ${step.error ? 'error' : ''}`}>
                            <div className="cb-exec-step-header">
                              <span className="cb-exec-step-badge" style={{ background: NODE_COLORS[step.nodeType] || '#64748b' }}>
                                <PhosphorIcon className={`bi ${NODE_ICONS[step.nodeType] || 'bi-circle'}`}></PhosphorIcon> {step.label}
                              </span>
                              {step.tokensUsed > 0 && <span className="cb-exec-step-tokens">{step.tokensUsed} tokens</span>}
                            </div>
                            <div className="cb-exec-step-output">
                              {step.output.substring(0, 300)}{step.output.length > 300 ? '...' : ''}
                            </div>
                            {step.error && <div className="cb-exec-step-error"><PhosphorIcon className="bi bi-exclamation-triangle"></PhosphorIcon> {step.error}</div>}
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Edit Node Modal */}
      {editNode && (
        <div className="modal-overlay" onClick={() => setEditNode(null)}>
          <div className="modal-content" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <div className="cb-modal-node-badge" style={{ background: NODE_COLORS[editNode.nodeType] + '22', borderColor: NODE_COLORS[editNode.nodeType] + '55' }}>
                <PhosphorIcon className={`bi ${NODE_ICONS[editNode.nodeType]}`} style={{ color: NODE_COLORS[editNode.nodeType] }}></PhosphorIcon>
                <span style={{ color: NODE_COLORS[editNode.nodeType], fontWeight: 600 }}>{NODE_LABELS[editNode.nodeType]}</span>
              </div>
              <button className="modal-close" onClick={() => setEditNode(null)}>&times;</button>
            </div>
            <div className="modal-body">
              <div className="form-group">
                <label>Label</label>
                <input type="text" value={editNode.label} onChange={(e) => updateEditNode('label', e.target.value)} />
              </div>

              {/* Core: Agent */}
              {editNode.nodeType === 'agent' && (<>
                <div className="form-group">
                  <label>Saved Prompt</label>
                  <div className="cb-saved-prompt-row">
                    <PromptPicker label={editNode.promptId ? 'Replace saved prompt' : 'Use saved prompt'} onSelect={applySavedPromptToEditNode} />
                    {editNode.promptId && (
                      <button type="button" className="cb-saved-prompt-clear" onClick={clearSavedPromptFromEditNode}>
                        Clear link
                      </button>
                    )}
                  </div>
                  <p className="cb-form-hint">Selecting a saved prompt copies its current content into this node and keeps the prompt id linked.</p>
                </div>
                <div className="form-group">
                  <label>System Prompt</label>
                  <textarea value={editNode.systemPrompt || ''} onChange={(e) => updateEditNode('systemPrompt', e.target.value)} rows={3} placeholder="You are a helpful assistant that..." />
                </div>
                <div className="form-group">
                  <label>User Prompt / Instruction</label>
                  <textarea value={editNode.inlinePrompt || ''} onChange={(e) => updateEditNode('inlinePrompt', e.target.value)} rows={4} placeholder="Summarize the following: {{input}}" />
                  <p className="cb-form-hint">Use {'{{input}}'} to reference the previous node's output.</p>
                </div>
              </>)}

              {/* Core: Classify */}
              {editNode.nodeType === 'classify' && (
                <div className="form-group">
                  <label>Labels (comma-separated)</label>
                  <input type="text" value={editNode.classifyLabels || ''} onChange={(e) => updateEditNode('classifyLabels', e.target.value)} placeholder="positive, negative, neutral" />
                  <p className="cb-form-hint">AI will classify the input into one of these categories.</p>
                </div>
              )}

              {/* Core: Note */}
              {editNode.nodeType === 'note' && (
                <div className="form-group">
                  <label>Note Text</label>
                  <textarea value={editNode.noteText || ''} onChange={(e) => updateEditNode('noteText', e.target.value)} rows={4} placeholder="Add documentation or comments here..." />
                </div>
              )}

              {/* Tools: Prompt */}
              {editNode.nodeType === 'prompt' && (<>
                <div className="form-group">
                  <label>Saved Prompt</label>
                  <div className="cb-saved-prompt-row">
                    <PromptPicker label={editNode.promptId ? 'Replace saved prompt' : 'Use saved prompt'} onSelect={applySavedPromptToEditNode} />
                    {editNode.promptId && (
                      <button type="button" className="cb-saved-prompt-clear" onClick={clearSavedPromptFromEditNode}>
                        Clear link
                      </button>
                    )}
                  </div>
                  <p className="cb-form-hint">Selecting a saved prompt copies its current content into this node and keeps the prompt id linked.</p>
                </div>
                <div className="form-group">
                  <label>Prompt</label>
                  <textarea value={editNode.inlinePrompt || ''} onChange={(e) => updateEditNode('inlinePrompt', e.target.value)} rows={5} placeholder="Enter the prompt text. Use {{input}} for previous output..." />
                </div>
              </>)}

              {/* Tools: HTTP Request */}
              {editNode.nodeType === 'http_request' && (<>
                <div className="form-group">
                  <label>URL</label>
                  <input type="text" value={editNode.httpUrl || ''} onChange={(e) => updateEditNode('httpUrl', e.target.value)} placeholder="https://api.example.com/endpoint" />
                </div>
                <div className="form-group">
                  <label>Method</label>
                  <select value={editNode.httpMethod || 'GET'} onChange={(e) => updateEditNode('httpMethod', e.target.value)}>
                    <option>GET</option><option>POST</option><option>PUT</option><option>DELETE</option><option>PATCH</option>
                  </select>
                </div>
                <div className="form-group">
                  <label>Body (JSON, optional)</label>
                  <textarea value={editNode.httpBody || ''} onChange={(e) => updateEditNode('httpBody', e.target.value)} rows={3} placeholder='{"key": "{{input}}"}' />
                </div>
              </>)}

              {/* Tools: Web Search */}
              {editNode.nodeType === 'web_search' && (
                <div className="form-group">
                  <label>Search Query</label>
                  <input
                    type="text"
                    value={editNode.inlinePrompt || ''}
                    onChange={(e) => updateEditNode('inlinePrompt', e.target.value)}
                    placeholder="{{input}}"
                  />
                  <p className="cb-form-hint">Use <code>{'{{input}}'}</code> to pass the previous node's output as the search query. Leave blank to search the raw input.</p>
                </div>
              )}

              {/* Tools: Code */}
              {editNode.nodeType === 'code' && (
                <div className="form-group">
                  <label>Code Snippet (JavaScript)</label>
                  <textarea value={editNode.codeSnippet || ''} onChange={(e) => updateEditNode('codeSnippet', e.target.value)} rows={6} placeholder={'// input is available as `input`\nreturn input.toUpperCase()'} style={{ fontFamily: 'monospace', fontSize: '0.85rem' }} />
                  <p className="cb-form-hint">Return a string value. Use <code>input</code> to access the previous node's output.</p>
                </div>
              )}

              {/* Logic: Condition */}
              {editNode.nodeType === 'condition' && (
                <div className="form-group">
                  <label>Condition Expression</label>
                  <input type="text" value={editNode.conditionExpression || ''} onChange={(e) => updateEditNode('conditionExpression', e.target.value)} placeholder="e.g., contains:error or length_gt:100" />
                  <p className="cb-form-hint">contains:text · not_contains:text · length_gt:N · length_lt:N · is_empty · is_not_empty</p>
                </div>
              )}

              {/* Logic: Switch */}
              {editNode.nodeType === 'switch' && (
                <div className="form-group">
                  <label>Switch Expression</label>
                  <textarea value={editNode.switchExpression || ''} onChange={(e) => updateEditNode('switchExpression', e.target.value)} rows={4} placeholder={"case1: contains:urgent\ncase2: contains:billing\ndefault"} />
                  <p className="cb-form-hint">One rule per line. Label edges with the matching case name.</p>
                </div>
              )}

              {/* Logic: Loop */}
              {editNode.nodeType === 'loop' && (<>
                <div className="form-group">
                  <label>Loop Condition</label>
                  <input type="text" value={editNode.loopCondition || ''} onChange={(e) => updateEditNode('loopCondition', e.target.value)} placeholder="e.g., contains:DONE" />
                  <p className="cb-form-hint">Loop continues while condition is false.</p>
                </div>
                <div className="form-group">
                  <label>Max Iterations</label>
                  <input type="number" value={editNode.loopMax ?? 5} onChange={(e) => updateEditNode('loopMax', parseInt(e.target.value))} min={1} max={20} />
                </div>
              </>)}

              {/* Logic: Delay */}
              {editNode.nodeType === 'delay' && (
                <div className="form-group">
                  <label>Delay (seconds)</label>
                  <input type="number" value={editNode.delaySeconds ?? 1} onChange={(e) => updateEditNode('delaySeconds', parseInt(e.target.value))} min={1} max={60} />
                </div>
              )}

              {/* Data: Transform */}
              {editNode.nodeType === 'transform' && (
                <div className="form-group">
                  <label>Transform Expression</label>
                  <input type="text" value={editNode.transformExpression || ''} onChange={(e) => updateEditNode('transformExpression', e.target.value)} placeholder="e.g., uppercase, first_line, first:100" />
                  <p className="cb-form-hint">uppercase · lowercase · trim · first_line · last_line · word_count · char_count · first:N · last:N · replace:old→new</p>
                </div>
              )}

              {/* Data: Set Variable */}
              {editNode.nodeType === 'set_variable' && (<>
                <div className="form-group">
                  <label>Variable Name</label>
                  <input type="text" value={editNode.variableName || ''} onChange={(e) => updateEditNode('variableName', e.target.value)} placeholder="my_variable" />
                </div>
                <div className="form-group">
                  <label>Value (or {'{{input}}'} for previous output)</label>
                  <input type="text" value={editNode.variableValue || ''} onChange={(e) => updateEditNode('variableValue', e.target.value)} placeholder="{{input}}" />
                </div>
              </>)}

              {/* Data: Format */}
              {editNode.nodeType === 'format' && (
                <div className="form-group">
                  <label>Format Template</label>
                  <textarea value={editNode.formatTemplate || ''} onChange={(e) => updateEditNode('formatTemplate', e.target.value)} rows={4} placeholder={"## Summary\n{{input}}\n\n---\nGenerated by GenPrompt"} />
                  <p className="cb-form-hint">Use {'{{input}}'} to insert the previous output.</p>
                </div>
              )}

              {/* Data: Filter */}
              {editNode.nodeType === 'filter' && (
                <div className="form-group">
                  <label>Filter Expression</label>
                  <input type="text" value={editNode.filterExpression || ''} onChange={(e) => updateEditNode('filterExpression', e.target.value)} placeholder="e.g., min_length:50 or not_contains:error" />
                  <p className="cb-form-hint">Passes input through if condition is met, blocks if not. min_length:N · max_length:N · not_contains:text · contains:text</p>
                </div>
              )}

              {/* Logic: User Approval */}
              {editNode.nodeType === 'user_approval' && (<>
                <div className="form-group">
                  <label>Approval Message</label>
                  <textarea value={editNode.approvalMessage || ''} onChange={(e) => updateEditNode('approvalMessage', e.target.value)} rows={3} placeholder="Please review and approve to continue..." />
                  <p className="cb-form-hint">Message shown to the user when awaiting approval. Use {'{{input}}'} to show the previous output.</p>
                </div>
                <div className="form-group">
                  <label>Approve Button Label</label>
                  <input type="text" value={editNode.approvalButtonLabel || ''} onChange={(e) => updateEditNode('approvalButtonLabel', e.target.value)} placeholder="Approve" />
                </div>
                <div className="form-group">
                  <label>Timeout (seconds, 0 = no timeout)</label>
                  <input type="number" value={editNode.approvalTimeout ?? 0} onChange={(e) => updateEditNode('approvalTimeout', parseInt(e.target.value))} min={0} max={3600} />
                  <p className="cb-form-hint">If the user does not respond within this time, the chain will stop. Set to 0 to wait indefinitely.</p>
                </div>
              </>)}

              {/* Tools: MCP */}
              {editNode.nodeType === 'file_search' && (<>
                <div className="modal-field">
                  <label>Search Query</label>
                  <input type="text" value={editNode.fileSearchQuery || ''} onChange={(e) => updateEditNode('fileSearchQuery', e.target.value)} placeholder="e.g., {{input}} or a fixed keyword" />
                  <span className="modal-hint">Use {'{{input}}'} to search with previous node's output</span>
                </div>
                <div className="modal-field">
                  <label>File Type Filter</label>
                  <input type="text" value={editNode.fileSearchTypes || ''} onChange={(e) => updateEditNode('fileSearchTypes', e.target.value)} placeholder="e.g., .pdf, .txt, .md (comma separated)" />
                </div>
                <div className="modal-field">
                  <label>Max Results</label>
                  <input type="number" value={editNode.fileSearchMaxResults ?? 10} onChange={(e) => updateEditNode('fileSearchMaxResults', parseInt(e.target.value))} min={1} max={100} />
                </div>
              </>)}
              {editNode.nodeType === 'merge' && (<>
                <div className="modal-field">
                  <label>Merge Strategy</label>
                  <select value={editNode.mergeStrategy || 'concatenate'} onChange={(e) => updateEditNode('mergeStrategy', e.target.value)}>
                    <option value="concatenate">Concatenate — join all inputs in order</option>
                    <option value="join">Join with separator</option>
                    <option value="first">First — use only the first input</option>
                    <option value="last">Last — use only the last input</option>
                    <option value="json_array">JSON Array — wrap all inputs in an array</option>
                  </select>
                </div>
                {(editNode.mergeStrategy === 'join' || !editNode.mergeStrategy || editNode.mergeStrategy === 'concatenate') && (
                  <div className="modal-field">
                    <label>Separator</label>
                    <input type="text" value={editNode.mergeSeparator ?? '\n\n'} onChange={(e) => updateEditNode('mergeSeparator', e.target.value)} placeholder="\n\n" />
                    <span className="modal-hint">Inserted between each merged input. Use \n for newline.</span>
                  </div>
                )}
              </>)}
              {editNode.nodeType === 'mcp' && (<>
                <div className="form-group">
                  <label>MCP Server URL</label>
                  <input type="text" value={editNode.mcpServerUrl || ''} onChange={(e) => updateEditNode('mcpServerUrl', e.target.value)} placeholder="http://localhost:3000/mcp" />
                  <p className="cb-form-hint">HTTP endpoint of your MCP server (supports JSON-RPC 2.0 over HTTP or SSE transport).</p>
                </div>
                <div className="form-group">
                  <label>Tool Name</label>
                  <input type="text" value={editNode.mcpToolName || ''} onChange={(e) => updateEditNode('mcpToolName', e.target.value)} placeholder="e.g., read_file, search, query_db" />
                  <p className="cb-form-hint">Exact name of the tool exposed by the MCP server to invoke.</p>
                </div>
                <div className="form-group">
                  <label>Arguments (JSON, optional)</label>
                  <textarea value={editNode.mcpArguments || ''} onChange={(e) => updateEditNode('mcpArguments', e.target.value)} rows={4} placeholder={'{\n  "path": "{{input}}",\n  "limit": 100\n}'} style={{ fontFamily: 'monospace', fontSize: '0.82rem' }} />
                  <p className="cb-form-hint">JSON object passed as tool arguments. Use <code>{'{{input}}'}</code> to inject the previous node's output. Leave blank to send <code>{'{"input": "..."}'}</code> automatically.</p>
                </div>
              </>)}
            </div>
            <div className="modal-footer">
              <button className="btn btn-secondary" onClick={() => setEditNode(null)}>{t('common.cancel')}</button>
              <button className="btn btn-primary" onClick={saveEditNode}><PhosphorIcon className="bi bi-check-lg"></PhosphorIcon> {t('common.save')}</button>
            </div>
          </div>
        </div>
      )}

      {toast.visible && <div className="toast-notification">{toast.message}</div>}
    </>
  )
}

export default ChainBuilder
