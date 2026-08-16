using System.Text;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;

namespace AiReceptionist.Api.Services;

/// <summary>A single document uploaded to the Retell knowledge base.</summary>
public record KnowledgeDocument(string Title, string Text);

/// <summary>
/// Splits tenant content into the two places Retell can hold it (SRS §13):
///
///  • <b>System prompt</b> — small, always in context: personality, rules, conversation
///    flow, business identity, hours, emergency rules and tool usage. Things the agent
///    must obey on every turn. Its wording is platform-wide and editable in the super admin
///    console (PromptTemplate); this class fills it in with the tenant's own facts.
///  • <b>Knowledge base</b> — retrieved on demand: product/service catalogues, FAQs,
///    policies and long reference text. Bulky, frequently changing content that would
///    otherwise bloat the prompt (and cost tokens on every single turn).
///
/// Clients never edit the core instructions; only their own data feeds these.
/// </summary>
public interface IPromptBuilderService
{
    Task<string> BuildSystemPromptAsync(int orgId);
    Task<List<KnowledgeDocument>> BuildKnowledgeDocumentsAsync(int orgId);
}

public class PromptBuilderService : IPromptBuilderService
{
    private readonly ISettingsRepository _settings;
    private readonly ICatalogRepository _catalog;
    private readonly IKnowledgeRepository _knowledge;
    private readonly IHolidayRepository _holidays;
    private readonly IPromptTemplateRepository _template;

    public PromptBuilderService(ISettingsRepository settings, ICatalogRepository catalog,
        IKnowledgeRepository knowledge, IHolidayRepository holidays, IPromptTemplateRepository template)
    {
        _settings = settings;
        _catalog = catalog;
        _knowledge = knowledge;
        _holidays = holidays;
        _template = template;
    }

    public async Task<string> BuildSystemPromptAsync(int orgId)
    {
        var org = await _settings.GetOrganizationAsync(orgId)
                  ?? throw new InvalidOperationException("Organization not found.");
        var agent = await _settings.GetAgentConfigAsync(orgId);
        var profile = IndustryTemplates.Resolve(org.Industry);
        var kb = (await _knowledge.ListAsync(orgId)).ToList();

        // Style, rules, call shape and tool policy are platform-wide and edited in the super admin
        // console; everything below them is this tenant's own data.
        var template = await _template.GetEffectiveAsync();

        var sb = new StringBuilder();

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
        sb.AppendLine(profile.IsFieldService ? template.FieldServiceGuide : template.ConversationGuide);

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

        // Emergency rules are safety-critical: they stay in the prompt, never the knowledge base.
        var emergencies = kb.Where(k => k.Category == "EmergencyRule").ToList();
        if (emergencies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Emergency rules (act on these immediately)");
            foreach (var e in emergencies) sb.AppendLine($"- {e.Title}: {e.Content}");
        }

        sb.AppendLine();
        sb.AppendLine(template.ToolPolicy);

        return sb.ToString();
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
                if (s.IsEmergency) sb.AppendLine("- Available as an emergency / same-day service");
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
