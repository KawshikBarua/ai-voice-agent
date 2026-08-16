using System.Security.Cryptography;
using System.Text;

namespace AiReceptionist.Api.Common;

/// <summary>
/// Authentication for inbound Retell traffic.
///
/// Two different mechanisms, because Retell treats them differently:
///  • Webhooks (call_started/ended/analyzed) are signed with X-Retell-Signature —
///    HMAC-SHA256 of the raw body keyed with the API key.
///  • Custom function ("tool") calls made during a live call are NOT signed. They are
///    plain POSTs to whatever URL we registered, so the URL itself has to carry the
///    secret: we append ?k=&lt;token&gt;, where the token is derived from the API key and
///    the organization id. It is unguessable without the API key and needs no storage.
/// </summary>
public static class RetellSignature
{
    public static bool Verify(string rawBody, string apiKey, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));
        return FixedEquals(hash.ToLowerInvariant(), signature.Trim().ToLowerInvariant());
    }

    /// <summary>Webhook acceptance: valid signature, or verification deliberately disabled / no
    /// key configured. The key and verify flag come from the centralized platform connection
    /// (see IRetellConnectionRepository).
    ///
    /// <paramref name="allowUnverified"/> must be false outside development: an unconfigured or
    /// verification-disabled deployment would otherwise accept forged call events from anyone
    /// on the internet, letting them write call logs and customer timeline entries at will.</summary>
    public static bool Accept(string? apiKey, bool verify, string rawBody, string? signature,
        bool allowUnverified = false)
    {
        if (!verify || string.IsNullOrWhiteSpace(apiKey)) return allowUnverified;
        return Verify(rawBody, apiKey, signature);
    }

    /// <summary>Deterministic per-organization secret embedded in registered tool URLs.</summary>
    public static string ToolToken(string apiKey, int orgId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"tools:{orgId}"));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    /// <summary>Tool-call acceptance: a valid ?k= token, or a valid signature if Retell
    /// ever starts sending one. Open only when verification is off / no key configured.
    /// The key and verify flag come from the centralized platform connection.</summary>
    public static bool AcceptToolCall(string? apiKey, bool verify, int orgId, string rawBody,
        string? signature, string? token, bool allowUnverified = false)
    {
        // Fail closed outside development: these endpoints read customer records and
        // book/cancel appointments for the org named in the URL.
        if (!verify || string.IsNullOrWhiteSpace(apiKey)) return allowUnverified;

        if (!string.IsNullOrWhiteSpace(token) && FixedEquals(ToolToken(apiKey, orgId), token.Trim()))
            return true;

        return Verify(rawBody, apiKey, signature);
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
