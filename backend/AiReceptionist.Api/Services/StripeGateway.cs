using Stripe;
using Stripe.Checkout;

namespace AiReceptionist.Api.Services;

/// <summary>Where a customer is sent back to after Checkout or the billing portal. These are
/// tenant-app URLs, so they are configured rather than guessed.</summary>
public class StripeUrls
{
    public string Success { get; init; } = "";
    public string Cancel { get; init; } = "";
    public string PortalReturn { get; init; } = "";
}

/// <summary>
/// Everything this platform asks of Stripe, in one place.
///
/// Stripe lives in the tenant API rather than the super admin console for two reasons: the webhook
/// needs a publicly reachable endpoint, which this application already is (Retell calls it), and
/// the secret key then exists in exactly one process. The console drives all of this through the
/// platform endpoints, the same way it already drives Retell.
/// </summary>
public interface IStripeGateway
{
    /// <summary>False when no secret key is configured. Every screen degrades to manual billing
    /// rather than failing — a platform that has not connected Stripe yet still works.</summary>
    bool IsConfigured { get; }

    /// <summary>Finds or creates the Stripe customer for an organization.</summary>
    Task<string> EnsureCustomerAsync(int orgId, string orgName, string? email,
        string? existingCustomerId, CancellationToken ct = default);

    /// <summary>Creates the Stripe product and recurring price behind a tier. Prices are immutable
    /// in Stripe, so repricing a tier creates a new one and leaves the old one serving the
    /// subscriptions already on it.</summary>
    Task<(string ProductId, string PriceId)> CreatePriceAsync(PricingPlan plan, CancellationToken ct = default);

    /// <summary>Hosted Checkout for a customer subscribing themselves.</summary>
    Task<string> CreateCheckoutSessionAsync(int orgId, int planId, string customerId, string priceId,
        CancellationToken ct = default);

    /// <summary>The Stripe billing portal — where the customer changes their card and reads their
    /// own invoices. Card details never touch this application.</summary>
    Task<string> CreatePortalSessionAsync(string customerId, CancellationToken ct = default);

    /// <summary>Attaches a one-off charge. With <paramref name="invoiceId"/> it lands on that draft
    /// invoice; without one it waits for the customer's next invoice.</summary>
    Task AddInvoiceItemAsync(string customerId, string? invoiceId, decimal amount, string currency,
        string description, CancellationToken ct = default);

    /// <summary>Raises and emails a one-off invoice — the operator-driven alternative to Checkout.
    /// Any pending invoice items are swept onto it.</summary>
    Task<Invoice> CreateAndSendInvoiceAsync(string customerId, int daysUntilDue,
        CancellationToken ct = default);

    Task<Subscription> CreateSubscriptionAsync(int orgId, string customerId, string priceId,
        CancellationToken ct = default);

    Task CancelSubscriptionAsync(string subscriptionId, CancellationToken ct = default);

    /// <summary>Moves a live subscription onto a different price, charged from the next renewal
    /// rather than the moment it is asked for.</summary>
    Task UpdateSubscriptionPriceAsync(string subscriptionId, string priceId,
        CancellationToken ct = default);

    Task<Invoice?> GetInvoiceAsync(string invoiceId, CancellationToken ct = default);

    /// <summary>Verifies the signature and parses the event. Throws <see cref="StripeException"/>
    /// on a bad signature, which the controller turns into a 400.</summary>
    Event ConstructEvent(string payload, string signatureHeader);
}

public class StripeGateway : IStripeGateway
{
    private readonly StripeClient? _client;
    private readonly string? _webhookSecret;
    private readonly StripeUrls _urls;
    private readonly ILogger<StripeGateway> _logger;

    public StripeGateway(IConfiguration config, ILogger<StripeGateway> logger)
    {
        _logger = logger;

        var secretKey = Environment.GetEnvironmentVariable("STRIPE__SECRETKEY")
            ?? config["Stripe:SecretKey"];

        // A placeholder is not a key. Treating it as one would produce a confusing 401 from Stripe
        // on the first customer action instead of an honest "Stripe is not connected" up front.
        if (!string.IsNullOrWhiteSpace(secretKey) &&
            !secretKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            _client = new StripeClient(secretKey.Trim());
        }

        _webhookSecret = Environment.GetEnvironmentVariable("STRIPE__WEBHOOKSECRET")
            ?? config["Stripe:WebhookSecret"];

        // Blank, not just missing: the shipped appsettings.json carries "AppBaseUrl": "" as its
        // documentation anchor, and `??` would accept that empty string and hand Stripe a relative
        // "/billing?checkout=success", which it rejects with "Not a valid URL" at Checkout.
        var appBase = FirstNonBlank(
            config["Stripe:AppBaseUrl"],
            config.GetSection("Cors:AllowedOrigins").Get<string[]>()?.FirstOrDefault(),
            "http://localhost:5173")!.TrimEnd('/');

        _urls = new StripeUrls
        {
            Success = FirstNonBlank(config["Stripe:SuccessUrl"], $"{appBase}/billing?checkout=success")!,
            Cancel = FirstNonBlank(config["Stripe:CancelUrl"], $"{appBase}/billing?checkout=cancelled")!,
            PortalReturn = FirstNonBlank(config["Stripe:PortalReturnUrl"], $"{appBase}/billing")!,
        };

        if (_client is null)
            _logger.LogInformation(
                "Stripe is not configured (Stripe:SecretKey). Billing screens will work, but nothing " +
                "will be collected automatically — payments have to be recorded by hand.");
        else if (string.IsNullOrWhiteSpace(_webhookSecret))
            _logger.LogWarning(
                "Stripe:WebhookSecret is not set. The webhook endpoint will reject every delivery, so " +
                "invoices and payments will never be mirrored back. Set it from the Stripe dashboard.");
    }

    /// <summary>First value that is neither null nor blank. Configuration keys left empty as
    /// placeholders have to fall through to the next candidate, which <c>??</c> does not do.</summary>
    private static string? FirstNonBlank(params string?[] candidates) =>
        Array.Find(candidates, c => !string.IsNullOrWhiteSpace(c));

    public bool IsConfigured => _client is not null;

    private StripeClient Client => _client
        ?? throw new InvalidOperationException("Stripe is not configured (Stripe:SecretKey is not set).");

    public async Task<string> EnsureCustomerAsync(int orgId, string orgName, string? email,
        string? existingCustomerId, CancellationToken ct = default)
    {
        var customers = new CustomerService(Client);

        if (!string.IsNullOrWhiteSpace(existingCustomerId))
        {
            try
            {
                var existing = await customers.GetAsync(existingCustomerId, cancellationToken: ct);
                if (existing is not null && existing.Deleted != true) return existing.Id;

                _logger.LogWarning(
                    "Stripe customer {CustomerId} for organization {OrgId} is deleted; creating a new one.",
                    existingCustomerId, orgId);
            }
            catch (StripeException ex)
            {
                // A key rotated to a different Stripe account will not know this customer. Making
                // a fresh one beats failing every billing action from here on.
                _logger.LogWarning(ex,
                    "Stripe does not recognise customer {CustomerId} for organization {OrgId}; creating a new one.",
                    existingCustomerId, orgId);
            }
        }

        var created = await customers.CreateAsync(new CustomerCreateOptions
        {
            Name = orgName,
            Email = string.IsNullOrWhiteSpace(email) ? null : email,
            // The webhook resolves the tenant from our own tables first, but a customer created
            // here and linked later is much easier to reconcile with the id written on it.
            Metadata = new Dictionary<string, string> { ["organizationId"] = orgId.ToString() },
        }, cancellationToken: ct);

        _logger.LogInformation("Created Stripe customer {CustomerId} for organization {OrgId}.",
            created.Id, orgId);
        return created.Id;
    }

    public async Task<(string ProductId, string PriceId)> CreatePriceAsync(
        PricingPlan plan, CancellationToken ct = default)
    {
        var prices = new PriceService(Client);
        var price = await prices.CreateAsync(new PriceCreateOptions
        {
            Currency = plan.Currency.ToLowerInvariant(),
            UnitAmount = StripeMoney.ToMinorUnits(plan.Amount, plan.Currency),
            Recurring = new PriceRecurringOptions
            {
                Interval = BillingCycles.IsYearly(plan.BillingCycle) ? "year" : "month",
            },
            // Reusing the product across repricings keeps one thing in Stripe's catalogue per tier.
            Product = string.IsNullOrWhiteSpace(plan.StripeProductId) ? null : plan.StripeProductId,
            ProductData = string.IsNullOrWhiteSpace(plan.StripeProductId)
                ? new PriceProductDataOptions { Name = plan.Name }
                : null,
            Metadata = new Dictionary<string, string>
            {
                ["planId"] = plan.Id.ToString(),
                ["includedMinutes"] = plan.IncludedMinutes.ToString(),
            },
        }, cancellationToken: ct);

        _logger.LogInformation("Created Stripe price {PriceId} for tier {PlanName}.", price.Id, plan.Name);
        return (price.ProductId, price.Id);
    }

    public async Task<string> CreateCheckoutSessionAsync(int orgId, int planId, string customerId,
        string priceId, CancellationToken ct = default)
    {
        var sessions = new SessionService(Client);
        var session = await sessions.CreateAsync(new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customerId,
            LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            SuccessUrl = _urls.Success,
            CancelUrl = _urls.Cancel,
            ClientReferenceId = orgId.ToString(),
            // The chosen tier rides along so the webhook can put the account on it once payment
            // has actually gone through — picking a tier and abandoning Checkout changes nothing.
            Metadata = new Dictionary<string, string>
            {
                ["organizationId"] = orgId.ToString(),
                ["planId"] = planId.ToString(),
            },
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string> { ["organizationId"] = orgId.ToString() },
            },
        }, cancellationToken: ct);

        return session.Url;
    }

    public async Task<string> CreatePortalSessionAsync(string customerId, CancellationToken ct = default)
    {
        var portal = new Stripe.BillingPortal.SessionService(Client);
        var session = await portal.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = _urls.PortalReturn,
        }, cancellationToken: ct);

        return session.Url;
    }

    public async Task AddInvoiceItemAsync(string customerId, string? invoiceId, decimal amount,
        string currency, string description, CancellationToken ct = default)
    {
        var items = new InvoiceItemService(Client);
        await items.CreateAsync(new InvoiceItemCreateOptions
        {
            Customer = customerId,
            Invoice = string.IsNullOrWhiteSpace(invoiceId) ? null : invoiceId,
            Amount = StripeMoney.ToMinorUnits(amount, currency),
            Currency = currency.ToLowerInvariant(),
            // This description is what the customer reads on the invoice, so it carries the whole
            // explanation: how many minutes, at what rate, for which period.
            Description = description,
        }, cancellationToken: ct);
    }

    public async Task<Invoice> CreateAndSendInvoiceAsync(string customerId, int daysUntilDue,
        CancellationToken ct = default)
    {
        var invoices = new InvoiceService(Client);

        // Sweeps up every pending invoice item on the customer — the plan charge the caller just
        // added, plus any overage carried over from a closed period.
        var draft = await invoices.CreateAsync(new InvoiceCreateOptions
        {
            Customer = customerId,
            CollectionMethod = "send_invoice",
            DaysUntilDue = daysUntilDue,
        }, cancellationToken: ct);

        var finalized = await invoices.FinalizeInvoiceAsync(draft.Id, cancellationToken: ct);
        return await invoices.SendInvoiceAsync(finalized.Id, cancellationToken: ct);
    }

    public async Task<Subscription> CreateSubscriptionAsync(int orgId, string customerId, string priceId,
        CancellationToken ct = default)
    {
        var subscriptions = new SubscriptionService(Client);
        return await subscriptions.CreateAsync(new SubscriptionCreateOptions
        {
            Customer = customerId,
            Items = [new SubscriptionItemOptions { Price = priceId, Quantity = 1 }],
            Metadata = new Dictionary<string, string> { ["organizationId"] = orgId.ToString() },
        }, cancellationToken: ct);
    }

    public async Task CancelSubscriptionAsync(string subscriptionId, CancellationToken ct = default)
    {
        var subscriptions = new SubscriptionService(Client);
        await subscriptions.CancelAsync(subscriptionId, cancellationToken: ct);
    }

    public async Task UpdateSubscriptionPriceAsync(string subscriptionId, string priceId,
        CancellationToken ct = default)
    {
        var subscriptions = new SubscriptionService(Client);
        var existing = await subscriptions.GetAsync(subscriptionId, cancellationToken: ct);

        // The existing item is replaced rather than a second one added, so the customer ends up on
        // one tier instead of being charged for both.
        var item = existing.Items?.Data?.FirstOrDefault()
            ?? throw new StripeException($"Stripe subscription {subscriptionId} has no items to move.");

        await subscriptions.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
        {
            Items = [new SubscriptionItemOptions { Id = item.Id, Price = priceId }],
            // None, and the anchor left alone: the customer asked to change tier from the next
            // cycle, so nothing is charged or credited now and the renewal date does not move.
            ProrationBehavior = "none",
        }, cancellationToken: ct);

        _logger.LogInformation(
            "Stripe subscription {SubscriptionId} moves to price {PriceId} at its next renewal.",
            subscriptionId, priceId);
    }

    public async Task<Invoice?> GetInvoiceAsync(string invoiceId, CancellationToken ct = default)
    {
        try
        {
            return await new InvoiceService(Client).GetAsync(invoiceId, cancellationToken: ct);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Could not read Stripe invoice {InvoiceId}.", invoiceId);
            return null;
        }
    }

    public Event ConstructEvent(string payload, string signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_webhookSecret))
            throw new StripeException("Stripe:WebhookSecret is not configured.");

        // throwOnApiVersionMismatch: false — Stripe accounts get upgraded to newer API versions
        // independently of this library, and a mismatch is not a reason to drop a real payment.
        return EventUtility.ConstructEvent(payload, signatureHeader, _webhookSecret,
            throwOnApiVersionMismatch: false);
    }
}
