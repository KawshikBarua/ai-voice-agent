using System.Collections.Concurrent;
using System.Security.Cryptography;
using AiReceptionist.Api.Common;

namespace AiReceptionist.Api.Services;

public record PayloadSession(string SessionId, DateTime ExpiresUtc);

public interface IPayloadKeyRing
{
    /// <summary>Base64 SPKI of the public half — what the browser imports to wrap its AES key.</summary>
    string PublicKeySpki { get; }

    /// <summary>Changes whenever the key pair does (a restart), so a client holding a stale
    /// public key can notice without a failed round trip.</summary>
    string KeyId { get; }

    /// <summary>Unwraps a client-generated AES-256 key and registers a session for it.
    /// Null when the blob was not encrypted to this key pair.</summary>
    PayloadSession? OpenSession(string wrappedKeyBase64);

    /// <summary>The session's AES key, or null when it is unknown or has lapsed. Every hit
    /// slides the expiry forward, so an active tab never has to re-handshake.</summary>
    byte[]? Resolve(string sessionId);
}

/// <summary>
/// Holds the RSA key pair the browser wraps its per-session AES key with, plus the live sessions.
///
/// Both live in memory, so a restart invalidates every session and clients simply handshake again
/// (the client retries once on the renew signal). That also means this only works as-is for a
/// single instance or with sticky sessions — behind a load balancer the sessions need a shared
/// store, which is the same Redis scaffold point the rate limiter has.
/// </summary>
public class PayloadKeyRing : IPayloadKeyRing, IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly TimeSpan _lifetime;
    private readonly ILogger<PayloadKeyRing> _logger;
    private DateTime _lastPruneUtc = DateTime.UtcNow;

    private sealed class Entry
    {
        public required byte[] Key { get; init; }
        public DateTime ExpiresUtc { get; set; }
    }

    public PayloadKeyRing(IConfiguration configuration, ILogger<PayloadKeyRing> logger)
    {
        _logger = logger;
        var minutes = configuration.GetValue("Encryption:SessionMinutes", 120);
        _lifetime = TimeSpan.FromMinutes(Math.Clamp(minutes, 5, 24 * 60));

        PublicKeySpki = Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());
        // A hash of the public key, not a random id: two processes with the same key would
        // agree on it, and it never leaks anything the public key does not.
        KeyId = Convert.ToHexString(SHA256.HashData(_rsa.ExportSubjectPublicKeyInfo()).AsSpan(0, 8));
    }

    public string PublicKeySpki { get; }
    public string KeyId { get; }

    public PayloadSession? OpenSession(string wrappedKeyBase64)
    {
        byte[] key;
        try
        {
            key = _rsa.Decrypt(Convert.FromBase64String(wrappedKeyBase64), RSAEncryptionPadding.OaepSHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }

        if (key.Length != PayloadCrypto.KeyBytes)
        {
            _logger.LogWarning("Rejected a payload session: unwrapped key was {Length} bytes.", key.Length);
            return null;
        }

        Prune();
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expires = DateTime.UtcNow.Add(_lifetime);
        _sessions[sessionId] = new Entry { Key = key, ExpiresUtc = expires };
        return new PayloadSession(sessionId, expires);
    }

    public byte[]? Resolve(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var entry))
            return null;

        if (entry.ExpiresUtc <= DateTime.UtcNow)
        {
            _sessions.TryRemove(sessionId, out _);
            return null;
        }

        entry.ExpiresUtc = DateTime.UtcNow.Add(_lifetime);
        return entry.Key;
    }

    /// <summary>Swept on session creation rather than on a timer: the dictionary only grows when
    /// sessions are opened, so that is the only moment it can need trimming.</summary>
    private void Prune()
    {
        if (DateTime.UtcNow - _lastPruneUtc < TimeSpan.FromMinutes(5)) return;
        _lastPruneUtc = DateTime.UtcNow;

        foreach (var (id, entry) in _sessions)
            if (entry.ExpiresUtc <= DateTime.UtcNow)
                _sessions.TryRemove(id, out _);
    }

    public void Dispose() => _rsa.Dispose();
}
