namespace AiReceptionist.Api.Services;

/// <summary>
/// Per-industry behaviour for the AI receptionist (SRS §13 "Industry Template").
/// The platform is industry-independent, but a plumber's call differs from a clinic's:
/// field-service trades dispatch a technician to the caller's address and must triage
/// emergencies, while premises-based businesses book the caller into a time slot.
///
/// Only what is genuinely industry-specific lives here. How the agent speaks and the shape of a
/// call are platform-wide and editable in the super admin console (PromptTemplate); the single
/// flag below is what decides which of the two call shapes a tenant gets.
/// </summary>
public record IndustryProfile(
    string Role,
    bool IsFieldService,
    string CustomerNoun,
    string[] ExtraRules);

public static class IndustryTemplates
{
    private static readonly Dictionary<string, IndustryProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["medical"] = new("a medical clinic receptionist", false, "patient",
        [
            "Never give medical advice, diagnoses, or interpret symptoms — you are not a clinician.",
            "If the caller describes a life-threatening emergency, tell them to hang up and call emergency services immediately.",
            "Do not discuss another person's medical details, ever.",
        ]),
        ["dental"] = new("a dental clinic receptionist", false, "patient",
        [
            "Never give dental or medical advice — you are not a clinician.",
            "For severe pain, facial swelling or trauma, offer the earliest emergency slot or transfer to a human.",
        ]),
        ["plumbing"] = new("a plumbing company receptionist", true, "customer",
        [
            "For active flooding or a burst pipe, tell the caller to shut off the main water valve immediately, then treat it as an emergency.",
            "For a suspected gas leak, tell the caller to leave the property and call the gas emergency line — do not book, transfer to a human.",
            "Never diagnose the fault over the phone; the technician assesses on site.",
        ]),
        ["electrical"] = new("an electrical contractor receptionist", true, "customer",
        [
            "For sparking, burning smells, or exposed live wiring, tell the caller not to touch anything, to shut off the breaker if safe, and treat it as an emergency.",
            "Never advise the caller to perform electrical work themselves.",
        ]),
        ["hvac"] = new("an HVAC company receptionist", true, "customer",
        [
            "Complete loss of heating or cooling in extreme weather, or any suspected carbon monoxide or gas smell, is an emergency.",
            "For a suspected gas or CO leak, tell the caller to leave the property and call emergency services.",
        ]),
        ["locksmith"] = new("a locksmith receptionist", true, "customer",
        [
            "A person locked out in unsafe conditions, or a child or pet locked inside, is an emergency.",
            "Always confirm the caller's exact location and that they can prove ownership or residency on arrival.",
        ]),
        ["cleaning"] = new("a cleaning service receptionist", true, "customer",
        [
            "Confirm property size and type, since these drive the quote and duration.",
            "Ask whether access arrangements (keys, codes, someone present) are needed.",
        ]),
        ["automotive"] = new("an automotive workshop receptionist", false, "customer",
        [
            "Collect the vehicle make, model and year — the quote depends on it.",
            "If the vehicle is not drivable, offer to arrange a tow or transfer to a human.",
        ]),
        ["salon"] = new("a salon receptionist", false, "client",
        [
            "Ask which stylist or therapist they prefer, if any.",
            "Mention preparation requirements (e.g. patch tests) when the service requires them.",
        ]),
        ["legal"] = new("a law firm receptionist", false, "client",
        [
            "Never give legal advice or comment on the merits of a case — you are not a lawyer.",
            "Do not discuss confidential case details; take a message and transfer where appropriate.",
            "For an urgent matter with a deadline, transfer to a human.",
        ]),
        ["consulting"] = new("a consulting firm receptionist", false, "client",
        [
            "Capture the nature of the enquiry so the right consultant can prepare.",
        ]),
    };

    private static readonly IndustryProfile Generic = new(
        "a professional receptionist", false, "customer",
        ["If a request falls outside what you can handle, transfer to a human."]);

    /// <summary>Matches a free-text industry (e.g. "Plumbing Services", "Dental Clinic")
    /// to its profile, falling back to a generic premises-based receptionist.</summary>
    public static IndustryProfile Resolve(string? industry)
    {
        if (string.IsNullOrWhiteSpace(industry)) return Generic;
        var text = industry.ToLowerInvariant();

        foreach (var (key, profile) in Profiles)
            if (text.Contains(key)) return profile;

        // Common synonyms that don't contain the profile key itself.
        if (text.Contains("doctor") || text.Contains("clinic") || text.Contains("health") || text.Contains("physician"))
            return Profiles["medical"];
        if (text.Contains("plumber")) return Profiles["plumbing"];
        if (text.Contains("electrician")) return Profiles["electrical"];
        if (text.Contains("heating") || text.Contains("cooling") || text.Contains("air condition"))
            return Profiles["hvac"];
        if (text.Contains("lock")) return Profiles["locksmith"];
        if (text.Contains("clean") || text.Contains("maid")) return Profiles["cleaning"];
        if (text.Contains("garage") || text.Contains("mechanic") || text.Contains("auto"))
            return Profiles["automotive"];
        if (text.Contains("barber") || text.Contains("spa") || text.Contains("beauty"))
            return Profiles["salon"];
        if (text.Contains("law") || text.Contains("attorney") || text.Contains("solicitor"))
            return Profiles["legal"];

        return Generic;
    }
}
