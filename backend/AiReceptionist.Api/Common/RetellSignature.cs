using System.Security.Cryptography;
using System.Text;

namespace AiReceptionist.Api.Common;

/// <summary>
/// Authentication for inbound Retell traffic.
///
/// Two different mechanisms, because Retell treats them differently:
///  • Webhooks (call_started/ended/analyzed) are signed with X-Retell-Signature —
///    HMAC-SHA256 keyed with the API key, over the raw body with the send timestamp
///    appended. See <see cref="Verify"/> for the header format.
///  • Custom function ("tool") calls made during a live call are NOT signed. They are
///    plain POSTs to whatever URL we registered, so the URL itself has to carry the
///    secret: we append ?k=&lt;token&gt;, where the token is derived from the API key and
///    the organization id. It is unguessable without the API key and needs no storage.
/// </summary>
public static class RetellSignature
{
    /// <summary>Checks an X-Retell-Signature header against the body it was sent with.
    ///
    /// Retell's header is not a bare digest. It is a pair:
    /// <c>v=&lt;unix ms&gt;,d=&lt;hex digest&gt;</c>, and the digest covers the raw body with that
    /// same timestamp appended — <c>HMAC-SHA256(apiKey, rawBody + timestamp)</c>. Comparing the
    /// digest of the body alone against the whole header string, which is what this used to do,
    /// cannot ever match: every genuine delivery was rejected as a forgery, and because a webhook
    /// is fire-and-forget, every call it carried was simply lost.
    ///
    /// A header that carries no <c>v=</c>/<c>d=</c> pair is treated as a bare hex digest of the
    /// body, which is the older form and what a hand-rolled test request will send.
    ///
    /// The timestamp is not held to a freshness window. Retell's own verifier does not enforce
    /// one, a replayed call event is turned away by the unique index on the Retell call id
    /// anyway, and a clock a few minutes out would otherwise reproduce exactly the silent outage
    /// this method is here to end.</summary>
    public static bool Verify(string rawBody, string apiKey, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;

        var (timestamp, digest) = ParseSignature(signature);
        if (digest is null) return false;

        // The timestamp is ASCII digits, so appending it to the string and encoding once gives the
        // same bytes as appending it to the encoded body.
        var signed = timestamp is null ? rawBody : rawBody + timestamp;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed)));
        return FixedEquals(hash.ToLowerInvariant(), digest);
    }

    /// <summary>Splits "v=&lt;ts&gt;,d=&lt;hex&gt;" into its parts, lowercased. Returns a null
    /// timestamp for the bare-digest form, and a null digest when the header is neither.</summary>
    private static (string? Timestamp, string? Digest) ParseSignature(string signature)
    {
        string? timestamp = null, digest = null;

        foreach (var part in signature.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var span = part.Trim();
            if (span.StartsWith("v=", StringComparison.OrdinalIgnoreCase))
                timestamp = span[2..].Trim();
            else if (span.StartsWith("d=", StringComparison.OrdinalIgnoreCase))
                digest = span[2..].Trim().ToLowerInvariant();
        }

        if (digest is not null) return (timestamp, digest);

        // No pair: the whole header is the digest, and nothing was appended to the body.
        var bare = signature.Trim().ToLowerInvariant();
        return (null, bare.Length == 0 ? null : bare);
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
