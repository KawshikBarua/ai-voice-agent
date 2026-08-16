# Frontly SaaS Platform

Multi-tenant SaaS platform that automates inbound phone calls with AI voice agents (Retell AI) and
gives businesses a dashboard for appointments, customers, services, products, knowledge base and calls.
Industry-independent: clinics, plumbers, salons, law firms — any appointment-based business.

Built from the SRS in `Preview.md`; UI design follows the MediApp-style mockup
(soft-teal background, off-white shell, white rounded cards, black pill buttons, yellow promo accents).

## Structure

```
ai-voice-agent/
├─ backend/AiReceptionist.Api/         ASP.NET Core 8 Web API (Dapper + SQL Server + JWT) — the tenant API
├─ backend/AiReceptionist.SuperAdmin/  ASP.NET Core 8 MVC platform console (Razor + cookie auth)
├─ backend/Shared/                     Source files compiled into both server apps (billing schema, entities, cycle maths)
├─ frontend/                           React + Vite (Router, Zustand, React Query, Tailwind v4, Framer Motion)
└─ database/                           SQL scripts for the AiDB database (also run automatically at startup)
```

Both server applications share one database. The tenant API owns the tenant schema; the super
admin console owns the platform tables (`database/03_platform.sql`).

The billing tables are the exception: both applications write them — the console owns tiers and
manual payments, the API owns Stripe and the usage rollup — so their DDL and the types over it live
in `backend/Shared/` and are compiled into both, rather than being duplicated in two initializers
that could drift. Either app can create them, so the two can still start in any order.

## Database — AiDB

The platform uses a SQL Server database named **AiDB** (as requested, instead of LiveDb).

- On startup the API **creates AiDB automatically** (plus schema + demo seed data) if it doesn't exist.
- Manual alternative: run `database/01_create_database.sql` then `database/02_schema.sql`.
- Every tenant table carries `OrganizationId` (strict tenant isolation) and uses soft delete.

Connection string is in `backend/AiReceptionist.Api/appsettings.json`
(defaults to `localhost\SQLEXPRESS` with Windows auth).

## Run it

**Backend** (http://localhost:5200, Swagger at `/swagger`):

```powershell
cd backend/AiReceptionist.Api
dotnet run --urls http://localhost:5200
```

**Frontend** (http://localhost:5173, proxies `/api` to the backend):

```powershell
cd frontend
npm install
npm run dev
```

**Super admin console** (http://localhost:5300) — needs the API running:

```powershell
cd backend/AiReceptionist.SuperAdmin
dotnet run --urls http://localhost:5300
```

**Demo login** (seeded automatically): `admin@demo.com` / `Admin123!`

**Super admin login** (seeded automatically): `superadmin@demo.com` / `Super123!`

## What's implemented

| SRS section | Status |
|---|---|
| Multi-tenant architecture (§4) | `OrganizationId` on every table; tenant resolved from JWT claim; all queries scoped |
| Auth (§5, §20) | JWT + refresh-token rotation, BCrypt hashing, RBAC roles (SuperAdmin/OrgAdmin/Manager/Receptionist/ReadOnly), login rate limiting, audit logging |
| Dashboard (§6) | Calls today, talk time used vs the plan's included minutes, monthly revenue, call analytics by outcome (7/30 days or 12 months, chart or table), revenue over the year, upcoming closures, busiest hours, answer & AI booking rates, today's appointments |
| Customers (§7) | CRUD, search, returning-customer detection (phone → email → name), customer timeline (§10) |
| Appointments (§8) | Book / cancel / confirm / reschedule with conflict detection, payment status |
| Calendar (§9) | Month view with per-day appointment preview |
| Products & Services (§11–12) | CRUD APIs with pagination; products can be disabled per org |
| Knowledge Base (§13) | CRUD + auto-generated **final AI prompt** (system prompt + business info + services + products + FAQs + policies + hours); core instructions not editable by tenants, only platform-wide from the super admin console |
| AI Agent (§14) | Per-tenant config: voice, language, greeting, transfer number, Retell agent id |
| Call management (§15) | Call log with transcript, recording URL, AI summary; Retell webhook endpoint (`POST /api/v1/webhooks/retell`) |
| Security (§20) | Standardized response envelope with traceId, exception middleware, CORS restrictions, IP + login rate limiting, audit trail, soft delete, encrypted web-app payloads (below) |
| API standards (§21) | REST, pagination/filtering, Swagger, response compression, health checks (`/health`) |
| Pricing & billing | Tier catalogue with per-customer overrides, Stripe subscriptions + Checkout + billing portal, metered AI minutes with overage carried onto the next invoice, mirrored invoices, customer-facing billing page ([below](#billing-and-stripe)) |

**Not yet wired** (scaffold points exist): SMTP email sending, Redis distributed cache (in-memory rate
limiter used locally), MFA, onboarding wizard UI, Application Insights.

## Encrypted payloads (web app only)

Request and response bodies between the React app and the tenant API are encrypted at the
application layer, on top of TLS.

- **Handshake.** `GET /api/v1/crypto/handshake` returns an RSA-2048 public key. The browser
  generates an AES-256-GCM key, wraps it with RSA-OAEP-SHA256 and trades it at
  `POST /api/v1/crypto/session` for a session id, which it then sends as `X-Enc-Session` on
  every call. One handshake per page load — the client single-flights it.
- **On the wire.** Bodies are `{"enc":"<base64 nonce||ciphertext||tag>"}`. Responses carry
  `X-Enc: 1`. `frontend/src/api/crypto.js` and the axios interceptors in `client.js` do this, so
  pages keep passing and reading plain objects.
- **Session loss.** A restart drops the key pair and every session; the server answers `409` with
  `X-Enc-Renew: 1` and the client re-handshakes and retries once — invisible to the user. Sessions
  are held in memory, so this wants a single instance or sticky sessions until the Redis scaffold
  point is wired.
- **Exempt by path** — always plain JSON: `/api/v1/webhooks/*` and `/api/v1/ai/tools/*` (Retell
  signs the raw body with `X-Retell-Signature`, so rewriting it would break verification),
  `/api/v1/platform/*` (the super admin console), `/api/v1/crypto/*`, `/health` and `/swagger`.
- **Settings** — `Encryption:Enabled` (default true) and `Encryption:Required` (default false).
  With `Required` false, an unencrypted client still works, which keeps Swagger and curl usable;
  set it true to refuse unsealed requests on every non-exempt endpoint.

**What this is worth.** The AES key lives in the browser, so anything that can run script in the
page can read it — this is not a second authentication factor. It keeps payloads out of proxy
logs, browser devtools, disk caches and casual inspection. TLS is still what secures the
connection, and the JWT is still what authorizes the caller.

## Super admin console

A separate ASP.NET Core 8 MVC application (`backend/AiReceptionist.SuperAdmin`, port 5300). There is
no super admin *inside* an organization any more: the platform operator signs in here with a cookie
session, and the tenant app exposes nothing to the `SuperAdmin` role.

- **Dashboard** — every organization's revenue (paid appointments), outstanding balance, call volume
  and missed/transferred split, appointments, customers, users, AI agent state and subscription
  standing. Per-organization detail adds monthly call volume, recent calls and recent appointments.
- **Retell AI** — the platform-wide API key, base URL, webhook URL, default voice and signature
  verification live on one screen. Each tenant is either **Connect**-able or, once an agent exists,
  offers only **Re-sync** and **Disconnect**. The rule is enforced server-side, so an organization can
  never end up with a second agent.
- **AI Prompt** — the wording every tenant's receptionist is built from: how it talks, the rules it
  cannot break, the shape of a call (one version for businesses the customer visits, one for trades
  that travel to them) and when it may call a tool. One text for the whole platform. Saving stores
  it and touches nothing on Retell: a connected agent keeps the prompt it was built with until it is
  re-synced, so a rollout stays a deliberate, per-organization act. A section left blank falls back to the built-in default in
  `Services/PromptDefaults.cs`, so only genuine overrides are stored and "restore defaults" is a
  matter of clearing the box. The screen also previews the finished prompt for any tenant, with that
  tenant's industry, hours and closures filled in.
- **Pricing** — the tier catalogue: name, price, cycle, the AI minutes included per period and the
  price of each minute beyond them. Creating a tier can create the matching Stripe price in one
  step. Assigning a tier **copies** its numbers onto the organization's subscription, so repricing a
  tier never silently re-bills anyone already on it; any of those numbers can then be overridden for
  one customer. Repricing a tier creates a fresh Stripe price (they are immutable upstream) and
  leaves existing subscribers on the old one until they are deliberately moved. Retiring a tier
  removes it from the catalogue only.
- **Billing** — every organization's plan, minutes used against its allowance, standing and
  collection method on one screen. Payment arrives either through Stripe or by hand, and both land
  in the same payment history. An organization counts as paid while its period plus grace days has
  not lapsed. Accounts can be disabled by hand, or automatically by a sweep that runs every few
  hours (`Platform:AutoSuspendSweepHours`). Paying up — through Stripe or manually — re-enables an
  automatically disabled account; a manual suspension is only ever lifted by hand.
- **Restricting one agent** — narrower than disabling an account. The organization's agent stops
  taking calls (its number is detached from Retell and its live-call tools are refused) while
  sign-in keeps working, so a customer who has been cut off can still log in, read why on their
  billing page, and settle up. Ordinary tenant edits queue an agent re-sync, which deliberately
  skips re-attaching the number while a restriction is in force.

**Disabling an organization** (`Organizations.IsActive = 0`) is enforced by the tenant API: sign-in
and token refresh return 403, outstanding refresh tokens are burned, and the AI agent's live-call
tools are refused — so a suspended tenant stops costing money within one access-token lifetime.

Agent creation is *not* reimplemented in the console. It calls the API's platform endpoints
(`/api/v1/platform/retell/*`), authenticated with the shared `Platform:AdminKey` in an
`X-Platform-Key` header, so the prompt builder and tool definitions stay in one place and cannot
drift from the API's background re-sync worker. Set the same random 32+ character value in both
apps' `appsettings.Local.json` (or `PLATFORM__ADMINKEY`); neither app will start, and the endpoints
return 503, on a missing or placeholder key.

## Billing and Stripe

Money is metered on **AI talk time**. Each call is rounded up to a whole minute — the definition the
tenant dashboard has always used — so the dashboard, the console and the invoice can never disagree
about a number the customer is charged for.

- **Two clocks, deliberately.** A subscription's `CurrentPeriodStart/End` is the *access* window: it
  moves only when a payment is recorded, and the grace period and overdue sweep read it. Usage is
  counted in its own windows, anchored to the end of the last period that closed. Without that split,
  an unpaid customer's minutes would all pile into one window that never closes.
- **Overage is carried, not billed separately.** When a usage window elapses it is totalled into
  `OrganizationUsagePeriods` — allowance, minutes used, minutes over, the rate and the amount — and
  the amount is held on the subscription as pending. When Stripe opens the next invoice
  (`invoice.created`), that amount is attached as its own line, worded so the charge explains
  itself: *"AI minutes over plan: 142 min beyond the 500 included, at 0.12 USD per minute (1 Jul –
  1 Aug 2026)"*. A closed period is never recalculated, so a customer asking about an old charge
  gets the numbers that were actually used.
- **Closing is triggered three ways** — a worker every `Billing:PeriodSweepHours`, the
  `invoice.created` webhook, and opening the billing page — and is idempotent under all of them
  (a unique index on organization + period end).
- **A plan with 0 included minutes is a flat fee**: usage is still recorded, never charged.

**Customers** see all of this at `/billing` in the tenant app: the next bill itemised with the
reason under each line, minutes used against the allowance, every period that has closed with its
arithmetic spelled out, and their invoices. Two Stripe actions are offered — Checkout to set up
automatic payment, and the Stripe billing portal to change the card — and card details never reach
this application.

**Both ways of getting paid** work per customer. *Subscribe in Stripe* charges a card on file each
cycle; *Email an invoice* raises a one-off Stripe invoice the customer pays by link, sweeping in any
carried-over overage. Neither is required: with no Stripe key at all every screen still works and
payment is recorded by hand, exactly as before.

### Setup

1. Put your Stripe **secret key** in `backend/AiReceptionist.Api/appsettings.Local.json`
   (git-ignored): `{ "Stripe": { "SecretKey": "sk_...", "WebhookSecret": "whsec_..." } }`
   — or set `STRIPE__SECRETKEY` / `STRIPE__WEBHOOKSECRET`.
2. The API must be publicly reachable for Stripe to call back — the same tunnel Retell already
   needs (`ngrok http 5200`, or a Cloudflare tunnel).
3. In the Stripe dashboard add an endpoint at `https://<your-public-url>/api/v1/webhooks/stripe`,
   subscribed to `invoice.created`, `invoice.finalized`, `invoice.paid`, `invoice.payment_failed`,
   `checkout.session.completed` and `customer.subscription.*`. Copy its signing secret into
   `Stripe:WebhookSecret`. **Without it every delivery is rejected**, so invoices and payments are
   never mirrored back.
4. Set `Stripe:AppBaseUrl` to where the tenant app is served if it is not the first
   `Cors:AllowedOrigins` entry — it is where Stripe returns the customer after Checkout.
5. In the super admin console: **Pricing → New tier**, tick "Create the matching price in Stripe",
   then open an organization and **Apply tier**.

The webhook endpoint is exempt from payload encryption by path (`/api/v1/webhooks/*`), because
Stripe signs the raw body. Every handler is safe to run twice: event ids are claimed before they
are acted on, and a redelivered paid invoice cannot record a second payment or move the billing
period twice.

## Retell AI integration

Fully implemented in `Services/RetellService.cs`, `Controllers/RetellController.cs`,
`Controllers/AiToolsController.cs` and `Controllers/RetellWebhookController.cs`:

- **Agent sync** — `POST /api/v1/platform/retell/{orgId}/sync` (the *Connect* / *Re-sync* buttons in
  the super admin console) creates or updates the tenant's Retell LLM (generated final prompt +
  tools + greeting) and Agent (voice, language, webhook). Subsequent knowledge-base / service /
  product / settings changes re-sync automatically via a background worker. Tenants themselves get
  only `GET /api/v1/retell/status`, which is read-only.
- **Live-call tools** — during calls the agent invokes `POST /api/v1/ai/tools/{orgId}/...`:
  `identify_customer`, `check_availability`, `book_appointment`, `cancel_appointment`,
  `reschedule_appointment`, `check_appointment` — plus Retell's built-in `end_call` and
  `transfer_call` (to the configured transfer number). All data comes from AiDB, so the AI never
  invents appointments (SRS §19). There is **no pricing tool**: the service and product catalogues
  are uploaded as knowledge-base documents on every sync, so the agent answers a price question from
  context instead of pausing the call for a round-trip. Prices therefore reflect the last sync —
  fine for prices, worth knowing for stock counts.
- **Webhook** — `POST /api/v1/webhooks/retell` handles `call_ended` / `call_analyzed`: stores the call
  log, transcript, recording URL and AI summary, matches returning customers by phone, and writes
  customer timeline events. Tenant is resolved from the payload's `agent_id`.
- **Transferred-call intent capture** — cold transfers hand the call to a human, so the AI's live
  tools stop firing and anything the human books is invisible to the system. A background worker
  (`Services/CallIntentWorker.cs`) reads the transcript of each **transferred** call with Claude
  (`Services/CallIntentService.cs`) and stores a suggested book/cancel/reschedule action. Staff
  review it on the **Calls** screen and confirm or dismiss — the AI never books off a transfer
  (SRS §19). Requires an Anthropic API key (`Anthropic:ApiKey`); the feature no-ops without one.
  Confirm reuses the same capacity-checked booking path as the live AI tools.
- **Security** — webhook and tool requests are verified with `X-Retell-Signature`
  (HMAC-SHA256 of the raw body keyed with the API key).

### Setup

1. Put your Retell API key in `backend/AiReceptionist.Api/appsettings.Local.json` (git-ignored):
   `{ "Retell": { "ApiKey": "key_...", "WebhookBaseUrl": "" } }`
2. Expose the API publicly (Retell rejects localhost URLs): `ngrok http 5200` or
   `cloudflared tunnel --url http://localhost:5200`, then set `WebhookBaseUrl` to the public URL
   and restart the API.
3. In the super admin console: **Retell AI → Connect** on the organization.
4. Buy or import a phone number in the Retell dashboard (or skip this and use the dashboard's
   web test-call feature without a number).
5. Enter that number in **Settings → AI Agent → Retell phone number**, in E.164 format
   (`+15551234567`). Each sync points it at this tenant's agent, so inbound calls are answered;
   a number is never bought automatically, and one number can only serve one organization.
   If the number is not on the Retell account the sync says so and the agent is left untouched.

## Secrets

Never commit real keys. Override via environment variables, e.g. `JWT__SECRET`,
`ConnectionStrings__AiDB`, `Retell__ApiKey`, `Anthropic__ApiKey`, `Smtp__Password`,
`STRIPE__SECRETKEY`, `STRIPE__WEBHOOKSECRET`.
