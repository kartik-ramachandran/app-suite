import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { GoogleOAuthProvider } from '@react-oauth/google'
import './i18n/index'
// Parent app's global styles first (the copied pages rely on them), then the
// standalone shell + palette last so its tokens win.
import './styles/base.css'
import './styles/App.css'
import './styles/features.css'
import './styles/EditorialTheme.css'
import './index.css'
import App from './App.tsx'
import { bootstrapLocalMode } from './services/localMode'

// The parent's default visual theme.
document.documentElement.classList.add('theme-editorial')
document.documentElement.dataset.visualTheme = 'editorial'


// Local mode (no sign-in): get the owner session before the first render so
// the route guards see it. Does nothing when local mode is off.
void bootstrapLocalMode().finally(() => {
  createRoot(document.getElementById('root')!).render(
    <GoogleOAuthProvider clientId={import.meta.env.VITE_GOOGLE_CLIENT_ID || ''}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </GoogleOAuthProvider>,
  )
})
