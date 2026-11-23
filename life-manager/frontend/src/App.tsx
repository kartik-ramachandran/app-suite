import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import LifeTrackerPage from './pages/LifeTracker'
import MedicineCabinetPage from './pages/MedicineCabinet'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/life-tracker" replace /> : <StandaloneSignIn />} />
      <Route path="/life-tracker" element={<RequireAuth><LifeTrackerPage /></RequireAuth>} />
      <Route path="/medicine" element={<RequireAuth><MedicineCabinetPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/life-tracker" replace />} />
    </Routes>
  )
}
