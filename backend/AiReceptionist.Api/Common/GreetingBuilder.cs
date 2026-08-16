namespace AiReceptionist.Api.Common;

/// <summary>Builds the system-generated agent greeting. Tenants do not author this — it is
/// derived from the organization's name so every AI introduces the business by name.
/// Shared by the settings repository (what gets stored) and the Retell sync (what gets
/// pushed as the agent's begin_message) so the two can never drift apart.
///
/// It is the first thing a caller hears, so it is written the way a receptionist actually
/// answers a phone: the business name once, the AI disclosure folded into the same breath
/// rather than announced as a second sentence, and a short open question.</summary>
public static class GreetingBuilder
{
    public static string For(string? organizationName)
    {
        var business = string.IsNullOrWhiteSpace(organizationName) ? "us" : organizationName.Trim();
        return $"Hi, thanks for calling {business} — you're through to our AI assistant. How can I help?";
    }
}
