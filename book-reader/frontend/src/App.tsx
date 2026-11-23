import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import BookReaderPage from './pages/BookReader'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/book-reader" replace /> : <StandaloneSignIn />} />
      <Route path="/book-reader" element={<RequireAuth><BookReaderPage /></RequireAuth>} />
      <Route path="/book-reader/:id" element={<RequireAuth><BookReaderPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/book-reader" replace />} />
    </Routes>
  )
}
