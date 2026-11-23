import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import AgentsPage from './pages/Agents'
import AgentChatPage from './pages/AgentChat'
import BotAgentsPage from './pages/BotAgents'
import BotChatPage from './pages/BotChat'
import CoordinatorPage from './pages/Coordinator'
import OrchestratorPage from './pages/Orchestrator'
import ConnectionsPage from './pages/Connections'
import JobsPage from './pages/Jobs'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/agents" replace /> : <StandaloneSignIn />} />
      <Route path="/agents" element={<RequireAuth><AgentsPage /></RequireAuth>} />
      <Route path="/agent-chat" element={<RequireAuth><AgentChatPage /></RequireAuth>} />
      <Route path="/bot-agents" element={<RequireAuth><BotAgentsPage /></RequireAuth>} />
      <Route path="/bot-chat/:botType" element={<RequireAuth><BotChatPage /></RequireAuth>} />
      <Route path="/coordinator" element={<RequireAuth><CoordinatorPage /></RequireAuth>} />
      <Route path="/orchestrator" element={<RequireAuth><OrchestratorPage /></RequireAuth>} />
      <Route path="/connections" element={<RequireAuth><ConnectionsPage /></RequireAuth>} />
      <Route path="/jobs" element={<RequireAuth><JobsPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/agents" replace />} />
    </Routes>
  )
}
