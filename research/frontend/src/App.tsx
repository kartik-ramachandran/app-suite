import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import ResearchPage from './pages/Research'
import SwarmPage from './pages/Swarm'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/research" replace /> : <StandaloneSignIn />} />
      <Route path="/research" element={<RequireAuth><ResearchPage /></RequireAuth>} />
      <Route path="/swarm" element={<RequireAuth><SwarmPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/research" replace />} />
    </Routes>
  )
}
