using System.Security.Claims;
using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

[Route("account")]
public class AccountController : Controller
{
    private readonly IPlatformAuthRepository _auth;
    private readonly ILogger<AccountController> _logger;

    public AccountController(IPlatformAuthRepository auth, ILogger<AccountController> logger)
    {
        _auth = auth;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginInput());
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginInput input, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        var user = await _auth.FindSuperAdminByEmailAsync(input.Email?.Trim() ?? "");

        // One message for "no such account", "not a super admin" and "wrong password": telling
        // them apart would let anyone enumerate which addresses are platform operators.
        if (user is null || !Verify(input.Password, user.PasswordHash))
        {
            _logger.LogWarning("Failed super admin sign-in for {Email} from {Ip}.",
                input.Email, HttpContext.Connection.RemoteIpAddress);
            ModelState.AddModelError(string.Empty, "Incorrect email or password.");
            return View(input);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, "SuperAdmin"),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        _logger.LogInformation("Super admin {Email} signed in.", user.Email);

        // Only ever bounce to a path on this site — an absolute returnUrl would turn the login
        // form into an open redirect.
        return Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl!) : RedirectToAction("Index", "Dashboard");
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    /// <summary>A stored hash from an unexpected format throws rather than returning false, which
    /// would surface as a 500 on the login form instead of a failed sign-in.</summary>
    private static bool Verify(string? password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash)) return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
