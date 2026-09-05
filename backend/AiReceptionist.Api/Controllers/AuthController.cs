using System.ComponentModel.DataAnnotations;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AiReceptionist.Api.Controllers;

/// <summary><paramref name="RememberMe"/> is the "Keep me signed in" tick. False issues the refresh
/// cookie as a session cookie, so closing the browser ends the session; true keeps it for
/// <see cref="ITokenService.RefreshTokenDays"/>. Defaults to false for callers that omit it —
/// the safer of the two on a shared machine.</summary>
public record LoginRequest(string Email, string Password, bool RememberMe = false);

/// <summary>Body is optional — the refresh token normally travels in the httpOnly cookie.
/// Retained so existing callers that post the token explicitly keep working.</summary>
public record RefreshRequest(string? RefreshToken);

/// <summary>The refresh token is deliberately absent: it is issued as an httpOnly cookie so
/// page scripts (and therefore any XSS) cannot read it. Only the short-lived access token is
/// handed to JavaScript.</summary>
public record AuthResult(string AccessToken, object User);

/// <summary>Public sign-up for a new client business (tenant). Creates the organization plus
/// its first administrator account with the basic information needed to get started.</summary>
public record RegisterRequest(
    [Required, StringLength(200, MinimumLength = 2)] string BusinessName,
    [Required, StringLength(100, MinimumLength = 2)] string AdminName,
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(100, MinimumLength = 8)] string Password,
    string? Industry = null,
    string? Phone = null);

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthRepository _auth;
    private readonly ITokenService _tokens;
    private readonly IAuditRepository _audit;
    private readonly ITenantProvider _tenant;
    private readonly IHostEnvironment _env;

    /// <summary>Scoped to the auth routes: the browser only ever needs to present the refresh
    /// token when signing in, refreshing or signing out, so it is not attached to every API call.</summary>
    private const string RefreshCookieName = "frontly_rt";
    private const string RefreshCookiePath = "/api/v1/auth";

    public AuthController(IAuthRepository auth, ITokenService tokens, IAuditRepository audit,
        ITenantProvider tenant, IHostEnvironment env)
    {
        _auth = auth;
        _tokens = tokens;
        _audit = audit;
        _tenant = tenant;
        _env = env;
    }

    /// <summary>
    /// Issues a rotated refresh token as an httpOnly cookie and stores its hash.
    ///
    /// <paramref name="persistent"/> is the "Keep me signed in" choice. It governs the cookie only:
    /// without an Expires the browser drops it when it closes, which is what ends the session on a
    /// shared machine. The stored token still expires on its own schedule either way, so a cookie
    /// that outlives its row is refused rather than honoured.
    /// </summary>
    private async Task<string> IssueRefreshCookieAsync(int userId, bool persistent)
    {
        var refresh = _tokens.CreateRefreshToken();
        var expires = DateTime.UtcNow.AddDays(_tokens.RefreshTokenDays);
        await _auth.StoreRefreshTokenAsync(new Domain.RefreshToken
        {
            UserId = userId,
            Token = refresh,
            ExpiresAt = expires,
            Persistent = persistent,
        });

        Response.Cookies.Append(RefreshCookieName, refresh, new CookieOptions
        {
            HttpOnly = true,                        // unreadable from JavaScript
            Secure = !_env.IsDevelopment(),         // plain http is only tolerated locally
            SameSite = SameSiteMode.Strict,         // not sent on cross-site requests (CSRF)
            Path = RefreshCookiePath,
            // Omitted entirely when not persistent — a session cookie, gone with the browser.
            Expires = persistent ? expires : null,
        });
        return refresh;
    }

    private void ClearRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = !_env.IsDevelopment(),
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
        });

    /// <summary>Cookie first, explicit body second (for non-browser callers).</summary>
    private string? ReadRefreshToken(RefreshRequest? request) =>
        Request.Cookies.TryGetValue(RefreshCookieName, out var cookie) && !string.IsNullOrWhiteSpace(cookie)
            ? cookie
            : request?.RefreshToken;

    // Password guessing is throttled per IP by the "login" policy — without it the only limit
    // is the global 200 req/min bucket, which is ample for an online brute-force attack.
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await _auth.FindByEmailAsync(request.Email.Trim().ToLowerInvariant());
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            if (user is not null)
                await _audit.LogAsync(user.OrganizationId, user.Id, "LoginFailed", ip: ClientIp());
            return Unauthorized(ApiResponse<object>.Fail("Invalid email or password."));
        }

        // A disabled organization (unpaid subscription, or suspended from the super admin console)
        // must not get a token at all — this is the point where access actually stops.
        if (!await _auth.IsOrganizationActiveAsync(user.OrganizationId))
        {
            await _audit.LogAsync(user.OrganizationId, user.Id, "LoginBlockedAccountDisabled", ip: ClientIp());
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(
                "This account has been disabled. Please contact support."));
        }

        var access = _tokens.CreateAccessToken(user);
        await IssueRefreshCookieAsync(user.Id, request.RememberMe);
        await _audit.LogAsync(user.OrganizationId, user.Id, "Login", ip: ClientIp());

        return Ok(ApiResponse<AuthResult>.Ok(new AuthResult(access, new
        {
            user.Id,
            user.FullName,
            user.Email,
            user.Role,
            user.OrganizationId
        }), "Signed in successfully."));
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await _auth.EmailExistsAsync(email))
            return Conflict(ApiResponse<object>.Fail("An account with this email already exists."));

        var org = new Domain.Organization
        {
            Name = request.BusinessName.Trim(),
            Industry = request.Industry?.Trim() ?? "",
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            Email = email
        };
        var admin = new Domain.User
        {
            FullName = request.AdminName.Trim(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = Roles.OrgAdmin
        };

        var user = await _auth.RegisterOrganizationAsync(org, admin);
        await _audit.LogAsync(user.OrganizationId, user.Id, "OrganizationRegistered",
            $"Business={org.Name}", ClientIp());

        var access = _tokens.CreateAccessToken(user);
        // Somebody setting their business up is on their own machine and has more to do than sign
        // in again, so sign-up keeps them signed in. They can end it from the login page next time.
        await IssueRefreshCookieAsync(user.Id, persistent: true);

        return Ok(ApiResponse<AuthResult>.Ok(new AuthResult(access, new
        {
            user.Id,
            user.FullName,
            user.Email,
            user.Role,
            user.OrganizationId
        }), "Account created successfully."));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest? request = null)
    {
        var presented = ReadRefreshToken(request);
        if (string.IsNullOrWhiteSpace(presented))
            return Unauthorized(ApiResponse<object>.Fail("Refresh token is invalid or expired."));

        var stored = await _auth.FindRefreshTokenAsync(presented);
        if (stored is null || stored.ExpiresAt < DateTime.UtcNow)
        {
            ClearRefreshCookie();
            return Unauthorized(ApiResponse<object>.Fail("Refresh token is invalid or expired."));
        }

        // Replay of an already-rotated token means the token leaked (the legitimate client
        // would hold the newer one). Burn the whole family rather than just rejecting this call.
        if (stored.Revoked)
        {
            await _auth.RevokeAllForUserAsync(stored.UserId);
            var owner = await _auth.FindByIdAsync(stored.UserId);
            if (owner is not null)
                await _audit.LogAsync(owner.OrganizationId, owner.Id, "RefreshTokenReuseDetected", ip: ClientIp());
            ClearRefreshCookie();
            return Unauthorized(ApiResponse<object>.Fail("Refresh token is invalid or expired."));
        }

        var user = await _auth.FindByIdAsync(stored.UserId);
        if (user is null)
        {
            ClearRefreshCookie();
            return Unauthorized(ApiResponse<object>.Fail("User no longer exists."));
        }

        // Refresh is the renewal point for a session that is already running, so an account
        // disabled mid-session loses access here — at most one access-token lifetime later.
        if (!await _auth.IsOrganizationActiveAsync(user.OrganizationId))
        {
            await _auth.RevokeAllForUserAsync(user.Id);
            ClearRefreshCookie();
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(
                "This account has been disabled. Please contact support."));
        }

        await _auth.RevokeRefreshTokenAsync(presented);
        var access = _tokens.CreateAccessToken(user);
        // The rotation inherits the original choice: a session the user did not ask to keep must
        // not become a persistent one just because the access token was renewed once.
        await IssueRefreshCookieAsync(user.Id, stored.Persistent);

        return Ok(ApiResponse<AuthResult>.Ok(new AuthResult(access, new
        {
            user.Id, user.FullName, user.Email, user.Role, user.OrganizationId
        })));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? request = null)
    {
        // Only revoke a token that belongs to the caller — otherwise any authenticated user
        // could sign out anyone else whose token they got hold of.
        var presented = ReadRefreshToken(request);
        if (!string.IsNullOrWhiteSpace(presented))
        {
            var stored = await _auth.FindRefreshTokenAsync(presented);
            if (stored is not null && stored.UserId == _tenant.UserId)
                await _auth.RevokeRefreshTokenAsync(presented);
        }

        ClearRefreshCookie();
        return Ok(ApiResponse<object>.Ok(new { }, "Signed out."));
    }

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
