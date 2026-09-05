using AiReceptionist.SuperAdmin.Data;
using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Git-ignored local secrets (Platform:AdminKey, connection string overrides).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ---------- Services ----------
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddScoped<IPlatformAuthRepository, PlatformAuthRepository>();
builder.Services.AddScoped<IOrganizationRepository, OrganizationRepository>();
builder.Services.AddScoped<IUserAccountRepository, UserAccountRepository>();
builder.Services.AddScoped<IBillingRepository, BillingRepository>();
builder.Services.AddScoped<ILandingContentRepository, LandingContentRepository>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddHostedService<AutoSuspendWorker>();

// The public marketing site is a separate React app on its own origin, so its browser needs
// permission to read /api/landing-content. Origins are listed in configuration rather than
// wildcarded: the endpoint is anonymous, and a wildcard would let any page on the internet embed
// this console's responses.
var landingOrigins = builder.Configuration.GetSection("Landing:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5400"];
builder.Services.AddCors(options =>
    options.AddPolicy(AiReceptionist.SuperAdmin.Controllers.LandingController.CorsPolicy, policy =>
    {
        policy.WithMethods("GET").WithHeaders("Accept", "Content-Type");

        if (builder.Environment.IsDevelopment())
        {
            // Any loopback port is fine while developing. A dev server that finds its usual port
            // busy moves to the next one, and pinning the allowlist to a single port turns that
            // into a CORS error that reads as a bug in this app rather than a busy port.
            // Loopback only — this is not a wildcard, and it never applies outside Development.
            policy.SetIsOriginAllowed(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback);
        }
        else
        {
            policy.WithOrigins(landingOrigins);
        }
    }));

// The platform key authenticates this app to the tenant API's /api/v1/platform endpoints, which
// can rotate the Retell credential and re-point any tenant's agent. A shipped placeholder is
// public knowledge, so refuse to start on one rather than run with a guessable console-to-API link.
var platformKey = Environment.GetEnvironmentVariable("PLATFORM__ADMINKEY")
    ?? builder.Configuration["Platform:AdminKey"];
if (string.IsNullOrWhiteSpace(platformKey) || platformKey.Length < 32 ||
    platformKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Platform:AdminKey is missing, shorter than 32 characters, or still the placeholder. Set the " +
        "same random value here (appsettings.Local.json or PLATFORM__ADMINKEY) and in AiReceptionist.Api " +
        "before starting the console.");
}

var apiBaseUrl = builder.Configuration["Platform:ApiBaseUrl"] ?? "http://localhost:5200";
builder.Services.AddHttpClient<IPlatformApiClient, PlatformApiClient>(http =>
{
    http.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
    http.DefaultRequestHeaders.Add("X-Platform-Key", platformKey);
    // A Retell sync creates an LLM and an agent over several upstream calls; the default
    // 100 seconds is tight when Retell is slow, and a timeout here looks like a failed sync.
    http.Timeout = TimeSpan.FromSeconds(180);
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SuperAdmin.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // Development runs on plain http://localhost; anywhere else the cookie is the session,
        // so it must never travel in the clear.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(builder.Configuration.GetValue("Platform:SessionHours", 8));
        options.SlidingExpiration = true;
    });

// Every page requires a signed-in SuperAdmin unless it opts out with [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole("SuperAdmin")
        .Build());

var app = builder.Build();

// ---------- Database bootstrap (platform tables inside the existing AiDB) ----------
try
{
    PlatformDbInitializer.Initialize(app.Configuration, app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogError(ex,
        "Could not initialize the platform schema. Ensure SQL Server is running, AiDB exists (start " +
        "AiReceptionist.Api once) and the connection string is correct. The console will start, but " +
        "database calls will fail until then.");
}

// ---------- Pipeline ----------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/home/error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
// Must sit between routing and authorization so the preflight and the actual cross-origin GET
// both carry the policy's headers.
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(name: "default", pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();
