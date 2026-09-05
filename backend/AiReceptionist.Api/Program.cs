using System.Text;
using System.Threading.RateLimiting;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Middleware;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Git-ignored local secrets (e.g. Retell API key) — see appsettings.Local.json
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ---------- Services ----------
builder.Services.AddControllers();

// Swagger is served in Development only (see the pipeline below), so only build the generator
// there — production then has nothing registered that a stray UseSwagger() could expose.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddResponseCompression();

// Payload encryption for the web app (see PayloadEncryptionMiddleware). Singleton: it owns the
// RSA key pair the browser wraps its per-session AES key with, plus the live sessions.
builder.Services.AddSingleton<IPayloadKeyRing, PayloadKeyRing>();

builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddScoped<ITenantProvider, TenantProvider>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPromptBuilderService, PromptBuilderService>();

// Retell AI integration (SRS §14–15): agent sync + background re-sync worker
builder.Services.AddHttpClient<IRetellService, RetellService>();
builder.Services.AddSingleton<IRetellSyncQueue, RetellSyncQueue>();
builder.Services.AddHostedService<RetellSyncWorker>();

// Transferred-call intent capture: mine transcripts for booking intent (Anthropic)
builder.Services.AddHttpClient<ICallIntentService, CallIntentService>();
builder.Services.AddHostedService<CallIntentWorker>();

builder.Services.AddScoped<IAuthRepository, AuthRepository>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IAppointmentRepository, AppointmentRepository>();
builder.Services.AddScoped<ICatalogRepository, CatalogRepository>();
builder.Services.AddScoped<ICallRepository, CallRepository>();
builder.Services.AddScoped<IKnowledgeRepository, KnowledgeRepository>();
builder.Services.AddScoped<IHolidayRepository, HolidayRepository>();
builder.Services.AddScoped<ISettingsRepository, SettingsRepository>();
builder.Services.AddScoped<IDashboardRepository, DashboardRepository>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<IAiToolsRepository, AiToolsRepository>();
builder.Services.AddScoped<ICallActionSuggestionRepository, CallActionSuggestionRepository>();
builder.Services.AddScoped<IRetellConnectionRepository, RetellConnectionRepository>();
builder.Services.AddScoped<IPromptTemplateRepository, PromptTemplateRepository>();

// Billing: tiers, Stripe, usage periods and the overage carried to the next invoice.
builder.Services.AddScoped<IBillingRepository, BillingRepository>();
builder.Services.AddSingleton<IStripeGateway, StripeGateway>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddHostedService<BillingPeriodWorker>();
// The webhook is the fast path for payments; this is the guarantee. It re-reads what Stripe has
// actually collected and applies anything no delivery ever arrived for.
builder.Services.AddHostedService<BillingReconciliationWorker>();

// JWT authentication (SRS §5, §20)
var jwtSecret = Environment.GetEnvironmentVariable("JWT__SECRET") ?? builder.Configuration["Jwt:Secret"];

// The signing key is the single thing standing between an attacker and a forged token for any
// tenant or role (orgId + role are claims). A shipped placeholder is public knowledge, so refuse
// to start on one rather than run a silently-forgeable system.
if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < 32 ||
    jwtSecret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Jwt:Secret is missing, too short, or still the placeholder. Set a random secret of at " +
        "least 32 characters via the JWT__SECRET environment variable (or Jwt:Secret in " +
        "appsettings.Local.json) before starting the API.");
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

// CORS restricted to the frontend origin (SRS §20)
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
// AllowCredentials is required for the httpOnly refresh cookie to be sent on cross-origin
// calls. It is only legal alongside an explicit origin list (never AllowAnyOrigin).
builder.Services.AddCors(options =>
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// Rate limiting (SRS §20 — Redis-backed in production; in-memory fixed window here)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        // Retell's live-call tools and webhooks arrive from few egress IPs at high volume —
        // give them their own generous bucket so calls are never throttled mid-conversation.
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/api/v1/ai/tools", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/webhooks", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetFixedWindowLimiter("retell-traffic",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 3000, Window = TimeSpan.FromMinutes(1) });

        return RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 200, Window = TimeSpan.FromMinutes(1) });
    });
    // Credential endpoints (login / register / refresh) get a tight bucket to stop online
    // password guessing. It MUST be partitioned per client IP: a single shared limiter would
    // let one attacker burn the whole allowance and lock every tenant out of signing in.
    options.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        $"login:{ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous"}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));

    // Token refresh is a legitimate per-page-load operation (the access token is held in
    // memory only), so it needs far more headroom than password entry — but still capped.
    options.AddPolicy("refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(
        $"refresh:{ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous"}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddHealthChecks();

var app = builder.Build();

// ---------- Database bootstrap (creates AiDB + schema + demo seed) ----------
try
{
    DbInitializer.Initialize(app.Configuration, app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogError(ex,
        "Could not initialize AiDB. Ensure SQL Server is running and the connection string is correct. " +
        "The API will start, but database calls will fail until the database is available.");
}

// ---------- Secret hygiene ----------
// The Retell key is not merely an API credential: it doubles as the HMAC secret for inbound
// webhooks and live-call tool tokens, so leaking it lets an attacker forge traffic into this API
// rather than just spend Retell credits. Because GetEffectiveAsync accepts Retell:ApiKey from any
// configuration source, a key pasted into a tracked file works silently while shipping with the
// repo — the failure mode is invisible at runtime, so report it at boot instead.
foreach (var (key, label) in new[]
{
    ("Retell:ApiKey", "Retell API key"),
    ("Jwt:Secret", "JWT signing key"),
    ("Platform:AdminKey", "platform administration key"),
    ("Anthropic:ApiKey", "Anthropic API key"),
    ("Smtp:Password", "SMTP password"),
    ("Stripe:SecretKey", "Stripe secret key"),
    ("Stripe:WebhookSecret", "Stripe webhook signing secret"),
})
{
    foreach (var file in TrackedConfigFilesFor(app.Configuration, key))
        app.Logger.LogWarning(
            "SECURITY: {Label} ({Key}) is present in {File}, which is not git-ignored. Move it to " +
            "appsettings.Local.json or an environment variable, then rotate the exposed value.",
            label, key, file);
}

// The "_"-prefixed entries in appsettings.json are documentation — nothing reads them — so a real
// secret parked there is exposed for no functional benefit. Prose has spaces; secrets do not.
foreach (var slot in new[] { "Retell:_ApiKey", "Jwt:_Secret", "Platform:_AdminKey" })
{
    if (app.Configuration[slot] is { Length: > 0 } note && !note.Contains(' '))
        app.Logger.LogWarning(
            "SECURITY: {Slot} in appsettings.json holds a value that looks like a real secret. That " +
            "entry is a comment and is never read — delete it and rotate the value.", slot);
}

// A key resolved from configuration rather than the RetellConnection row still works, but it means
// the super admin console is not the source of truth and rotation needs a redeploy.
try
{
    using var scope = app.Services.CreateScope();
    var stored = await scope.ServiceProvider.GetRequiredService<IRetellConnectionRepository>().GetAsync();
    if (string.IsNullOrWhiteSpace(stored?.ApiKey) && !string.IsNullOrWhiteSpace(app.Configuration["Retell:ApiKey"]))
        app.Logger.LogWarning(
            "Retell API key is resolving from configuration, not the RetellConnection row. Set it in " +
            "the super admin console (Retell AI) so it can be rotated without a redeploy.");
}
catch (Exception ex)
{
    app.Logger.LogDebug(ex, "Skipped the Retell key source check (database unavailable).");
}

// ---------- Pipeline ----------
app.UseMiddleware<ExceptionMiddleware>();

// Swagger publishes a complete map of every endpoint and payload shape — useful to an attacker
// probing production, so keep it to non-production environments.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.UseResponseCompression();
app.UseCors("Frontend");

// Payload encryption sits inside compression (so what goes on the wire is the compressed form
// of the sealed body, and Content-Encoding still describes it) and inside CORS (so the browser
// is allowed to read a session-expired response and retry the handshake). Everything after this
// point — rate limiter rejections, 401s, validation errors, controller output — is sealed for a
// client that presents a session; Retell and the platform endpoints are exempt by path.
if (app.Configuration.GetValue("Encryption:Enabled", true))
    app.UseMiddleware<PayloadEncryptionMiddleware>();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

/// <summary>Tracked (non "*.Local.json") config files carrying a value for <paramref name="key"/>.
/// Every provider is inspected rather than just the winning one, because a committed secret that
/// happens to be shadowed by a local override is still a committed secret.</summary>
static IEnumerable<string> TrackedConfigFilesFor(IConfiguration config, string key)
{
    if (config is not IConfigurationRoot root) yield break;

    foreach (var provider in root.Providers)
    {
        if (!provider.TryGet(key, out var value) || string.IsNullOrWhiteSpace(value)) continue;

        if ((provider as FileConfigurationProvider)?.Source.Path is { } path &&
            !path.Contains(".Local.", StringComparison.OrdinalIgnoreCase))
            yield return path;
    }
}
