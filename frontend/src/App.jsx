import { useEffect } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuthStore } from './store/auth'
import { watchSystemTheme } from './store/theme'
import { requestRefresh } from './api/client'
import AppLayout from './components/AppLayout'
import Login from './pages/Login'
import Dashboard from './pages/Dashboard'
import Appointments from './pages/Appointments'
import CalendarPage from './pages/CalendarPage'
import Customers from './pages/Customers'
import Calls from './pages/Calls'
import KnowledgeBase from './pages/KnowledgeBase'
import Catalogue from './pages/Catalogue'
import Billing from './pages/Billing'
import Settings from './pages/Settings'

function RequireAuth({ children }) {
  const user = useAuthStore((s) => s.user)
  return user ? children : <Navigate to="/login" replace />
}

/**
 * The access token lives in memory, so a page reload starts without one. The refresh cookie
 * survives, so exchange it for a fresh access token before rendering — otherwise a returning
 * user would briefly appear signed in with every request failing.
 */
function useSessionBootstrap() {
  const { setSession, setReady, logout } = useAuthStore.getState()

  useEffect(() => {
    let cancelled = false
    requestRefresh()
      .then(({ data }) => { if (!cancelled) setSession(data.data.user, data.data.accessToken) })
      .catch(() => { if (!cancelled) logout() })
      .finally(() => { if (!cancelled) setReady(true) })
    return () => { cancelled = true }
  }, [setSession, setReady, logout])
}

export default function App() {
  useSessionBootstrap()
  const ready = useAuthStore((s) => s.ready)

  // index.html paints the stored theme before first render; this only keeps the "System"
  // choice in step if the device flips to dark while the app is open.
  useEffect(watchSystemTheme, [])

  if (!ready) {
    return (
      <div className="grid min-h-screen place-items-center">
        <p className="text-sm text-muted">Loading…</p>
      </div>
    )
  }

  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route
        path="/"
        element={
          <RequireAuth>
            <AppLayout />
          </RequireAuth>
        }
      >
        <Route index element={<Dashboard />} />
        <Route path="calendar" element={<CalendarPage />} />
        <Route path="appointments" element={<Appointments />} />
        <Route path="customers" element={<Customers />} />
        <Route path="calls" element={<Calls />} />
        <Route path="catalogue" element={<Catalogue />} />
        <Route path="knowledge-base" element={<KnowledgeBase />} />
        <Route path="billing" element={<Billing />} />
        <Route path="settings" element={<Settings />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
