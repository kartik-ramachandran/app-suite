import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import EmmdStudioPage from './pages/EmmdStudio'
import EmmdViewerPage from './pages/EmmdViewer'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/emmd" replace /> : <StandaloneSignIn />} />
      <Route path="/emmd" element={<RequireAuth><EmmdStudioPage /></RequireAuth>} />
      <Route path="/emmd/view/:id" element={<RequireAuth><EmmdViewerPage /></RequireAuth>} />
      <Route path="/emmd/doc/:id" element={<RequireAuth><EmmdStudioPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/emmd" replace />} />
    </Routes>
  )
}
