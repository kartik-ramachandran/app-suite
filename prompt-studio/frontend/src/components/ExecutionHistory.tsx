import PhosphorIcon from './PhosphorIcon'
import { useState, useEffect } from 'react'
import { getExecutions, deleteExecution } from '../services/api'

interface Execution {
  id: string
  promptName: string
  model: string
  totalTokensUsed: number
  createdAt: string
  updatedAt: string
  messageCount: number
}

interface ExecutionHistoryProps {
  promptId: string
  onSelect: (executionId: string) => void
  onClose: () => void
}

function ExecutionHistory({ promptId, onSelect, onClose }: ExecutionHistoryProps) {
  const [executions, setExecutions] = useState<Execution[]>([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    fetchExecutions()
  }, [promptId])

  const fetchExecutions = async () => {
    setLoading(true)
    try {
      const promptFilter = promptId !== 'unsaved' ? promptId : undefined
      const response = await getExecutions(promptFilter)
      setExecutions(response.data.executions)
    } catch {
      // silent fail
    } finally {
      setLoading(false)
    }
  }

  const handleDelete = async (e: React.MouseEvent, id: string) => {
    e.stopPropagation()
    try {
      await deleteExecution(id)
      setExecutions(prev => prev.filter(ex => ex.id !== id))
    } catch {
      // silent fail
    }
  }

  const formatDate = (dateStr: string) => {
    const date = new Date(dateStr)
    const now = new Date()
    const diffMs = now.getTime() - date.getTime()
    const diffMins = Math.floor(diffMs / 60000)
    const diffHours = Math.floor(diffMs / 3600000)
    const diffDays = Math.floor(diffMs / 86400000)

    if (diffMins < 1) return 'Just now'
    if (diffMins < 60) return `${diffMins}m ago`
    if (diffHours < 24) return `${diffHours}h ago`
    if (diffDays < 7) return `${diffDays}d ago`
    return date.toLocaleDateString()
  }

  return (
    <div className="execution-history-panel">
      <div className="execution-history-header">
        <h4><PhosphorIcon className="bi bi-clock-history"></PhosphorIcon> History</h4>
        <button className="btn btn-sm" onClick={onClose}>&times;</button>
      </div>
      <div className="execution-history-list">
        {loading && <div className="execution-history-loading">Loading...</div>}
        {!loading && executions.length === 0 && (
          <div className="execution-history-empty">No past executions</div>
        )}
        {executions.map(exec => (
          <div
            key={exec.id}
            className="execution-history-item"
            onClick={() => onSelect(exec.id)}
          >
            <div className="execution-history-item-header">
              <span className="execution-history-model">{exec.model}</span>
              <span className="execution-history-time">{formatDate(exec.createdAt)}</span>
            </div>
            <div className="execution-history-item-meta">
              <span>{exec.messageCount} messages</span>
              <span>{exec.totalTokensUsed.toLocaleString()} tokens</span>
              <button
                className="execution-history-delete"
                onClick={(e) => handleDelete(e, exec.id)}
                title="Delete"
              >
                <PhosphorIcon className="bi bi-trash"></PhosphorIcon>
              </button>
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}

export default ExecutionHistory
