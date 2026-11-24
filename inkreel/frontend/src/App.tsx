import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import Layout from './components/Layout'
import SignIn from './pages/SignIn'
import SourceChat from './pages/SourceChat'
import Visualizer from './pages/Visualizer'
import VideoStudio from './pages/VideoStudio'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <Layout>{children}</Layout>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/ink" replace /> : <SignIn />} />
      <Route path="/ink" element={<RequireAuth><Visualizer /></RequireAuth>} />
      <Route path="/chat" element={<RequireAuth><SourceChat /></RequireAuth>} />
      <Route path="/video" element={<RequireAuth><VideoStudio /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/ink" replace />} />
    </Routes>
  )
}
