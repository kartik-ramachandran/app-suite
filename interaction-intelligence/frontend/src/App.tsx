import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import InteractionIntelligencePage from './pages/InteractionIntelligence'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/interaction-intelligence" replace /> : <StandaloneSignIn />} />
      <Route path="/interaction-intelligence" element={<RequireAuth><InteractionIntelligencePage /></RequireAuth>} />
      <Route path="/interaction-intelligence/:toolName" element={<RequireAuth><InteractionIntelligencePage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/interaction-intelligence" replace />} />
    </Routes>
  )
}
