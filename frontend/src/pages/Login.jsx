import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { motion } from 'framer-motion'
import { api } from '../api/client'
import { useAuthStore } from '../store/auth'

/* Thin geometric line work behind the hero — kept very low opacity so it reads as texture. */
function HeroPattern() {
  return (
    <svg className="absolute inset-0 h-full w-full" aria-hidden="true">
      <defs>
        <pattern id="hero-grid" width="56" height="56" patternUnits="userSpaceOnUse">
          <path d="M56 0H0V56" fill="none" stroke="#ffffff" strokeWidth="0.6" />
        </pattern>
        <pattern id="hero-diag" width="120" height="120" patternUnits="userSpaceOnUse">
          <path d="M0 120L120 0M-30 30L30 -30M90 150L150 90" fill="none" stroke="#ffffff" strokeWidth="0.6" />
        </pattern>
      </defs>
      <rect width="100%" height="100%" fill="url(#hero-grid)" opacity="0.05" />
      <rect width="100%" height="100%" fill="url(#hero-diag)" opacity="0.04" />
      <g fill="none" stroke="#ffffff" opacity="0.06">
        <circle cx="50%" cy="52%" r="200" />
        <circle cx="50%" cy="52%" r="290" />
        <circle cx="50%" cy="52%" r="380" />
      </g>
    </svg>
  )
}

/* Stylised 3D-ish character: young professional, gray side part, white tee, blue jeans,
   silver laptop — plus the headset, because this product is a voice agent. */
function HeroCharacter() {
  return (
    <svg viewBox="0 0 400 440" className="relative h-full w-full" aria-hidden="true">
      <defs>
        <linearGradient id="skin" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="#f9d3b4" />
          <stop offset="100%" stopColor="#e0a77f" />
        </linearGradient>
        <linearGradient id="hair" x1="0.2" y1="0" x2="0.9" y2="1">
          <stop offset="0%" stopColor="#c9ccd4" />
          <stop offset="100%" stopColor="#7f838e" />
        </linearGradient>
        <linearGradient id="tee" x1="0.1" y1="0" x2="0.9" y2="1">
          <stop offset="0%" stopColor="#ffffff" />
          <stop offset="100%" stopColor="#d3d9e4" />
        </linearGradient>
        <linearGradient id="jeans" x1="0.1" y1="0" x2="0.9" y2="1">
          <stop offset="0%" stopColor="#5b83d4" />
          <stop offset="100%" stopColor="#2b4685" />
        </linearGradient>
        <linearGradient id="silver" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stopColor="#f2f4f8" />
          <stop offset="55%" stopColor="#ccd2dd" />
          <stop offset="100%" stopColor="#a7aeba" />
        </linearGradient>
        <linearGradient id="screen" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stopColor="#2a3350" />
          <stop offset="100%" stopColor="#141826" />
        </linearGradient>
        <radialGradient id="floorGlow" cx="0.5" cy="0.5" r="0.5">
          <stop offset="0%" stopColor="#7c8cff" stopOpacity="0.45" />
          <stop offset="100%" stopColor="#7c8cff" stopOpacity="0" />
        </radialGradient>
      </defs>

      {/* soft light pooled under the figure */}
      <ellipse cx="200" cy="404" rx="150" ry="34" fill="url(#floorGlow)" />
      <ellipse cx="200" cy="406" rx="86" ry="15" fill="#000000" opacity="0.35" />

      {/* legs */}
      <path d="M176 268h20l-4 122c0 6-4 9-10 9s-10-3-10-9z" fill="url(#jeans)" />
      <path d="M204 268h20l4 122c0 6-4 9-10 9s-10-3-10-9z" fill="url(#jeans)" />
      <path d="M176 268h48l-2 22h-44z" fill="#000000" opacity="0.14" />
      {/* sneakers */}
      <path d="M170 386h22v10c0 5-5 8-14 8h-14c-4 0-6-2-6-5 0-4 3-6 7-8z" fill="#f4f6fa" />
      <path d="M208 386h22l5 5c4 2 7 4 7 8 0 3-2 5-6 5h-14c-9 0-14-3-14-8z" fill="#f4f6fa" />

      {/* arms — bare forearms below short sleeves */}
      <path d="M168 192L145 236L160 274" fill="none" stroke="url(#skin)" strokeWidth="16" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M232 192L255 236L240 274" fill="none" stroke="url(#skin)" strokeWidth="16" strokeLinecap="round" strokeLinejoin="round" />

      {/* torso / tee */}
      <path
        d="M200 166c18 0 32 9 35 22l8 68c1 7-4 12-12 12h-62c-8 0-13-5-12-12l8-68c3-13 17-22 35-22z"
        fill="url(#tee)"
      />
      <path d="M200 166c-8 0-13 7-13 15h26c0-8-5-15-13-15z" fill="#000000" opacity="0.08" />
      {/* short sleeves */}
      <path d="M170 184L160 212" fill="none" stroke="url(#tee)" strokeWidth="26" strokeLinecap="round" />
      <path d="M230 184L240 212" fill="none" stroke="url(#tee)" strokeWidth="26" strokeLinecap="round" />
      <path d="M172 212L149 219M228 212L251 219" fill="none" stroke="#000000" strokeWidth="2" opacity="0.07" strokeLinecap="round" />

      {/* laptop */}
      <g>
        <rect x="150" y="214" width="100" height="60" rx="6" fill="url(#silver)" />
        <rect x="156" y="220" width="88" height="48" rx="3" fill="url(#screen)" />
        <path d="M161 256h22v7h-22zM161 242h50v6h-50zM161 229h34v6h-34z" fill="#8fa6ff" opacity="0.75" />
        <path d="M142 274h116l13 13c3 3 1 6-3 6H132c-4 0-6-3-3-6z" fill="url(#silver)" />
        <path d="M142 274h116l4 4H138z" fill="#000000" opacity="0.14" />
      </g>

      {/* hands gripping the laptop */}
      <circle cx="163" cy="277" r="11" fill="url(#skin)" />
      <circle cx="237" cy="277" r="11" fill="url(#skin)" />

      {/* neck + head */}
      <path d="M188 140h24v26h-24z" fill="url(#skin)" />
      <path d="M188 148c7 6 17 6 24 0v-8h-24z" fill="#000000" opacity="0.12" />
      <ellipse cx="200" cy="106" rx="42" ry="45" fill="url(#skin)" />
      {/* ears */}
      <ellipse cx="159" cy="110" rx="7" ry="10" fill="url(#skin)" />
      <ellipse cx="241" cy="110" rx="7" ry="10" fill="url(#skin)" />
      {/* gray side-part hair */}
      <path
        d="M200 60c26 0 43 17 43 39 0 5-1 10-2 14-2-14-8-22-18-26-14 12-38 15-56 9 3 12 0 20-6 25-2-6-3-14-3-22 0-23 16-39 42-39z"
        fill="url(#hair)"
      />
      {/* face */}
      <ellipse cx="185" cy="108" rx="4" ry="5.5" fill="#20242e" />
      <ellipse cx="215" cy="108" rx="4" ry="5.5" fill="#20242e" />
      <path d="M176 96c5-4 11-4 15-1M209 95c4-3 10-3 15 1" fill="none" stroke="#6d7280" strokeWidth="3" strokeLinecap="round" />
      <path d="M191 126c5 5 13 5 18 0" fill="none" stroke="#b4744f" strokeWidth="3.4" strokeLinecap="round" />
      <ellipse cx="171" cy="122" rx="8" ry="5" fill="#f08f7c" opacity="0.35" />
      <ellipse cx="229" cy="122" rx="8" ry="5" fill="#f08f7c" opacity="0.35" />

      {/* headset */}
      <path d="M158 108a42 46 0 0 1 84 0" fill="none" stroke="#2f3543" strokeWidth="8" strokeLinecap="round" />
      <rect x="148" y="98" width="16" height="26" rx="7" fill="#2f3543" />
      <rect x="236" y="98" width="16" height="26" rx="7" fill="#2f3543" />
      <path d="M156 124c0 14 8 22 20 24" fill="none" stroke="#2f3543" strokeWidth="4.5" strokeLinecap="round" />
      <circle cx="177" cy="149" r="5" fill="#7c8cff" />
    </svg>
  )
}

/* Abstract 3D props that orbit the character. */
function FloatingProps() {
  const float = (dy, duration, delay = 0) => ({
    animate: { y: [0, dy, 0] },
    transition: { duration, delay, repeat: Infinity, ease: 'easeInOut' },
  })

  return (
    <>
      {/* hexagon frame around the character */}
      <svg
        className="pointer-events-none absolute left-1/2 top-[42%] h-[64%] max-h-[470px] w-auto -translate-x-1/2 -translate-y-1/2"
        viewBox="0 0 200 220"
        fill="none"
        aria-hidden="true"
      >
        <path
          d="M100 4l83 48v116l-83 48-83-48V52z"
          stroke="url(#hexStroke)"
          strokeWidth="1.2"
          strokeLinejoin="round"
        />
        <defs>
          <linearGradient id="hexStroke" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stopColor="#8f7cff" stopOpacity="0.75" />
            <stop offset="50%" stopColor="#ffffff" stopOpacity="0.18" />
            <stop offset="100%" stopColor="#3ddc97" stopOpacity="0.7" />
          </linearGradient>
        </defs>
      </svg>

      {/* purple crystal cube */}
      <motion.svg
        {...float(-16, 6)}
        className="absolute left-[10%] top-[16%] w-16 drop-shadow-[0_16px_30px_rgba(124,92,255,0.5)]"
        viewBox="0 0 100 110"
        aria-hidden="true"
      >
        <path d="M50 2l46 26-46 26L4 28z" fill="#b39bff" />
        <path d="M4 28l46 26v52L4 80z" fill="#7a54e8" />
        <path d="M96 28L50 54v52l46-26z" fill="#5b34c9" />
      </motion.svg>

      {/* green glowing sphere */}
      <motion.div
        {...float(18, 7, 0.6)}
        className="absolute right-[12%] top-[22%] h-14 w-14 rounded-full"
        style={{
          background: 'radial-gradient(circle at 32% 28%, #b9ffdf 0%, #3ddc97 45%, #12a06a 100%)',
          boxShadow: '0 0 44px 8px rgba(61,220,151,0.45)',
        }}
      />

      {/* small neon square */}
      <motion.div
        {...float(-12, 5.5, 0.3)}
        className="absolute right-[18%] bottom-[20%] h-10 w-10 rotate-12 rounded-[8px] border border-[#8f7cff]/80"
        style={{ boxShadow: '0 0 22px rgba(143,124,255,0.45) inset, 0 0 22px rgba(143,124,255,0.35)' }}
      />

      {/* neon outlined triangles */}
      <motion.svg {...float(14, 6.5, 0.9)} className="absolute left-[16%] bottom-[24%] w-12" viewBox="0 0 60 54" aria-hidden="true">
        <path d="M30 4l26 46H4z" fill="none" stroke="#3ddc97" strokeWidth="2" strokeLinejoin="round" opacity="0.85" />
      </motion.svg>
      <motion.svg {...float(-10, 5, 1.2)} className="absolute right-[26%] top-[9%] w-8" viewBox="0 0 60 54" aria-hidden="true">
        <path d="M30 4l26 46H4z" fill="none" stroke="#8f7cff" strokeWidth="2.6" strokeLinejoin="round" opacity="0.8" />
      </motion.svg>
      <motion.svg {...float(12, 7.5, 0.4)} className="absolute left-[26%] top-[62%] w-7 rotate-180" viewBox="0 0 60 54" aria-hidden="true">
        <path d="M30 4l26 46H4z" fill="none" stroke="#ffffff" strokeWidth="2.6" strokeLinejoin="round" opacity="0.35" />
      </motion.svg>
    </>
  )
}

function Field({ label, hint, ...rest }) {
  return (
    <label className="block">
      <span className="mb-1.5 flex items-center justify-between text-sm font-semibold text-ink">
        {label}
        {hint}
      </span>
      <input
        /* No `outline-none` here: that removed the only keyboard-focus cue the field had.
           The hover/focus border shift below is the pointer affordance, and the global
           :focus-visible ring in index.css handles keyboard focus on top of it. */
        className="w-full rounded-2xl border border-line bg-card/60 px-4 py-3.5 text-base text-ink shadow-[0_1px_2px_rgba(16,24,40,0.04)] backdrop-blur transition placeholder:text-muted focus:border-brand/50 focus:bg-card"
        {...rest}
      />
    </label>
  )
}

export default function Login() {
  const [email, setEmail] = useState('admin@demo.com')
  const [password, setPassword] = useState('Admin123!')
  const [showPassword, setShowPassword] = useState(false)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const setSession = useAuthStore((s) => s.setSession)
  const navigate = useNavigate()

  const submit = async (e) => {
    e.preventDefault()
    setLoading(true)
    setError('')
    try {
      // Through the shared client: it carries withCredentials for the httpOnly refresh cookie
      // and seals the body, which matters most here — this is the request with the password in it.
      const { data } = await api.post('/auth/login', { email, password })
      const { accessToken, user } = data.data
      setSession(user, accessToken)
      navigate('/')
    } catch (err) {
      setError(err.response?.data?.message ?? 'Seems like something went wrong. Please try again.')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="relative grid min-h-screen place-items-center overflow-hidden p-4 lg:p-0">
      {/* ambient colour behind the glass */}
      <div className="pointer-events-none absolute -left-32 top-[-10%] h-[420px] w-[420px] rounded-full bg-lavender opacity-70 blur-[120px]" />
      <div className="pointer-events-none absolute -right-24 bottom-[-15%] h-[460px] w-[460px] rounded-full bg-mint opacity-70 blur-[130px]" />
      <div className="pointer-events-none absolute left-1/3 top-1/2 h-[320px] w-[320px] rounded-full bg-cream opacity-60 blur-[120px]" />

      <motion.div
        initial={{ opacity: 0, y: 18 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.5, ease: 'easeOut' }}
        className="relative flex w-full max-w-[1440px] flex-col overflow-hidden rounded-[24px] border border-line/60 bg-card/55 p-3 shadow-[0_30px_80px_-20px_rgba(16,24,40,0.25)] backdrop-blur-2xl lg:h-[85vh] lg:min-h-[600px] lg:w-[90vw] lg:flex-row lg:p-4"
      >
        {/* ── Left: login form ─────────────────────────────────────── */}
        <div className="flex w-full flex-col overflow-y-auto px-4 pb-4 pt-[30px] sm:px-8 lg:w-[45%] lg:px-12">
          <div className="flex items-center">
            <img src="/logo.png" alt="Frontly" className="h-10 w-auto object-contain" />
          </div>

          <div className="my-auto w-full max-w-[400px] py-10">
            <h1 className="font-display text-4xl font-bold leading-[1.15] text-ink">Login to Frontly!</h1>
            <p className="mt-2 text-base text-muted">Please enter log in details below</p>

            <form onSubmit={submit} className="mt-8 space-y-4">
              <Field
                label="Email"
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="you@company.com"
                autoComplete="email"
                required
              />

              <div className="relative">
                <Field
                  label="Password"
                  type={showPassword ? 'text' : 'password'}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  placeholder="••••••••"
                  autoComplete="current-password"
                  required
                />
                <button
                  type="button"
                  onClick={() => setShowPassword((v) => !v)}
                  aria-label={showPassword ? 'Hide password' : 'Show password'}
                  className="absolute right-4 top-[38px] text-xs font-semibold text-muted transition hover:text-ink"
                >
                  {showPassword ? 'Hide' : 'Show'}
                </button>
              </div>

              <div className="flex items-center justify-between pt-1">
                <label className="flex items-center gap-2 text-sm text-ink-soft">
                  <input type="checkbox" className="h-4 w-4 rounded border-line accent-[var(--color-brand-strong)]" defaultChecked />
                  Keep me signed in
                </label>
               
              </div>

              {error && (
                <p className="rounded-2xl bg-red-100/80 px-4 py-2.5 text-sm font-medium text-red-700">{error}</p>
              )}

              <button
                type="submit"
                disabled={loading}
                className="w-full rounded-2xl bg-brand-strong py-3.5 text-base font-semibold text-on-brand shadow-[0_10px_24px_-12px_var(--color-brand-strong)] transition hover:opacity-90 disabled:opacity-60"
              >
                {loading ? 'Signing in…' : 'Sign in'}
              </button>

            </form>
          </div>
        </div>

        {/* ── Right: promotional hero ──────────────────────────────── */}
        {/* Deliberately dark in both themes — it is a promo panel, not a surface. The
            charcoal carries a little of the brand teal so it sits with the logo above it
            rather than reading as a neutral black rectangle. */}
        <div className="relative hidden w-[55%] overflow-hidden rounded-[28px] bg-[#0e2229] lg:block">
          <HeroPattern />
          <FloatingProps />

          <div className="relative flex h-full flex-col">
            <div className="min-h-0 flex-1 px-10 pt-8">
              <HeroCharacter />
            </div>
            <div className="px-12 pb-10 text-center">
              <h2 className="font-display text-3xl font-bold leading-tight text-white">
                Never miss another call.
              </h2>
              <p className="mx-auto mt-2.5 max-w-[420px] text-base leading-relaxed text-white/55">
                Your AI receptionist answers, books and follows up — around the clock, in your brand's voice.
              </p>
              <div className="mt-6 flex items-center justify-center gap-2">
                <span className="h-1.5 w-7 rounded-full bg-white" />
                <span className="h-1.5 w-1.5 rounded-full bg-white/30" />
                <span className="h-1.5 w-1.5 rounded-full bg-white/30" />
              </div>
            </div>
          </div>
        </div>
      </motion.div>
    </div>
  )
}
