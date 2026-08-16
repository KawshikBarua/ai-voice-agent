using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

/// <summary>Shared helpers for the signed-in console: who is acting, and the one-shot banner
/// shown after a redirect.</summary>
public abstract class PlatformControllerBase : Controller
{
    protected int? CurrentUserId =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    protected void Notify(string message) => TempData["Notice"] = message;

    protected void Warn(string message) => TempData["Problem"] = message;

    /// <summary>Reports the outcome of an operation on the banner without the caller having to
    /// branch at every call site.</summary>
    protected void Report(bool ok, string message)
    {
        if (ok) Notify(message); else Warn(message);
    }
}
