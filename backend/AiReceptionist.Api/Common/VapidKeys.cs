using System.Security.Cryptography;

namespace AiReceptionist.Api.Common;

/// <summary>
/// The one credential Web Push needs, and the reason the whole feature costs nothing to run.
///
/// A VAPID key pair is a plain P-256 key pair this deployment generates for itself. The public
/// half is handed to browsers when they subscribe; the private half signs the request that asks a
/// push service (Google's, Mozilla's, Apple's) to deliver a message. There is no account, no
/// registration and no per-message charge anywhere in that chain — the signature is what proves
/// the sender is the same party the browser subscribed to.
///
/// Losing the private key invalidates every existing subscription, because a browser only accepts
/// deliveries signed by the key it subscribed against. So it is generated once, kept out of the
/// repository like any other secret, and never rotated casually.
/// </summary>
public static class VapidKeys
{
    /// <summary>Generates a pair, base64url-encoded the way the Web Push spec and the browser
    /// APIs expect. Run once per deployment; see the <c>vapid</c> command in Program.cs.</summary>
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(includePrivateParameters: true);

        // The public key goes on the wire as an uncompressed EC point: 0x04, then X, then Y.
        // Browsers reject anything else, including the DER/SPKI form .NET exports by default.
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        p.Q.X!.CopyTo(publicKey, 1);
        p.Q.Y!.CopyTo(publicKey, 33);

        return (Base64Url(publicKey), Base64Url(p.D!));
    }

    /// <summary>Whether a configured public key could actually be used by a browser.
    /// <c>applicationServerKey</c> is rejected outright if it is not a 65-byte uncompressed
    /// point, and the failure surfaces in the browser rather than here — so it is worth catching
    /// at boot, where the message can say what is wrong.</summary>
    public static bool IsValidPublicKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var bytes = FromBase64Url(value);
            return bytes.Length == 65 && bytes[0] == 0x04;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
