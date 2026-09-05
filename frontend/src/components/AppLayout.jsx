import { useEffect, useState } from 'react'
import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { AnimatePresence, motion } from 'framer-motion'
import { useQuery } from '@tanstack/react-query'
import { api, signOut, unwrap } from '../api/client'
import { useAuthStore } from '../store/auth'
import { useThemeStore, resolveTheme } from '../store/theme'
import { Avatar } from './ui'
import { Toasts, useAlertStream } from './alerts'
import PushPrompt from './PushPrompt'
import { pushSupported, registerServiceWorker, refreshSubscription } from '../lib/push'

const Icon = ({ d, className = 'h-[18px] w-[18px]' }) => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"
    strokeLinecap="round" strokeLinejoin="round" className={className}>
    {d.map((p, i) => <path key={i} d={p} />)}
  </svg>
)

export const icons = {
  dashboard: ['M4 4h7v7H4z', 'M13 4h7v4h-7z', 'M13 12h7v8h-7z', 'M4 15h7v5H4z'],
  calendar: ['M8 2v4M16 2v4M3 9h18', 'M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z'],
  appointments: ['M9 6h11M9 12h11M9 18h11', 'M4 6h.01M4 12h.01M4 18h.01'],
  stats: ['M4 20V10M10 20V4M16 20v-7M22 20H2'],
  chats: ['M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z'],
  settings: ['M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z',
    'M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h.01a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51h.01a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v.01a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'],
  bell: ['M18 8a6 6 0 1 0-12 0c0 7-3 9-3 9h18s-3-2-3-9', 'M13.7 21a2 2 0 0 1-3.4 0'],
  phone: ['M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3 19.5 19.5 0 0 1-6-6 19.8 19.8 0 0 1-3-8.7A2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.13.96.36 1.9.7 2.8a2 2 0 0 1-.45 2.1L8.1 9.9a16 16 0 0 0 6 6l1.3-1.3a2 2 0 0 1 2.1-.45c.9.34 1.84.57 2.8.7A2 2 0 0 1 22 16.9z'],
  book: ['M4 19.5A2.5 2.5 0 0 1 6.5 17H20', 'M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z'],
  users: ['M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2', 'M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z', 'M23 21v-2a4 4 0 0 0-3-3.87', 'M16 3.13a4 4 0 0 1 0 7.75'],
  tag: ['M20.6 13.4 12 22l-9-9V3h10l7.6 7.6a2 2 0 0 1 0 2.8z', 'M7.5 7.5h.01'],
  card: ['M2 7a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2z', 'M2 10h20', 'M6 15h4'],
}

function Item({ to, icon, label, badge, end, onNavigate }) {
  return (
    <NavLink
      to={to}
      end={end}
      onClick={onNavigate}
      className={({ isActive }) =>
        // The active row now carries the brand hue as well as the raised card, so the
        // current page is legible at a glance instead of resting on a faint shadow alone.
        `flex items-center gap-3 rounded-2xl px-4 py-2.5 text-base font-medium transition-colors ${
          isActive
            ? 'bg-card text-brand-strong font-semibold shadow-sm'
            : 'text-ink-soft hover:bg-card/60'
        }`
      }
    >
      <Icon d={icon} />
      <span className="flex-1">{label}</span>
      {badge ? (
        <span className="grid h-5 w-5 place-items-center rounded-full bg-ink text-2xs font-semibold text-on-ink">
          {badge}
        </span>
      ) : null}
    </NavLink>
  )
}

function SnapshotStat({ label, value, highlight, onClick }) {
  return (
    <button onClick={onClick}
      className="relative flex w-full items-center justify-between gap-2 rounded-xl px-2 py-1.5 text-left transition hover:bg-card/40">
      <span className="text-xs text-ink-soft">{label}</span>
      <span className={`text-sm font-bold ${highlight ? 'text-danger' : 'text-ink'}`}>{value}</span>
    </button>
  )
}

/** Quick light/night switch. The three-way choice (including "System") lives in Settings →
 *  Appearance; this only flips between the two visible states. */
function ThemeToggle() {
  const mode = useThemeStore((s) => s.mode)
  const setMode = useThemeStore((s) => s.setMode)
  const isDark = resolveTheme(mode) === 'dark'

  return (
    <button
      onClick={() => setMode(isDark ? 'light' : 'dark')}
      title={isDark ? 'Switch to light mode' : 'Switch to night mode'}
      aria-label={isDark ? 'Switch to light mode' : 'Switch to night mode'}
      className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-card text-ink-soft shadow-sm transition hover:text-ink"
    >
      {isDark ? (
        <Icon d={['M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10z', 'M12 1v2M12 21v2M4.2 4.2l1.4 1.4M18.4 18.4l1.4 1.4M1 12h2M21 12h2M4.2 19.8l1.4-1.4M18.4 5.6l1.4-1.4']} />
      ) : (
        <Icon d={['M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z']} />
      )}
    </button>
  )
}

/**
 * Keeps this browser reachable.
 *
 * Registering the worker is what allows a push to arrive with every tab closed, and re-sending an
 * existing subscription each load guards against the browser having rotated its keys underneath
 * us. Both are silent: a device that has never been given permission subscribes to nothing, and
 * nobody is prompted from here — that only ever happens from Settings, where they asked for it.
 */
function usePushRegistration() {
  const { data: config } = useQuery({
    queryKey: ['push-config'],
    queryFn: () => api.get('/notifications/config').then(unwrap),
    staleTime: Infinity,
  })

  useEffect(() => {
    if (!config?.enabled || !pushSupported()) return
    registerServiceWorker().then(() => refreshSubscription()).catch(() => {
      /* Best-effort: the dashboard's own queue covers a device that cannot be reached. */
    })
  }, [config])
}

function SidebarContent({ onNavigate }) {
  const { user } = useAuthStore()
  const navigate = useNavigate()
  const go = (to) => { onNavigate?.(); navigate(to) }

  // Live badge counts — refreshed periodically so they track real activity.
  const { data: counts } = useQuery({
    queryKey: ['sidebar-counts'],
    queryFn: () => api.get('/dashboard/counts').then(unwrap),
    refetchInterval: 60_000,
  })

  return (
    <>
      <div className="mb-8 flex items-center gap-2.5 px-1">
        <img src="/logo.png" alt="Frontly" className="h-8 w-auto shrink-0 object-contain" />
        <span className="flex-1" />
        <ThemeToggle />
      </div>

      <p className="mb-2 px-4 text-xs font-medium uppercase tracking-wide text-muted">General</p>
      <nav className="space-y-1">
        <Item to="/" end icon={icons.dashboard} label="Dashboard" onNavigate={onNavigate} />
        <Item to="/calendar" icon={icons.calendar} label="Calendar" onNavigate={onNavigate} />
        <Item to="/appointments" icon={icons.appointments} label="Appointments"
          badge={counts?.appointments} onNavigate={onNavigate} />
        <Item to="/customers" icon={icons.users} label="Customers" onNavigate={onNavigate} />
      </nav>

      <p className="mb-2 mt-6 px-4 text-xs font-medium uppercase tracking-wide text-muted">Tools</p>
      <nav className="space-y-1">
        <Item to="/calls" icon={icons.phone} label="Calls" badge={counts?.calls} onNavigate={onNavigate} />
        <Item to="/catalogue" icon={icons.tag} label="Services & Products" onNavigate={onNavigate} />
        <Item to="/knowledge-base" icon={icons.book} label="Knowledge Base" onNavigate={onNavigate} />
        <Item to="/billing" icon={icons.card} label="Billing" onNavigate={onNavigate} />
        <Item to="/settings" icon={icons.settings} label="Settings" onNavigate={onNavigate} />
      </nav>

      <div className="mt-auto pt-6">
        <div className="relative mb-4 overflow-hidden rounded-card bg-brand-soft p-4">
          <div className="absolute -right-6 -top-6 h-20 w-20 rounded-full bg-card/50" />
          <p className="relative mb-2 text-xs font-semibold uppercase tracking-wide text-muted">
            Customer snapshot
          </p>
          <div className="relative -mx-2 space-y-0.5">
            <SnapshotStat label="Active customers" value={counts?.customers ?? 0}
              onClick={() => go('/customers')} />
            <SnapshotStat label="Upcoming appointments" value={counts?.appointments ?? 0}
              onClick={() => go('/appointments')} />
            <SnapshotStat label="Missed calls to follow up" value={counts?.calls ?? 0}
              highlight={(counts?.calls ?? 0) > 0} onClick={() => go('/calls')} />
          </div>
        </div>

        <button
          onClick={async () => { await signOut(); navigate('/login') }}
          className="flex w-full items-center gap-3 rounded-2xl bg-card px-3 py-2.5 text-left shadow-sm transition hover:shadow"
          title="Sign out"
        >
          <Avatar name={user?.fullName ?? 'User'} size="sm" />
          <span className="min-w-0 flex-1">
            <span className="block truncate text-sm font-semibold">{user?.fullName}</span>
            <span className="block truncate text-xs text-muted">{user?.email}</span>
          </span>
          <svg viewBox="0 0 24 24" className="h-4 w-4 text-muted" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M6 9l6 6 6-6" />
          </svg>
        </button>
      </div>
    </>
  )
}

export default function AppLayout() {
  const [drawerOpen, setDrawerOpen] = useState(false)

  usePushRegistration()
  // A push both wakes the phone and refreshes whatever is on screen here, so an open dashboard
  // never shows a diary that is a minute out of date.
  const { toasts, dismiss } = useAlertStream()

  return (
    <div className="flex h-dvh w-full overflow-hidden bg-panel">
      {/*
        Desktop sidebar — fixed, scrolls independently if needed. It appears at lg, not md:
        on a tablet a 232px rail eats a third of the screen, so those widths keep the drawer
        and give the whole viewport to the content.
      */}
      <aside className="hidden w-[208px] shrink-0 flex-col overflow-y-auto border-r border-line/60 p-5 lg:flex xl:w-[232px] height-[calc(100dvh)]">
        <SidebarContent />
      </aside>

      {/* Mobile drawer */}
      <AnimatePresence>
        {drawerOpen && (
          <>
            <motion.div
              initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }}
              className="fixed inset-0 z-40 bg-black/30 md:hidden"
              onClick={() => setDrawerOpen(false)}
            />
            <motion.aside
              initial={{ x: -280 }} animate={{ x: 0 }} exit={{ x: -280 }}
              transition={{ type: 'tween', duration: 0.22 }}
              className="fixed inset-y-0 left-0 z-50 flex w-[264px] flex-col overflow-y-auto bg-panel p-5 shadow-2xl md:hidden"
            >
              <SidebarContent onNavigate={() => setDrawerOpen(false)} />
            </motion.aside>
          </>
        )}
      </AnimatePresence>

      {/* Main column: mobile top bar + scrollable content */}
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center gap-3 border-b border-line/60 px-4 py-3 lg:hidden">
          <button
            onClick={() => setDrawerOpen(true)}
            className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-card shadow-sm"
            aria-label="Open menu"
          >
            <svg viewBox="0 0 24 24" className="h-5 w-5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
              <path d="M4 7h16M4 12h16M4 17h16" />
            </svg>
          </button>
          <img src="/logo.png" alt="Frontly" className="h-7 w-auto object-contain" />
        </header>

        {/*
          `@container` makes this the measuring stick for the pages inside it. A page's
          columns then follow the space it actually has — which is the viewport minus the
          sidebar, and changes when the sidebar appears — instead of the viewport alone.
        */}
        <main className="@container min-w-0 flex-1 overflow-y-auto p-4 sm:p-5 lg:p-6 xl:p-8">
          {/* Above the page, not inside one: a device that cannot be reached is worth saying so
              wherever the person happens to be, and it takes itself away once it is dealt with. */}
          <PushPrompt />
          <Outlet />
        </main>
      </div>

      <Toasts toasts={toasts} dismiss={dismiss} />
    </div>
  )
}
