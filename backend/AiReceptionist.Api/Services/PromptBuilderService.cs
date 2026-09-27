using System.Text;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;

namespace AiReceptionist.Api.Services;

/// <summary>A single document uploaded to the Retell knowledge base.</summary>
public record KnowledgeDocument(string Title, string Text);

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
/// Splits tenant content into the places Retell can hold it (SRS §13):
///
///  • <b>General prompt</b> — always in context: personality, rules, business identity, hours and
///    emergency rules. Things the agent must obey on every turn.
///  • <b>State prompts</b> — only while the call is at that stage: finding out what they need,
///    finding a time, taking the booking, or dealing with one they already have.
///  • <b>Knowledge base</b> — retrieved on demand: product/service catalogues, FAQs,
///    policies and long reference text. Bulky, frequently changing content that would
///    otherwise bloat the prompt (and cost tokens on every single turn).
///
/// The wording is platform-wide and editable in the super admin console (PromptTemplate); this
/// class fills it in with the tenant's own facts. Clients never edit the core instructions.
/// </summary>
public interface IPromptBuilderService
{
    Task<AgentPrompt> BuildAgentPromptAsync(int orgId);
    Task<List<KnowledgeDocument>> BuildKnowledgeDocumentsAsync(int orgId);
}

public class PromptBuilderService : IPromptBuilderService
{
    private readonly ISettingsRepository _settings;
    private readonly ICatalogRepository _catalog;
    private readonly IKnowledgeRepository _knowledge;
    private readonly IHolidayRepository _holidays;
    private readonly IPromptTemplateRepository _template;
    private readonly IEmployeeRepository _employees;
    private readonly IBusinessLocationRepository _locations;

    public PromptBuilderService(ISettingsRepository settings, ICatalogRepository catalog,
        IKnowledgeRepository knowledge, IHolidayRepository holidays, IPromptTemplateRepository template,
        IEmployeeRepository employees, IBusinessLocationRepository locations)
    {
        _settings = settings;
        _catalog = catalog;
        _knowledge = knowledge;
        _holidays = holidays;
        _template = template;
        _employees = employees;
        _locations = locations;
    }

    public async Task<AgentPrompt> BuildAgentPromptAsync(int orgId)
    {
        var org = await _settings.GetOrganizationAsync(orgId)
                  ?? throw new InvalidOperationException("Organization not found.");
        var agent = await _settings.GetAgentConfigAsync(orgId);
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
        if (!string.IsNullOrWhiteSpace(org.Phone)) sb.AppendLine($"- Phone: {org.Phone}");
        sb.AppendLine($"- Currency: {org.Currency}  |  Timezone: {org.Timezone}");
        if (profile.IsFieldService)
            sb.AppendLine("- This business travels to the customer: a full service address is mandatory for every booking.");
        if (!string.IsNullOrWhiteSpace(agent?.TransferNumber))
            sb.AppendLine($"- Human transfer number: {agent.TransferNumber}");

        // Always emitted, even with no hours configured: BusinessHours falls back to 09:00–17:00
        // and the booking tools enforce that fallback, so staying silent here would leave the
        // agent guessing at hours it is actually being held to.
        sb.AppendLine();
        sb.AppendLine($"## Business hours (local time, {org.Timezone})");
        foreach (var line in BusinessHours.Describe(org.BusinessHoursJson))
            sb.AppendLine($"- {line}");

        // The size of the team is a fact about the business the agent should know — with three
        // stylists, "we can fit you in at two" can be true for two callers at once. Who is in on a
        // given day is not stated: it changes with days off and leave, and a prompt written at
        // sync time would be quietly wrong days later. check_availability is the live answer.
        var roster = await _employees.LoadRosterAsync(orgId, TodayLocal(org), TodayLocal(org));
        if (roster.Enabled)
        {
            sb.AppendLine();
            sb.AppendLine("## The team");
            sb.AppendLine($"- {roster.Employees.Count} people take appointments here: " +
                          $"{string.Join(", ", roster.Employees.Select(e => e.Name))}.");
            sb.AppendLine(roster.Employees.Count > 1
                ? $"- Up to {roster.Employees.Count} appointments can run at the same time, one with each of them. " +
                  "Two callers wanting the same hour is not a clash on its own."
                : "- One appointment at a time: there is a single person taking them.");
            sb.AppendLine("- Who is actually in changes day to day — days off, holiday, sick leave. Never work " +
                          "that out yourself and never tell a caller who is working. check_availability counts " +
                          "only the people genuinely on duty, so any time it gives back is one somebody can cover, " +
                          "and a day it says no one is working is a day with nothing to offer.");
            sb.AppendLine("- Do not promise a particular person. When a booking succeeds it tells you who the " +
                          "appointment is with, and that is the only name you may pass on.");
        }

        var branches = await _locations.ListActiveAsync(orgId);

        // Whether an address is collected at all, which is not the same question as whether there is
        // a coverage area to check it against. A trade that travels takes an address on every job
        // even when the owner has never drawn a patch — so how to take one over the phone is gated
        // on this, and only the rules about what the check answers are gated on the branches.
        var takesAddress = profile.IsFieldService || branches.Count > 0;

        // Where the business will travel to. The facts are general — "do you come out to X?" can be
        // asked at any point — but the actual decision is check_service_area's, which measures the
        // distance rather than guessing at it from a list of place names, and that belongs to the
        // state where the address is collected.
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
            sb.AppendLine("- Never judge for yourself whether somewhere is close enough. Distances are not something to estimate from a place name; the address is checked when it is taken.");
        }

        // The craft of getting an address out of someone on a phone call. Nothing here depends on a
        // coverage check — it is about how people actually speak — so it is emitted wherever an
        // address is taken, and worded without reference to a tool that may not be offered.
        if (takesAddress)
        {
            scheduling.AppendLine();
            scheduling.AppendLine("## Taking an address over the phone");
            scheduling.AppendLine("Nobody dictates an address neatly. Take it the way they give it.");
            scheduling.AppendLine("- Take whatever they gave you, even just a street and a number. They rang a local firm and assume you know the town. Do not hold the address back waiting for a town, and do not make them repeat it in a tidier order.");
            scheduling.AppendLine("- Never ask for a postcode or ZIP. People blank on it under stress even when they know it, and nothing here needs it.");
            scheduling.AppendLine("- They often stop halfway. Leave a beat before you speak: the rest of it, or the town, usually follows on its own. Prompt only if the silence holds.");
            scheduling.AppendLine("- A landmark or a cross street is a real answer — \"the unit behind the builders' merchant\", \"near the petrol station on the main road\". Take it as they said it. If you still need more, ask what is on the sign outside or what the nearest street is, not for a house number they may not have.");
            scheduling.AppendLine("- If they say it is the same place as last time, call identify_customer and use the address it gives back. Read it back once to confirm; do not make them dictate it again.");
            scheduling.AppendLine("- Read an address back in their own words. Reciting \"1220 Grand\" as \"1220 Grand Avenue, Kansas City, Jackson County, Missouri\" sounds like a machine and invites a correction to something that was already right.");

            booking.AppendLine("- Gate codes, buzzer numbers, flat and suite numbers, \"come round the back\" — take these whenever they come up and put them in the address you send to book_appointment. Never treat one as a reason to re-check the address or as a missing part of it.");
        }

        // What the coverage check answers, and what to do with each verdict. Only for a business that
        // has drawn a patch, because only then is check_service_area offered at all.
        if (branches.Count > 0)
        {
            scheduling.AppendLine();
            scheduling.AppendLine("## Checking the address");
            scheduling.AppendLine("- The moment a caller gives you an address, call check_service_area with it — before you agree a time.");
            scheduling.AppendLine("- Send it even when it is only a street and a number. The check searches this business's own area first, so a bare street usually resolves; it does not need a town from you.");
            scheduling.AppendLine("- Ask for the town only when the check comes back asking for it, and then ask for the town alone — not the whole address again.");
            scheduling.AppendLine("- If it names a landmark beside the address, use it to check you heard the street right — \"just by the old town hall?\" — rather than reading the address back word for word.");
            scheduling.AppendLine("- The tidied-up address it returns is for planning the route, not for reading out. Confirm in the caller's own words.");
            scheduling.AppendLine("- If the address cannot be found, go through it with them once more: the street, the number and the town. If it still cannot be found, carry on anyway. It is flagged for a colleague to confirm, so tell the caller someone will ring to confirm the address before the visit.");
            scheduling.AppendLine("- If it is outside the area but in a town covered, carry on and say honestly that it is outside the usual patch, so someone will ring back to confirm. Do not tell them it is certain.");
            scheduling.AppendLine("- If it is somewhere not covered at all, do not book. Apologise, say plainly that it is outside the area served, and suggest they find someone local. Be kind about it and do not leave them hoping.");
        }

        var closures = (await _holidays.ListUpcomingAsync(orgId, TodayLocal(org))).ToList();
        sb.AppendLine();
        sb.AppendLine("## Holiday closures");
        if (closures.Count == 0)
        {
            sb.AppendLine("- No closures are scheduled. Normal business hours apply.");
        }
        else
        {
            sb.AppendLine("The business is CLOSED all day on these dates, whatever the weekly hours say.");
            sb.AppendLine("Never offer or book a time on them. Say the business is closed for the");
            sb.AppendLine("occasion and offer the next working day instead.");
            foreach (var c in closures)
                sb.AppendLine($"- {c.Date:yyyy-MM-dd} ({c.Date:dddd}) — {c.Name}");
        }

        // Emergency rules are safety-critical: they stay in the general prompt, never a state or the
        // knowledge base, so they hold at every stage of the call.
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
            sb.AppendLine("## Urgent work (can usually be seen the same day)");
            foreach (var s in urgent) sb.AppendLine($"- {s.Name}");
            sb.AppendLine("When a caller needs one of these, take the trouble seriously before you take the details. One short line in your own words — \"that sounds like a horrible thing to be dealing with, let's get someone out to you\" — and then get on with helping. Say it once: sympathy repeated turns into padding, and what they actually want is someone at the door.");
            if (emergencies.Count > 0)
                sb.AppendLine("The emergency rules above still come first. Where one of them says to hang up, call emergency services or leave the property, that is the answer — deal with their safety and do not book around it.");

            scheduling.AppendLine();
            scheduling.AppendLine("## Urgent work");
            scheduling.AppendLine("- Check today before you ask which day suits. Give check_availability the word \"today\" and let it tell you what is left.");
            scheduling.AppendLine("- If today has something, lead with it — \"I can get someone to you this afternoon, about three\" — and offer another day only if they would rather.");
            scheduling.AppendLine("- If today has nothing, say so plainly and offer the earliest you do have. Do not talk around it, and do not leave them hoping.");
            scheduling.AppendLine("- Never say today is possible until check_availability has shown you a slot on it. A same-day promise you cannot keep is worse than the honest answer.");
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
        sb.AppendLine($"- The current local date and time is {clock}.");
        sb.AppendLine($"- That is {org.Timezone}, the timezone this business works in. Every date and time you say is in it.");
        sb.AppendLine("- That line is filled in fresh at the start of every call, so it is right. Your own sense of the date is not: never state or work out today's date, the day of the week, or the year from memory.");
        sb.AppendLine("- Do not do date arithmetic yourself. Give check_availability what the caller actually said — \"tomorrow\", \"next Friday\", \"the 3rd\" — and use the date it hands back.");
        sb.AppendLine("- Every tool reply carries the current date and time as well. Trust it over anything you worked out earlier in the call.");

        return new AgentPrompt(sb.ToString(), new Dictionary<string, string>
        {
            [AgentStates.Triage] = template.TriageState,
            [AgentStates.Scheduling] = scheduling.ToString(),
            [AgentStates.Booking] = booking.ToString(),
            [AgentStates.ExistingBooking] = template.ExistingBookingState,
        });
    }

    public async Task<List<KnowledgeDocument>> BuildKnowledgeDocumentsAsync(int orgId)
    {
        var org = await _settings.GetOrganizationAsync(orgId)
                  ?? throw new InvalidOperationException("Organization not found.");
        var services = (await _catalog.ListServicesAsync(orgId, 1, 500)).Items.ToList();
        var products = org.ProductsEnabled
            ? (await _catalog.ListProductsAsync(orgId, null, 1, 500)).Items.ToList()
            : new List<Domain.Product>();
        var kb = (await _knowledge.ListAsync(orgId)).ToList();

        var docs = new List<KnowledgeDocument>();

        // Services catalogue. This is the price list the agent quotes from — there is no pricing
        // tool behind it, so it has to read as an answer, not as a lookup table to be confirmed.
        var available = services.Where(s => s.IsAvailable).ToList();
        if (available.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"The services {org.Name} offers, and what they cost. This is the price list: " +
                          "quote from it directly. A price given as a range is a genuine range — say both " +
                          "ends of it. Anything not listed here cannot be priced over the phone.");
            sb.AppendLine();
            foreach (var s in available)
            {
                sb.AppendLine($"### {s.Name}");
                sb.AppendLine($"- Price range: {org.Currency} {s.MinPrice:0.##} to {org.Currency} {s.MaxPrice:0.##}");
                sb.AppendLine($"- Typical duration: {s.DurationMinutes} minutes");
                if (s.IsEmergency) sb.AppendLine("- Urgent work: this can usually be seen the same day, depending on what is actually free today");
                if (!string.IsNullOrWhiteSpace(s.Description)) sb.AppendLine($"- Details: {s.Description}");
                sb.AppendLine();
            }
            docs.Add(new KnowledgeDocument("Services and pricing", sb.ToString()));
        }

        // Product catalogue — often the largest and most volatile block.
        var sellable = products.Where(p => p.IsActive).ToList();
        if (sellable.Count > 0)
        {
            var sb = new StringBuilder();
            // Prices are stable enough to read straight out; stock is not. This list is a snapshot
            // from the last sync, so an "in stock" here is a fair indication and not a promise —
            // say so rather than have someone drive over for something that has since sold.
            sb.AppendLine($"What {org.Name} sells, and what it costs. This is the price list: quote from " +
                          "it directly. Stock is correct as of the last update rather than this minute — " +
                          "if the caller is making a journey for something, say you will confirm it is " +
                          "there and have someone call them back. Anything not listed here cannot be " +
                          "priced over the phone.");
            sb.AppendLine();
            foreach (var group in sellable.GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? "General" : p.Category))
            {
                sb.AppendLine($"### {group.Key}");
                foreach (var p in group)
                {
                    sb.AppendLine($"- {p.Name}: {org.Currency} {p.Price:0.##}" +
                                  (p.Quantity > 0 && p.IsAvailable ? " (in stock)" : " (out of stock)") +
                                  (string.IsNullOrWhiteSpace(p.Sku) ? "" : $" [SKU {p.Sku}]"));
                    if (!string.IsNullOrWhiteSpace(p.Description)) sb.AppendLine($"  {p.Description}");
                }
                sb.AppendLine();
            }
            docs.Add(new KnowledgeDocument("Product catalogue", sb.ToString()));
        }

        // Opening hours and closures are also a knowledge document, not just a prompt section:
        // "are you open on Sunday?" and "are you open over Christmas?" are asked constantly, and
        // retrieval answers them without the agent having to reason over the prompt.
        docs.Add(new KnowledgeDocument("Opening hours and holiday closures",
            await BuildHoursDocumentAsync(org)));

        AddCategoryDoc(docs, kb, "FAQ", "Frequently asked questions");
        AddCategoryDoc(docs, kb, "Policy", "Business policies");
        AddCategoryDoc(docs, kb, "BusinessInfo", $"About {org.Name}");
        AddCategoryDoc(docs, kb, "Custom", "Additional information");

        return docs;
    }

    private static DateTime TodayLocal(Domain.Organization org) =>
        TenantTime.NowLocal(TenantTime.Resolve(org.Timezone)).Date;

    private async Task<string> BuildHoursDocumentAsync(Domain.Organization org)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Opening hours for {org.Name}. All times are local ({org.Timezone}).");
        sb.AppendLine();
        sb.AppendLine("### Weekly hours");
        foreach (var line in BusinessHours.Describe(org.BusinessHoursJson))
            sb.AppendLine($"- {line}");

        var closures = (await _holidays.ListUpcomingAsync(org.Id, TodayLocal(org))).ToList();
        sb.AppendLine();
        sb.AppendLine("### Holiday closures");
        if (closures.Count == 0)
        {
            sb.AppendLine("No closures are scheduled — the weekly hours above apply on every date.");
        }
        else
        {
            sb.AppendLine("Closed all day on these dates, no appointments available:");
            foreach (var c in closures)
                sb.AppendLine($"- {c.Date:dddd d MMMM yyyy} — {c.Name}");
        }

        return sb.ToString();
    }

    private static void AddCategoryDoc(List<KnowledgeDocument> docs,
        List<Domain.KnowledgeBaseEntry> kb, string category, string title)
    {
        var entries = kb.Where(k => k.Category == category).ToList();
        if (entries.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.AppendLine($"### {e.Title}");
            sb.AppendLine(e.Content);
            sb.AppendLine();
        }
        docs.Add(new KnowledgeDocument(title, sb.ToString()));
    }
}
