import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import BillTrackerPage from './pages/BillTracker'
import DocAgentPage from './pages/DocAgent'
import WarrantyVaultPage from './pages/WarrantyVault'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/bills" replace /> : <StandaloneSignIn />} />
      <Route path="/bills" element={<RequireAuth><BillTrackerPage /></RequireAuth>} />
      <Route path="/invoice-agent" element={<RequireAuth><DocAgentPage /></RequireAuth>} />
      <Route path="/warranty" element={<RequireAuth><WarrantyVaultPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/bills" replace />} />
    </Routes>
  )
}
