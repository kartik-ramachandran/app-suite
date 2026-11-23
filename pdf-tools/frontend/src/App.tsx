import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import PdfToolsPage from './pages/PdfTools'
import FreePdfToolsPage from './pages/FreePdfTools'
import FreePdfToolDetailPage from './pages/FreePdfToolDetail'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/pdf-tools" replace /> : <StandaloneSignIn />} />
      <Route path="/pdf-tools" element={<RequireAuth><PdfToolsPage /></RequireAuth>} />
      <Route path="/free-pdf-tools" element={<RequireAuth><FreePdfToolsPage /></RequireAuth>} />
      <Route path="/free-pdf-tools/:toolKey" element={<RequireAuth><FreePdfToolDetailPage /></RequireAuth>} />
      <Route path="/pdf-tools/:toolName" element={<RequireAuth><PdfToolsPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/pdf-tools" replace />} />
    </Routes>
  )
}
