using System.Text;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;

namespace AiReceptionist.Api.Services;

/// <summary>The stages of a call. Each is a Retell LLM state: its own prompt and its own tools.</summary>
public static class AgentStates
{
    public const string Triage = "triage";
    public const string Scheduling = "scheduling";
    public const string Booking = "booking";
    public const string ExistingBooking = "existing_booking";

    public static readonly string[] All = [Triage, Scheduling, Booking, ExistingBooking];
}

/// <summary>A tenant's prompt as Retell receives it: the general prompt, in force on every turn,
/// and one prompt per state, appended to it while the call is in that state.</summary>
public record AgentPrompt(string General, IReadOnlyDictionary<string, string> States)
{
    /// <summary>Everything as one text, for the read-only previews.</summary>
    public string Render() =>
        General + string.Concat(AgentStates.All.Select(s => $"\n\n==== State: {s} ====\n{States[s]}"));
}

/// <summary>
/// Splits tenant content into the places the agent can find it (SRS §13):
///
///  • <b>General prompt</b> — always in context: personality, rules, business identity, emergency
///    rules, and a short index of what can be looked up. Things the agent must obey on every turn.
///  • <b>State prompts</b> — only while the call is at that stage: finding out what they need,
///    finding a time, taking the booking, or dealing with one they already have.
///  • <b>lookup_info</b> — fetched when a caller asks: prices, services, products, opening hours
///    and closures, FAQs and policies (see <see cref="IKnowledgeLookupService"/>).
///
/// Everything in the first two is paid for on every turn of every call, and Retell scales the bill
/// once a turn's context passes 4,000 tokens — the conversation and tool replies included. So the
/// rule for this class is that a line earns its place by changing what the agent does; anything
/// the agent only needs when it is asked about goes behind the lookup instead.
///
/// The wording is platform-wide and editable in the super admin console (PromptTemplate); this
/// class fills it in with the tenant's own facts. Clients never edit the core instructions.
/// </summary>
public interface IPromptBuilderService
{
    Task<AgentPrompt> BuildAgentPromptAsync(int orgId);
}

public class PromptBuilderService : IPromptBuilderService
{
    /// <summary>How many names the index lists before it summarises the rest. The index is there to
    /// tell the agent what exists and what to call it, not to hold the content.</summary>
    private const int MaxIndexedServices = 30;
    private const int MaxIndexedTitles = 12;
    private const int MaxTitleLength = 60;

    private readonly ISettingsRepository _settings;
    private readonly ICatalogRepository _catalog;
    private readonly IKnowledgeRepository _knowledge;
    private readonly IPromptTemplateRepository _template;
    private readonly IEmployeeRepository _employees;
    private readonly IBusinessLocationRepository _locations;

    public PromptBuilderService(ISettingsRepository settings, ICatalogRepository catalog,
        IKnowledgeRepository knowledge, IPromptTemplateRepository template,
        IEmployeeRepository employees, IBusinessLocationRepository locations)
    {
        _settings = settings;
        _catalog = catalog;
        _knowledge = knowledge;
        _template = template;
        _employees = employees;
        _locations = locations;
    }

    public async Task<AgentPrompt> BuildAgentPromptAsync(int orgId)
    {
        var org = await _settings.GetOrganizationAsync(orgId)
                  ?? throw new InvalidOperationException("Organization not found.");
        var profile = IndustryTemplates.Resolve(org.Industry);
        var kb = (await _knowledge.ListAsync(orgId)).ToList();
        var services = (await _catalog.ListServicesAsync(orgId, 1, 500)).Items.ToList();

        // Style, rules and the wording of each state are platform-wide and edited in the super admin
        // console; everything else is this tenant's own data.
        var template = await _template.GetEffectiveAsync();

        var sb = new StringBuilder();
        // Tenant facts that only matter at one stage of the call are appended to that state's wording.
        var scheduling = new StringBuilder(profile.IsFieldService ? template.FieldSchedulingState : template.SchedulingState).AppendLine();
        var booking = new StringBuilder(template.BookingState).AppendLine();

        sb.AppendLine($"You are {profile.Role} for {org.Name}, a {org.Industry} business.");
        sb.AppendLine($"You answer the phone. Call the person on the other end the {profile.CustomerNoun}.");
        sb.AppendLine();
        sb.AppendLine(template.Persona);
        sb.AppendLine();
        sb.AppendLine(template.CoreRules);

        if (profile.ExtraRules.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Particular to this business");
            foreach (var rule in profile.ExtraRules) sb.AppendLine($"- {rule}");
        }

        sb.AppendLine();
        sb.AppendLine("## Business details");
        sb.AppendLine($"- Name: {org.Name}");
        if (!string.IsNullOrWhiteSpace(org.Address)) sb.AppendLine($"- Address: {org.Address}");
        // Labelled as ours so it can never be mistaken for the caller's. The transfer number is not
        // written here at all: the transfer tool already carries it, and as the only other number in
        // the prompt it was what the agent read out when a caller said "use the one I'm calling from".
        if (!string.IsNullOrWhiteSpace(org.Phone)) sb.AppendLine($"- This business's own phone (never the caller's): {org.Phone}");
        sb.AppendLine($"- Currency: {org.Currency}  |  Timezone: {org.Timezone}");
        if (profile.IsFieldService)
            sb.AppendLine("- This business travels to the customer: a full service address is mandatory for every booking.");

        // What can be looked up, and what it is called. The content itself is not written here —
        // lookup_info returns it when a caller asks — but the agent has to know a thing exists to
        // ask for it, and has to have the exact service names to book with. Names and titles are a
        // few tokens each; the prices, hours and answers behind them were most of the prompt.
        sb.AppendLine();
        sb.AppendLine("## Looking things up");
        sb.AppendLine("- Prices, hours, closures, FAQs and policies aren't written here: call lookup_info when asked and answer from it, never from memory. If it has nothing, say you'll get it checked.");
        sb.AppendLine("- Only offer times check_availability gives back.");
        var offered = services.Where(s => s.IsAvailable).Select(s => s.Name).ToList();
        if (offered.Count > 0)
            sb.AppendLine($"- Services (book with these exact names): {Index(offered, MaxIndexedServices)}.");
        if (org.ProductsEnabled)
            sb.AppendLine("- There is a product catalogue: look up any item a caller asks about.");
        var topics = new[] { ("FAQ", "FAQs"), ("Policy", "policies"), ("BusinessInfo", "about the business"), ("Custom", "other") }
            .Select(c => (Label: c.Item2, Titles: kb.Where(k => k.Category == c.Item1).Select(k => Short(k.Title)).ToList()))
            .Where(c => c.Titles.Count > 0)
            .Select(c => $"{c.Label} ({Index(c.Titles, MaxIndexedTitles)})")
            .ToList();
        if (topics.Count > 0)
            sb.AppendLine($"- Also on file: {string.Join("; ", topics)}.");

        // The size of the team is a fact about the business the agent should know — with three
        // stylists, "we can fit you in at two" can be true for two callers at once. Who is in on a
        // given day is not stated: it changes with days off and leave, and a prompt written at
        // sync time would be quietly wrong days later. check_availability is the live answer.
        var roster = await _employees.LoadRosterAsync(orgId, TodayLocal(org), TodayLocal(org));
        if (roster.Enabled)
        {
            sb.AppendLine();
            sb.AppendLine("## The team");
            sb.AppendLine($"- {roster.Employees.Count} people take appointments: " +
                          $"{string.Join(", ", roster.Employees.Select(e => e.Name))}. " +
                          (roster.Employees.Count > 1
                              ? $"Up to {roster.Employees.Count} appointments can run at once, so two callers wanting the same hour is not a clash on its own."
                              : "One appointment at a time."));
            sb.AppendLine("- Who is in changes day to day. Never work it out or tell a caller who is working: check_availability only offers times someone can cover, and a booking tells you who it is with — the only name you may pass on.");
        }

        var branches = await _locations.ListActiveAsync(orgId);

        // Whether an address is collected at all, which is not the same question as whether there is
        // a coverage area to check it against. A trade that travels takes an address on every job
        // even when the owner has never drawn a patch — so how to take one over the phone is gated
        // on this, and only the rules about what the check answers are gated on the branches.
        var takesAddress = profile.IsFieldService || branches.Count > 0;

        // Where the business will travel to. The facts are general — "do you come out to X?" can be
        // asked at any point — but the actual decision is check_service_area's, which measures the
        // distance rather than guessing at it from a list of place names.
        if (branches.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Where we cover");
            foreach (var b in branches)
            {
                sb.AppendLine(b.CoversEntireCity
                    ? $"- {b.Name}: anywhere in {b.PlaceName}."
                    : $"- {b.Name}: {b.PlaceName}, and about {b.CoverageRadiusMiles:0} " +
                      $"mile{(Math.Round(b.CoverageRadiusMiles) == 1 ? "" : "s")} around it.");
            }
            sb.AppendLine("- Never judge distance yourself; the address check decides.");
        }

        // The craft of getting an address out of someone on a phone call. Nothing here depends on a
        // coverage check — it is about how people actually speak — so it is emitted wherever an
        // address is taken, and worded without reference to a tool that may not be offered.
        if (takesAddress)
        {
            scheduling.AppendLine();
            scheduling.AppendLine("## Taking an address");
            scheduling.AppendLine("- Take it however they give it — a street and number is enough. Never ask for a postcode or ZIP, or make them repeat it.");
            scheduling.AppendLine("- If they stop halfway, leave a beat. A landmark or cross street is a real answer.");
            scheduling.AppendLine("- Same place as last time? Take their name, ask if they're on the number they're calling from, then call identify_customer and confirm the address it returns.");
            scheduling.AppendLine("- Read it back in their own words.");

            booking.AppendLine("- Gate codes, flat numbers, \"round the back\": put them in the address you book with. Never a reason to re-check it.");
        }

        // What the coverage check answers, and what to do with each verdict, comes back with the
        // check itself — so the prompt only has to say when to call it. Only for a business that has
        // drawn a patch, because only then is check_service_area offered at all.
        if (branches.Count > 0)
        {
            scheduling.AppendLine();
            scheduling.AppendLine("## Checking the address");
            scheduling.AppendLine("- The moment they give an address, call check_service_area with it, before agreeing a time, and do what its answer says. Ask for the town only if it asks.");
        }

        // Emergency rules are safety-critical: they stay in the general prompt, never a state or the
        // lookup, so they hold at every stage of the call.
        var emergencies = kb.Where(k => k.Category == "EmergencyRule").ToList();
        if (emergencies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Emergency rules (act on these immediately)");
            foreach (var e in emergencies) sb.AppendLine($"- {e.Title}: {e.Content}");
        }

        // Same-day handling for the services this tenant has flagged as emergency work. Recognising
        // one is general; how to find it a slot happens while finding a time. It sits after the
        // emergency rules deliberately: those are safety instructions and have to win wherever the
        // two ever point in different directions.
        var urgent = services.Where(s => s.IsAvailable && s.IsEmergency).ToList();
        if (urgent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Urgent work (usually seen the same day): {string.Join(", ", urgent.Select(s => s.Name))}");
            sb.AppendLine("- When a caller needs one, say once, in your own words, that it sounds like a horrible thing to deal with and you'll get someone out — then get on with helping.");
            if (emergencies.Count > 0)
                sb.AppendLine("- The emergency rules above come first. Where one says to hang up, call emergency services or leave the property, deal with their safety and do not book around it.");

            scheduling.AppendLine();
            scheduling.AppendLine("## Urgent work");
            scheduling.AppendLine("- Check \"today\" first. If it has a slot, lead with it; if not, say so and offer the earliest. Never promise today without a slot.");
        }

        // Last on purpose. This prompt is built when the tenant syncs, not per call, so a date written
        // in here would be wrong by tomorrow. {{current_time_<iana>}} is Retell's own placeholder: it
        // is filled in as each call starts, in this tenant's timezone — every organization gets its
        // own, and the inbound webhook overwrites it with our value so a Retell-side fault cannot
        // leave the agent guessing. Being the only text that changes from call to call, it goes after
        // everything that does not, so the model provider can reuse its cached copy of the rest of the
        // prompt across calls. The rules below it matter as much as the value: left to itself the
        // model dates everything from its training cutoff, which is how "tomorrow" lands in the wrong year.
        var clock = "{{current_time_" + TenantTime.IanaId(org.Timezone) + "}}";
        sb.AppendLine();
        sb.AppendLine("## Right now");
        sb.AppendLine($"- It is {clock} ({org.Timezone}); every date and time you say is in that timezone. Trust this, never your own sense of the date.");
        sb.AppendLine("- Never work out dates yourself: give the tools the caller's words (\"tomorrow\", \"next Friday\") and use the date they return.");

        return new AgentPrompt(sb.ToString(), new Dictionary<string, string>
        {
            [AgentStates.Triage] = template.TriageState,
            [AgentStates.Scheduling] = scheduling.ToString(),
            [AgentStates.Booking] = booking.ToString(),
            [AgentStates.ExistingBooking] = template.ExistingBookingState,
        });
    }

    private static DateTime TodayLocal(Domain.Organization org) =>
        TenantTime.NowLocal(TenantTime.Resolve(org.Timezone)).Date;

    /// <summary>"A, B, C" — or "A, B, C and 14 more" past the limit.</summary>
    private static string Index(List<string> names, int max) =>
        names.Count <= max
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(max))} and {names.Count - max} more";

    private static string Short(string title)
    {
        var t = title.Trim();
        return t.Length <= MaxTitleLength ? t : t[..(MaxTitleLength - 1)].TrimEnd() + "…";
    }
}
