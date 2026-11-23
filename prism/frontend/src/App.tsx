import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import PrismPage from './pages/Prism'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/prism" replace /> : <StandaloneSignIn />} />
      <Route path="/prism" element={<RequireAuth><PrismPage /></RequireAuth>} />
      <Route path="/prism/:toolName" element={<RequireAuth><PrismPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/prism" replace />} />
    </Routes>
  )
}
