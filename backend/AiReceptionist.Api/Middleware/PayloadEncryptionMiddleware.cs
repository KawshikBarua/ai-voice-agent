using System.Text;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Services;

namespace AiReceptionist.Api.Middleware;

/// <summary>
/// Application-layer encryption of JSON request and response bodies for the web app.
///
/// A client that has completed the handshake sends <c>X-Enc-Session</c> on every call. Its request
/// body arrives as <c>{"enc":"&lt;base64&gt;"}</c> and is swapped for the plaintext before model
/// binding; the JSON that comes back is wrapped the same way and marked with <c>X-Enc: 1</c>.
/// Nothing else in the API knows this is happening.
///
/// What this is and is not: the AES key lives in the browser, so anyone who can run script in the
/// page can read it. This is defence against payloads sitting readable in proxy logs, browser
/// devtools and disk caches — not a substitute for TLS, which still carries everything.
///
/// Machine-to-machine callers are excluded by path, so Retell's webhook and live-call tools keep
/// posting plain JSON with their existing HMAC signature over the raw body — a body this middleware
/// must therefore never rewrite.
/// </summary>
public class PayloadEncryptionMiddleware
{
    /// <summary>Callers that are not the web app. Retell signs the raw body, the super admin
    /// console authenticates with a shared key, and the handshake itself has to be readable.</summary>
    private static readonly string[] ExemptPrefixes =
    [
        "/api/v1/crypto",
        "/api/v1/webhooks",
        "/api/v1/ai/tools",
        "/api/v1/platform",
        "/health",
        "/swagger",
    ];

    public const string SessionHeader = "X-Enc-Session";
    public const string EncryptedHeader = "X-Enc";
    /// <summary>Tells the client its session is gone and a fresh handshake will fix it.</summary>
    public const string RenewHeader = "X-Enc-Renew";

    private readonly RequestDelegate _next;
    private readonly IPayloadKeyRing _keyRing;
    private readonly ILogger<PayloadEncryptionMiddleware> _logger;
    private readonly bool _required;

    public PayloadEncryptionMiddleware(RequestDelegate next, IPayloadKeyRing keyRing,
        IConfiguration configuration, ILogger<PayloadEncryptionMiddleware> logger)
    {
        _next = next;
        _keyRing = keyRing;
        _logger = logger;
        _required = configuration.GetValue("Encryption:Required", false);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (IsExempt(path))
        {
            await _next(context);
            return;
        }

        var sessionId = context.Request.Headers[SessionHeader].ToString();

        if (string.IsNullOrEmpty(sessionId))
        {
            // Off by default so Swagger, curl and integration tests keep working; on, this is
            // what stops a client from simply omitting the header to opt out.
            if (_required && !HttpMethods.IsOptions(context.Request.Method))
            {
                await WriteProblemAsync(context, StatusCodes.Status400BadRequest,
                    "This endpoint requires an encrypted payload session.", renew: true);
                return;
            }
            await _next(context);
            return;
        }

        var key = _keyRing.Resolve(sessionId);
        if (key is null)
        {
            // 409 rather than 401: nothing is wrong with the caller's credentials, only with the
            // transport session, and the client's retry must not look like an auth failure.
            await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                "The encrypted payload session has expired. Repeat the handshake.", renew: true);
            return;
        }

        if (!await TryDecryptRequestAsync(context, key))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest,
                "The request payload could not be decrypted.", renew: false);
            return;
        }

        await EncryptResponseAsync(context, key);
    }

    private static bool IsExempt(string path) =>
        ExemptPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces the request body with its plaintext. A body-less request (GET, DELETE)
    /// is nothing to decrypt and still counts as success — the header alone asks for an
    /// encrypted response.</summary>
    private async Task<bool> TryDecryptRequestAsync(HttpContext context, byte[] key)
    {
        var request = context.Request;
        if (request.ContentLength is null or 0 && !request.Headers.ContainsKey("Transfer-Encoding"))
            return true;

        if (request.ContentType is { } type && !type.Contains("json", StringComparison.OrdinalIgnoreCase))
            return true; // file uploads and form posts are left alone

        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var raw = await reader.ReadToEndAsync(context.RequestAborted);
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var envelope = ReadEnvelope(raw);
        if (envelope is null)
        {
            _logger.LogWarning("Encrypted session {Session} sent an unwrapped body to {Path}.",
                Redact(context.Request.Headers[SessionHeader].ToString()), context.Request.Path);
            return false;
        }

        if (!PayloadCrypto.TryDecrypt(envelope, key, out var plaintext)) return false;

        request.Body = new MemoryStream(plaintext);
        request.ContentLength = plaintext.Length;
        request.ContentType = "application/json; charset=utf-8";
        return true;
    }

    /// <summary>Buffers whatever the rest of the pipeline writes, then sends it back wrapped.</summary>
    private async Task EncryptResponseAsync(HttpContext context, byte[] key)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var isJson = context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

        if (buffer.Length == 0 || !isJson)
        {
            // Empty (204), a redirect, or a non-JSON payload: pass it through untouched.
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
            return;
        }

        var sealedBody = Encoding.UTF8.GetBytes(
            $"{{\"enc\":\"{PayloadCrypto.Encrypt(buffer.ToArray(), key)}\"}}");

        context.Response.Headers[EncryptedHeader] = "1";
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = sealedBody.Length;
        await originalBody.WriteAsync(sealedBody, context.RequestAborted);
    }

    /// <summary>Pulls the <c>enc</c> string out of the envelope without materialising a DTO.
    /// Null when the body is not an envelope at all.</summary>
    private static string? ReadEnvelope(string raw)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("enc", out var value) &&
                   value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Sent in the clear: the client cannot decrypt anything at this point, and the
    /// message says nothing a caller does not already know.</summary>
    private static async Task WriteProblemAsync(HttpContext context, int status, string message, bool renew)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        if (renew) context.Response.Headers[RenewHeader] = "1";

        var body = ApiResponse<object>.Fail(message);
        body.TraceId = context.TraceIdentifier;
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(body,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }), context.RequestAborted);
    }

    private static string Redact(string sessionId) =>
        sessionId.Length <= 8 ? "…" : $"{sessionId[..8]}…";
}
