import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import KitBuilderPage from './pages/KitBuilder'
import WorkspacePage from './pages/Workspace'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/kit-builder" replace /> : <StandaloneSignIn />} />
      <Route path="/kit-builder" element={<RequireAuth><KitBuilderPage /></RequireAuth>} />
      <Route path="/workspace" element={<RequireAuth><WorkspacePage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/kit-builder" replace />} />
    </Routes>
  )
}
