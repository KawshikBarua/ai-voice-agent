using System.Security.Cryptography;

namespace AiReceptionist.Api.Common;

/// <summary>
/// AES-256-GCM for the request/response payload envelope.
///
/// Wire format is one base64 string: <c>nonce(12) || ciphertext || tag(16)</c>. That is exactly
/// what WebCrypto's <c>SubtleCrypto.encrypt({name:'AES-GCM'})</c> produces once the nonce is
/// prepended, so the browser and the API agree without either side reordering bytes.
/// </summary>
public static class PayloadCrypto
{
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int KeyBytes = 32;

    public static string Encrypt(ReadOnlySpan<byte> plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var packed = new byte[NonceBytes + ciphertext.Length + TagBytes];
        nonce.CopyTo(packed, 0);
        ciphertext.CopyTo(packed, NonceBytes);
        tag.CopyTo(packed, NonceBytes + ciphertext.Length);
        return Convert.ToBase64String(packed);
    }

    /// <summary>Returns false rather than throwing on anything malformed — a bad envelope is a
    /// client error to answer with 400, not a 500 to log.</summary>
    public static bool TryDecrypt(string base64, byte[] key, out byte[] plaintext)
    {
        plaintext = [];
        if (string.IsNullOrWhiteSpace(base64)) return false;

        byte[] packed;
        try { packed = Convert.FromBase64String(base64); }
        catch (FormatException) { return false; }

        if (packed.Length < NonceBytes + TagBytes) return false;

        var nonce = packed.AsSpan(0, NonceBytes);
        var cipherLength = packed.Length - NonceBytes - TagBytes;
        var ciphertext = packed.AsSpan(NonceBytes, cipherLength);
        var tag = packed.AsSpan(NonceBytes + cipherLength, TagBytes);
        var output = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, output);
        }
        catch (CryptographicException)
        {
            // Wrong key or tampered payload. Indistinguishable on purpose.
            return false;
        }

        plaintext = output;
        return true;
    }
}
