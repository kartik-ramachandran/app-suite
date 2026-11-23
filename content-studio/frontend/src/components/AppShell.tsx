import { createContext, useContext, useState } from 'react'
import type { ReactNode } from 'react'
import { Link, NavLink, useNavigate } from 'react-router-dom'
import PhosphorIcon from './PhosphorIcon'
import { isLocalMode } from '../services/localMode'

export interface Breadcrumb {
  label: string
  path?: string
}

interface LayoutContextValue {
  setBreadcrumbs: (b: Breadcrumb[]) => void
  setHeaderActions: (a: ReactNode) => void
}

export const LayoutContext = createContext<LayoutContextValue>({
  setBreadcrumbs: () => {},
  setHeaderActions: () => {},
})

// Same hook and context shape as the parent app's AuthenticatedLayout, so the
// copied pages work unchanged.
export function useLayoutControls() {
  return useContext(LayoutContext)
}

export default function AppShell({ children }: { children: ReactNode }) {
  const [breadcrumbs, setBreadcrumbs] = useState<Breadcrumb[]>([])
  const [headerActions, setHeaderActions] = useState<ReactNode>(null)
  const navigate = useNavigate()
  const user = (() => {
    try { return JSON.parse(localStorage.getItem('user') || 'null') } catch { return null }
  })()

  const signOut = () => {
    localStorage.removeItem('authToken')
    localStorage.removeItem('user')
    navigate('/signin')
  }

  return (
    <LayoutContext.Provider value={{ setBreadcrumbs, setHeaderActions }}>
      <div className="tm-shell">
        <header className="tm-header">
          <Link to="/content-studio" className="tm-brand">
            <PhosphorIcon className="bi bi-megaphone" />
            <span>Content Studio</span>
          </Link>
          <nav className="tm-nav">
            <NavLink to="/content-studio" className={({ isActive }) => `tm-nav-link${isActive ? ' tm-nav-active' : ''}`}>
              <PhosphorIcon className="bi bi-megaphone" /> Content Studio
            </NavLink>
            <NavLink to="/social-pack" className={({ isActive }) => `tm-nav-link${isActive ? ' tm-nav-active' : ''}`}>
              <PhosphorIcon className="bi bi-share" /> Social Pack
            </NavLink>
          </nav>
          <div className="tm-user">
            {headerActions}
            {user?.name && <span className="tm-user-name">{user.name}</span>}
            {!isLocalMode() && (
              <button className="tm-signout" onClick={signOut} title="Sign out">
              <PhosphorIcon className="bi bi-box-arrow-right" /> Sign out
            </button>
            )}
          </div>
        </header>
        {breadcrumbs.length > 0 && (
          <div className="tm-crumbs">
            {breadcrumbs.map((c, i) => (
              <span key={i} className="tm-crumb">
                {c.path ? <Link to={c.path}>{c.label}</Link> : <span>{c.label}</span>}
                {i < breadcrumbs.length - 1 && <PhosphorIcon className="bi bi-chevron-right" />}
              </span>
            ))}
          </div>
        )}
        <main className="tm-main">{children}</main>
      </div>
    </LayoutContext.Provider>
  )
}
