using System.Security.Cryptography;
using System.Text;

namespace AiReceptionist.Api.Common;

/// <summary>Hashes refresh tokens for storage. Refresh tokens are bearer credentials with a
/// multi-day lifetime — storing them verbatim means a single leaked database backup (or any
/// read-only SQL injection) hands over every live session. We store only the hash and compare
/// hashes on presentation, so the stored value is useless to an attacker.
///
/// Plain SHA-256 (no salt/work factor) is correct here, unlike for passwords: the token is 64
/// bytes of cryptographic randomness, so it is not brute-forceable or rainbow-table-able.</summary>
public static class TokenHash
{
    public static string Compute(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
