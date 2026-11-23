import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import './index.css'
import App from './App.tsx'
import { bootstrapLocalMode } from './services/localMode'


// Local mode (no sign-in): get the owner session before the first render so
// the route guards see it. Does nothing when local mode is off.
void bootstrapLocalMode().finally(() => {
  createRoot(document.getElementById('root')!).render(
    <BrowserRouter>
      <App />
    </BrowserRouter>,
  )
})
