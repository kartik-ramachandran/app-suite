import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import MediaStudioPage from './pages/MediaStudio'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/media-studio" replace /> : <StandaloneSignIn />} />
      <Route path="/media-studio" element={<RequireAuth><MediaStudioPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/media-studio" replace />} />
    </Routes>
  )
}
