using AiReceptionist.Api.Common;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

/// <summary>
/// Handshake for payload encryption. Deliberately anonymous and never encrypted itself — it is
/// what a client has to reach before it can encrypt anything, including the sign-in request.
/// </summary>
[ApiController]
[Route("api/v1/crypto")]
[AllowAnonymous]
public class CryptoController : ControllerBase
{
    private readonly IPayloadKeyRing _keyRing;
    private readonly ILogger<CryptoController> _logger;

    public CryptoController(IPayloadKeyRing keyRing, ILogger<CryptoController> logger)
    {
        _keyRing = keyRing;
        _logger = logger;
    }

    public record HandshakeResponse(string KeyId, string PublicKey, string Algorithm);

    public record SessionRequest(string WrappedKey);

    public record SessionResponse(string SessionId, DateTime ExpiresAt);

    /// <summary>The public half the client wraps its AES key with.</summary>
    [HttpGet("handshake")]
    public IActionResult Handshake() =>
        Ok(ApiResponse<HandshakeResponse>.Ok(
            new HandshakeResponse(_keyRing.KeyId, _keyRing.PublicKeySpki, "RSA-OAEP-256/AES-256-GCM")));

    /// <summary>Registers a client-generated AES-256 key, wrapped with the public key above.</summary>
    [HttpPost("session")]
    public IActionResult OpenSession([FromBody] SessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.WrappedKey))
            return BadRequest(ApiResponse<object>.Fail("A wrapped key is required."));

        var session = _keyRing.OpenSession(request.WrappedKey);
        if (session is null)
        {
            _logger.LogWarning("Payload key exchange failed for {Ip} — the wrapped key did not " +
                "unwrap with the current key pair.", HttpContext.Connection.RemoteIpAddress);
            return BadRequest(ApiResponse<object>.Fail(
                "The wrapped key could not be read. Fetch the handshake key again and retry."));
        }

        return Ok(ApiResponse<SessionResponse>.Ok(
            new SessionResponse(session.SessionId, session.ExpiresUtc)));
    }
}
