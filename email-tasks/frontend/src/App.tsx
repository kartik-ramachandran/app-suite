import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import EmailTasksPage from './pages/EmailTasks'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/email-tasks" replace /> : <StandaloneSignIn />} />
      <Route path="/email-tasks" element={<RequireAuth><EmailTasksPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/email-tasks" replace />} />
    </Routes>
  )
}
