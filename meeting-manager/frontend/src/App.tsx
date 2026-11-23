import { Navigate, Route, Routes } from 'react-router-dom'
import type { ReactNode } from 'react'
import AppShell from './components/AppShell'
import StandaloneSignIn from './pages/StandaloneSignIn'
import PromptCalendarPage from './pages/PromptCalendar'
import LiveMeetingsPage from './pages/LiveMeetings'
import FollowUpHubPage from './pages/FollowUpHub'
import AvailabilityCheckerPage from './pages/AvailabilityChecker'
import MeetingWorkspacePagePage from './pages/MeetingWorkspacePage'
import CalendarOAuthCallbackPage from './pages/CalendarOAuthCallback'
import { isLocalMode } from './services/localMode'

function RequireAuth({ children }: { children: ReactNode }) {
  const token = localStorage.getItem('authToken')
  if (!token) return <Navigate to="/signin" replace />
  return <AppShell>{children}</AppShell>
}

export default function App() {
  return (
    <Routes>
      <Route path="/signin" element={isLocalMode() ? <Navigate to="/calendar" replace /> : <StandaloneSignIn />} />
      <Route path="/calendar" element={<RequireAuth><PromptCalendarPage /></RequireAuth>} />
      <Route path="/live-meetings" element={<RequireAuth><LiveMeetingsPage /></RequireAuth>} />
      <Route path="/calendar/follow-ups" element={<RequireAuth><FollowUpHubPage /></RequireAuth>} />
      <Route path="/calendar/availability" element={<RequireAuth><AvailabilityCheckerPage /></RequireAuth>} />
      <Route path="/calendar/meeting/:id" element={<RequireAuth><MeetingWorkspacePagePage /></RequireAuth>} />
      <Route path="/calendar/callback/:provider" element={<RequireAuth><CalendarOAuthCallbackPage /></RequireAuth>} />
      <Route path="*" element={<Navigate to="/calendar" replace />} />
    </Routes>
  )
}
