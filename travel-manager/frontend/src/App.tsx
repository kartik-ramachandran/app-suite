import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import Layout from './components/Layout'
import SignIn from './pages/SignIn'
import Travel from './pages/Travel'
import Wardrobe from './pages/Wardrobe'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <Layout>{children}</Layout>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/travel" replace /> : <SignIn />} />
      <Route path="/travel" element={<RequireAuth><Travel /></RequireAuth>} />
      <Route path="/wardrobe" element={<RequireAuth><Wardrobe /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/travel" replace />} />
    </Routes>
  )
}
