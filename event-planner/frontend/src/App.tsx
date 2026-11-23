import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import EventPlannerPage from './pages/EventPlanner'
import GiftFinderPage from './pages/GiftFinder'
import ProposalGeneratorPage from './pages/ProposalGenerator'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/event-planner" replace /> : <StandaloneSignIn />} />
      <Route path="/event-planner" element={<RequireAuth><EventPlannerPage /></RequireAuth>} />
      <Route path="/gift-finder" element={<RequireAuth><GiftFinderPage /></RequireAuth>} />
      <Route path="/proposal" element={<RequireAuth><ProposalGeneratorPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/event-planner" replace />} />
    </Routes>
  )
}
