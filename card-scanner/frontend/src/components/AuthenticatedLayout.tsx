import { useLayoutEffect } from 'react'
import type { ReactNode } from 'react'
import { useLayoutControls } from './AppShell'
import type { Breadcrumb } from './AppShell'

// The parent app's AuthenticatedLayout, reduced to a compatible shim: the
// standalone AppShell already renders the header and nav, so pages that wrap
// themselves in <AuthenticatedLayout> just forward their crumbs and actions.
export { useLayoutControls, LayoutContext } from './AppShell'
export type { Breadcrumb } from './AppShell'

interface AuthenticatedLayoutProps {
  children?: ReactNode
  pageTitle?: string
  breadcrumbs?: Breadcrumb[]
  headerActions?: ReactNode
}

export default function AuthenticatedLayout({ children, breadcrumbs, headerActions }: AuthenticatedLayoutProps) {
  const { setBreadcrumbs, setHeaderActions } = useLayoutControls()
  useLayoutEffect(() => {
    if (breadcrumbs) setBreadcrumbs(breadcrumbs)
    if (headerActions !== undefined) setHeaderActions(headerActions)
    return () => {
      if (breadcrumbs) setBreadcrumbs([])
      if (headerActions !== undefined) setHeaderActions(null)
    }
  }, [breadcrumbs, headerActions, setBreadcrumbs, setHeaderActions])
  return <>{children}</>
}
