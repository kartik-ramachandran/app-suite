import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import DashboardPage from './pages/Dashboard'
import CreatePromptPage from './pages/CreatePrompt'
import ChainsPage from './pages/Chains'
import ChainBuilderPage from './pages/ChainBuilder'
import PersonasPage from './pages/Personas'
import PersonaLibraryPage from './pages/PersonaLibrary'
import SkillsPage from './pages/Skills'
import CollectionsPage from './pages/Collections'
import EvaluationsPage from './pages/Evaluations'
import HistoryPage from './pages/History'
import PromptImportPage from './pages/PromptImport'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/prompts" replace /> : <StandaloneSignIn />} />
      <Route path="/prompts" element={<RequireAuth><DashboardPage /></RequireAuth>} />
      <Route path="/create-prompt" element={<RequireAuth><CreatePromptPage /></RequireAuth>} />
      <Route path="/chains" element={<RequireAuth><ChainsPage /></RequireAuth>} />
      <Route path="/chain-builder" element={<RequireAuth><ChainBuilderPage /></RequireAuth>} />
      <Route path="/personas" element={<RequireAuth><PersonasPage /></RequireAuth>} />
      <Route path="/persona-library" element={<RequireAuth><PersonaLibraryPage /></RequireAuth>} />
      <Route path="/skills" element={<RequireAuth><SkillsPage /></RequireAuth>} />
      <Route path="/collections" element={<RequireAuth><CollectionsPage /></RequireAuth>} />
      <Route path="/evaluations" element={<RequireAuth><EvaluationsPage /></RequireAuth>} />
      <Route path="/history" element={<RequireAuth><HistoryPage /></RequireAuth>} />
      <Route path="/import-prompt" element={<RequireAuth><PromptImportPage /></RequireAuth>} />
      <Route path="/chain-builder/:id" element={<RequireAuth><ChainBuilderPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/prompts" replace />} />
    </Routes>
  )
}
