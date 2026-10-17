using System.Text;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;

namespace AiReceptionist.Api.Services;

/// <summary>One fact the agent can be told about a business: a service and its price, a product,
/// an FAQ, a policy, the opening hours.</summary>
/// <param name="Summary">A few words for the one-line list of everything offered, where the item
/// belongs in one.</param>
public record InfoItem(string Kind, string Title, string Text, string? Summary = null);

/// <summary>
/// The agent's reference material, served from this platform's own database a piece at a time.
///
/// It replaces Retell's built-in knowledge base, which cost on two counts: a per-minute add-on on
/// every call, and the chunks it retrieved being pushed into the model's context on every turn,
/// asked for or not — which, on top of the prompt, is what took turns past the token count Retell
/// starts scaling the bill at. Here the agent asks with <c>lookup_info</c> only when a caller
/// actually wants a fact, and gets back the two or three entries that answer it.
///
/// It also cannot go stale: there is no upload to fall behind the dashboard, so an edited price is
/// the price on the very next call.
///
/// Only the business's own published facts are in here. Nothing about customers is, so a caller
/// can learn nothing through this tool that the business would not tell anyone who rang.
/// </summary>
public interface IKnowledgeLookupService
{
    /// <summary>Everything the tool can answer from, for the read-only preview.</summary>
    Task<List<InfoItem>> ListItemsAsync(int orgId);

    /// <summary>The few entries that best answer a caller's question, best first. Empty when
    /// nothing on file does.</summary>
    Task<List<InfoItem>> SearchAsync(int orgId, string question);
}

public static class InfoKinds
{
    public const string Service = "service";
    public const string Product = "product";
    public const string Hours = "hours";
    public const string Faq = "faq";
    public const string Policy = "policy";
    public const string About = "about";
    public const string Other = "info";

    /// <summary>Knowledge-base categories as the tool files them. Emergency rules are absent on
    /// purpose: they are safety instructions, and stay in the prompt where they hold on every turn.</summary>
    public static string? ForCategory(string category) => category switch
    {
        "FAQ" => Faq,
        "Policy" => Policy,
        "BusinessInfo" => About,
        "Custom" => Other,
        _ => null,
    };
}

public class KnowledgeLookupService : IKnowledgeLookupService
{
    /// <summary>How many entries one answer carries, and how long each may be. Everything the tool
    /// returns joins the conversation and is counted on every later turn, so it is kept to what
    /// answers the question.</summary>
    private const int MaxMatches = 3;
    private const int MaxItemChars = 500;
    private const int MaxOverviewChars = 700;

    private readonly ISettingsRepository _settings;
    private readonly ICatalogRepository _catalog;
    private readonly IKnowledgeRepository _knowledge;
    private readonly IHolidayRepository _holidays;

    public KnowledgeLookupService(ISettingsRepository settings, ICatalogRepository catalog,
        IKnowledgeRepository knowledge, IHolidayRepository holidays)
    {
        _settings = settings;
        _catalog = catalog;
        _knowledge = knowledge;
        _holidays = holidays;
    }

    public async Task<List<InfoItem>> ListItemsAsync(int orgId)
    {
        var org = await _settings.GetOrganizationAsync(orgId)
                  ?? throw new InvalidOperationException("Organization not found.");
        var items = new List<InfoItem>();
        var cur = org.Currency;

        foreach (var s in (await _catalog.ListServicesAsync(orgId, 1, 500)).Items.Where(s => s.IsAvailable))
        {
            var price = s.MinPrice == s.MaxPrice
                ? $"{cur} {s.MinPrice:0.##}"
                : $"between {cur} {s.MinPrice:0.##} and {cur} {s.MaxPrice:0.##} — a genuine range, so give both ends";
            items.Add(new InfoItem(InfoKinds.Service, s.Name,
                $"{s.Name}: {price}; usually about {s.DurationMinutes} minutes." +
                (s.IsEmergency ? " Urgent work: can usually be seen the same day, depending on what is free." : "") +
                (string.IsNullOrWhiteSpace(s.Description) ? "" : $" {s.Description.Trim()}"),
                Summary: s.MinPrice == s.MaxPrice
                    ? $"{s.Name} ({cur} {s.MinPrice:0.##})"
                    : $"{s.Name} ({cur} {s.MinPrice:0.##}–{s.MaxPrice:0.##})"));
        }

        if (org.ProductsEnabled)
            foreach (var p in (await _catalog.ListProductsAsync(orgId, null, 1, 500)).Items.Where(p => p.IsActive))
                items.Add(new InfoItem(InfoKinds.Product, p.Name,
                    $"{p.Name}" + (string.IsNullOrWhiteSpace(p.Category) ? "" : $" ({p.Category})") +
                    $": {cur} {p.Price:0.##}, " +
                    // Stock moves faster than anything else here, so it is said for what it is.
                    (p.Quantity > 0 && p.IsAvailable
                        ? "in stock at the last count — if they are making a trip for it, offer to have someone confirm first."
                        : "out of stock at the moment.") +
                    (string.IsNullOrWhiteSpace(p.Description) ? "" : $" {p.Description.Trim()}")));

        items.Add(new InfoItem(InfoKinds.Hours, "Opening hours and closures", await HoursTextAsync(org)));

        foreach (var e in await _knowledge.ListAsync(orgId))
            if (InfoKinds.ForCategory(e.Category) is { } kind && !string.IsNullOrWhiteSpace(e.Content))
                items.Add(new InfoItem(kind, e.Title, $"{e.Title}: {e.Content.Trim()}"));

        return items;
    }

    public async Task<List<InfoItem>> SearchAsync(int orgId, string question)
    {
        var items = await ListItemsAsync(orgId);
        var words = Words(question);
        var wantsHours = words.Overlaps(HoursWords);
        var wantsPrice = words.Overlaps(PriceWords) || question.Contains("how much", StringComparison.OrdinalIgnoreCase);
        // What is left once the question words are gone is what the caller is asking about.
        var terms = words.Where(w => !StopWords.Contains(w) && !PriceWords.Contains(w) && !CatalogueWords.Contains(w))
            .ToHashSet();

        // A word most entries use ("work", "electrical") says little about which one is meant; a
        // word only one uses ("homes") says a lot. Each term is weighted by how rare it is here.
        var indexed = items.Select(i => (item: i, title: Words(i.Title), body: Words(i.Text))).ToList();
        var weight = terms.ToDictionary(t => t, t =>
        {
            var df = indexed.Count(x => x.title.Contains(t) || x.body.Contains(t));
            return Math.Log(1 + (double)indexed.Count / Math.Max(1, df));
        });

        var scored = indexed
            .Select(x => (x.item, score: Score(x.item, x.title, x.body, weight, wantsHours)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ToList();

        // Weak matches are dropped relative to the best one, so a question about EV chargers is not
        // padded out with every entry that happens to mention a car.
        var best = scored.Count > 0 ? scored[0].score : 0;
        var matches = scored.Where(x => x.score >= best * 0.6).Take(MaxMatches)
            .Select(x => x.item with { Text = Clip(x.item.Text, MaxItemChars) })
            .ToList();

        // "What do you do?", "what are your prices?" — a question about the list, not one entry of it.
        if (IsCatalogueQuestion(words, wantsPrice, matches))
            matches.Insert(0, Overview(items));

        return matches;
    }

    // ---------- scoring ----------

    private static double Score(InfoItem item, HashSet<string> title, HashSet<string> body,
        Dictionary<string, double> weight, bool wantsHours)
    {
        double score = 0;
        foreach (var (t, w) in weight)
        {
            if (title.Contains(t)) score += 3 * w;
            else if (body.Contains(t)) score += w;
            // "electric" for "electrical", "install" for "installation": a shared stem is worth a little.
            else if (t.Length >= 5 && (title.Any(x => SharesStem(x, t)) || body.Any(x => SharesStem(x, t)))) score += 0.5 * w;
        }
        if (wantsHours && item.Kind == InfoKinds.Hours) score += 6;
        return score;
    }

    private static bool SharesStem(string a, string b) =>
        a.Length >= 5 && string.CompareOrdinal(a, 0, b, 0, 5) == 0;

    private static bool IsCatalogueQuestion(HashSet<string> words, bool wantsPrice, List<InfoItem> matches)
    {
        var aboutTheList = words.Overlaps(CatalogueWords);
        var namedOne = matches.Any(m => m.Kind is InfoKinds.Service or InfoKinds.Product);
        return (aboutTheList || wantsPrice) && !namedOne;
    }

    /// <summary>The services (and, briefly, the products) in one short paragraph.</summary>
    private static InfoItem Overview(List<InfoItem> items)
    {
        var sb = new StringBuilder("Services offered: ");
        sb.Append(string.Join("; ", items.Where(i => i.Kind == InfoKinds.Service).Select(i => i.Summary ?? i.Title)));
        var products = items.Count(i => i.Kind == InfoKinds.Product);
        if (products > 0) sb.Append($". Also {products} product{(products == 1 ? "" : "s")} for sale — ask which one they mean.");
        sb.Append(" Offer two or three that fit what they need rather than reading the list out.");
        return new InfoItem(InfoKinds.Service, "Services offered", Clip(sb.ToString(), MaxOverviewChars));
    }

    /// <summary>Lower-cased words with a plural "s" taken off, so "homes" finds "home".</summary>
    private static HashSet<string> Words(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        foreach (var ch in (text ?? "").ToLowerInvariant().Append(' '))
        {
            if (char.IsLetterOrDigit(ch)) { word.Append(ch); continue; }
            if (word.Length > 0) set.Add(Stem(word.ToString()));
            word.Clear();
        }
        return set;

        static string Stem(string w) =>
            w.Length > 4 && w.EndsWith("ies") ? w[..^3] + "y"
            : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1]
            : w;
    }

    /// <summary>Cut at a word boundary so the agent is never handed half a word.</summary>
    private static string Clip(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > 0 ? cut : max)] + "…";
    }

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "is", "are", "was", "be", "do", "doe", "did", "you", "your", "we", "our", "i", "me", "my",
        "of", "for", "to", "in", "on", "at", "and", "or", "what", "whats", "how", "can", "could", "would", "will",
        "with", "about", "any", "there", "that", "this", "it", "have", "ha", "much", "please", "tell", "know",
        "guy", "get", "if", "so", "just", "like", "want", "need", "some", "does", "which", "when", "where", "u",
    ];

    private static readonly HashSet<string> PriceWords =
        ["price", "pricing", "cost", "charge", "fee", "quote", "rate", "expensive", "cheap", "pay"];

    private static readonly HashSet<string> HoursWords =
    [
        "open", "opening", "close", "closed", "closing", "hour", "holiday", "weekend", "saturday", "sunday",
        "monday", "tuesday", "wednesday", "thursday", "friday", "christmas", "late", "early",
    ];

    private static readonly HashSet<string> CatalogueWords =
        ["service", "offer", "provide", "list", "menu", "everything", "option"];

    // ---------- hours ----------

    private async Task<string> HoursTextAsync(Domain.Organization org)
    {
        var sb = new StringBuilder($"Opening hours ({org.Timezone}): ");
        sb.Append(string.Join("; ", BusinessHours.Describe(org.BusinessHoursJson)));
        sb.Append('.');

        var today = TenantTime.NowLocal(TenantTime.Resolve(org.Timezone)).Date;
        var closures = (await _holidays.ListUpcomingAsync(org.Id, today, take: 8)).ToList();
        sb.Append(closures.Count == 0
            ? " No closures coming up."
            : " Closed all day on: " + string.Join("; ", closures.Select(c => $"{c.Date:dddd d MMMM yyyy} ({c.Name})")) +
              ". Never offer a time on those dates.");
        sb.Append(" What can actually be booked is whatever check_availability gives back.");
        return sb.ToString();
    }
}
