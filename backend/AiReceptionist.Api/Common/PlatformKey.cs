using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AiReceptionist.Api.Common;

/// <summary>
/// Authenticates the super admin console (AiReceptionist.SuperAdmin) to this API's platform
/// endpoints with a shared secret in <c>X-Platform-Key</c>.
///
/// These endpoints rotate the Retell credential and re-point any tenant's agent, so they fail
/// closed: with no key configured they are unavailable rather than open. A JWT is deliberately
/// not used — the console is a separate application with its own cookie session and no tenant
/// context to put in a token.
/// </summary>
public sealed class PlatformKeyAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Platform-Key";
    public const string ConfigKey = "Platform:AdminKey";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var config = services.GetRequiredService<IConfiguration>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PlatformKey");

        var expected = Environment.GetEnvironmentVariable("PLATFORM__ADMINKEY") ?? config[ConfigKey];

        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32 ||
            expected.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Platform endpoint {Path} refused: {ConfigKey} is not configured (or is still the " +
                "placeholder). Set a random value of at least 32 characters here and in the super " +
                "admin console.", context.HttpContext.Request.Path, ConfigKey);
            context.Result = new ObjectResult(ApiResponse<object>.Fail(
                "Platform administration is not configured on this API."))
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        var presented = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(presented) || !FixedEquals(presented, expected))
        {
            logger.LogWarning("Rejected platform request to {Path} from {Ip}: bad or missing {Header}.",
                context.HttpContext.Request.Path, context.HttpContext.Connection.RemoteIpAddress, HeaderName);
            context.Result = new UnauthorizedObjectResult(
                ApiResponse<object>.Fail("Invalid platform key."));
            return;
        }

        await next();
    }

    /// <summary>Length is compared separately because the fixed-time comparison itself requires
    /// equal-length inputs; leaking the key's length is not a useful signal to an attacker.</summary>
    private static bool FixedEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
