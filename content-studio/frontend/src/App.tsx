import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import ContentStudioPage from './pages/ContentStudio'
import SocialPackPage from './pages/SocialPack'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/content-studio" replace /> : <StandaloneSignIn />} />
      <Route path="/content-studio" element={<RequireAuth><ContentStudioPage /></RequireAuth>} />
      <Route path="/social-pack" element={<RequireAuth><SocialPackPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/content-studio" replace />} />
    </Routes>
  )
}
