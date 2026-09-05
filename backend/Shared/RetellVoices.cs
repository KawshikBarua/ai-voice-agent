namespace AiReceptionist.Shared;

/// <summary>One voice the platform offers, as Retell identifies it.</summary>
public record RetellVoice(string Id, string Name, string Gender)
{
    /// <summary>"Grace (female)" — how the voice is listed to an operator or a tenant.</summary>
    public string Label => $"{Name} ({Gender})";
}

/// <summary>
/// The four voices this platform offers. Every one of them is a Retell <b>platform</b> voice
/// (provider "platform", ids prefixed <c>retell-</c>), and that is the point rather than a
/// coincidence: <c>enable_expressive_mode</c> only applies to platform voices, so confining the
/// choice to this set is what lets expressive mode be on for every tenant. A voice from any other
/// provider would silently lose it.
///
/// Compiled into both server applications so the super admin console and the API can never offer
/// different voices. The tenant-facing list in the web app (frontend/src/pages/Settings.jsx)
/// mirrors it and must be changed alongside.
/// </summary>
public static class RetellVoices
{
    public static readonly RetellVoice Grace = new("retell-Grace", "Grace", "female");
    public static readonly RetellVoice Ashley = new("retell-Ashley", "Ashley", "female");
    public static readonly RetellVoice Chloe = new("retell-Chloe", "Chloe", "female");
    public static readonly RetellVoice Nico = new("retell-Nico", "Nico", "male");

    public static readonly IReadOnlyList<RetellVoice> All = [Grace, Ashley, Chloe, Nico];

    /// <summary>The voice a tenant gets when nothing else has been chosen for them.</summary>
    public static readonly RetellVoice Default = Grace;

    /// <summary>
    /// Settles on one of the four for whatever is stored against a tenant.
    ///
    /// Matching is by name, so the provider-prefixed ids kept before this list existed
    /// ("11labs-Grace", "openai-Chloe") land on the platform voice of the same name and the
    /// tenant keeps the voice their callers know. Bare legacy names ("nova", "alloy") and
    /// anything else no longer on offer fall back to <paramref name="fallbackId"/> — the
    /// operator's default voice — because a voice outside this set cannot be expressive.
    /// </summary>
    public static string Resolve(string? voice, string? fallbackId = null)
    {
        var text = voice?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var match = All.FirstOrDefault(v =>
                text.Equals(v.Id, StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith($"-{v.Name}", StringComparison.OrdinalIgnoreCase) ||
                text.Equals(v.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Id;
        }

        // The operator's default only counts if it is itself one of ours.
        var fallback = All.FirstOrDefault(v => v.Id.Equals(fallbackId?.Trim(), StringComparison.OrdinalIgnoreCase));
        return (fallback ?? Default).Id;
    }
}
