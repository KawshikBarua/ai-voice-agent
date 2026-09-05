namespace AiReceptionist.Shared;

/// <summary>
/// The rows of the billing tables, as both server applications read and write them.
///
/// These are shapes, not behaviour: the super admin console layers its own view helpers on top by
/// deriving (see <c>Models/PlatformEntities.cs</c>), and the tenant API uses them directly. Sharing
/// the shape is what stops a column added for one app from being invisible to the other.
/// </summary>
public class SubscriptionRecord
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string PlanName { get; set; } = "Standard";
    public string BillingCycle { get; set; } = BillingCycles.Monthly;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime StartedAt { get; set; }

    /// <summary>The <b>access</b> window: it moves only when a payment is recorded, and the grace
    /// period and overdue sweep read it. Minutes are not counted against it — see
    /// <see cref="UsageWindows"/> for why those need windows of their own.</summary>
    public DateTime CurrentPeriodStart { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }

    public int GraceDays { get; set; } = 7;
    public bool AutoSuspend { get; set; } = true;

    /// <summary>AI talk-time the plan includes per billing period. 0 means the plan is not metered
    /// on minutes, so there is no balance to run out and never any overage.</summary>
    public int IncludedMinutes { get; set; }

    public string? Notes { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public int? ModifiedByUserId { get; set; }

    /// <summary>The catalogue tier this was set from, if any. The numbers above are copies, so
    /// repricing a tier never silently re-bills everyone already on it.</summary>
    public int? PlanId { get; set; }

    /// <summary>Charged per minute past <see cref="IncludedMinutes"/>, on the following invoice.</summary>
    public decimal OverageRatePerMinute { get; set; }

    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    /// <summary>Stripe's own view: active, past_due, canceled, incomplete…</summary>
    public string? StripeStatus { get; set; }

    /// <summary>Overage totalled at a period close but not yet charged — this is what "added to
    /// your next bill" is made of. Cleared once it reaches a Stripe invoice.</summary>
    public int PendingOverageMinutes { get; set; }
    public decimal PendingOverageAmount { get; set; }

    /// <summary>A tier the customer has chosen to move to once the period they are in has closed.
    /// Held rather than applied because <see cref="IncludedMinutes"/> is the yardstick the closing
    /// period is measured against — changing it early would re-measure minutes already spent.</summary>
    public int? PendingPlanId { get; set; }

    /// <summary>
    /// True once this organization is actually on something — a catalogue tier, a price, or an
    /// allowance.
    ///
    /// A row can exist without any of those: one is created to hold the Stripe customer link
    /// before Checkout, so that a payment arriving by any route has somewhere to land. That row is
    /// bookkeeping, not a plan, and every screen must read it as "no plan yet" or a customer who
    /// has paid nothing would be shown a phantom 0.00 subscription.
    /// </summary>
    public bool HasPlan => PlanId is > 0 || Amount > 0 || IncludedMinutes > 0;

    /// <summary>Stripe is collecting for this organization rather than the operator chasing it.</summary>
    public bool IsStripeLinked => !string.IsNullOrWhiteSpace(StripeSubscriptionId);
    public bool HasStripeCustomer => !string.IsNullOrWhiteSpace(StripeCustomerId);
    /// <summary>False means the plan is not metered on minutes at all.</summary>
    public bool IsMetered => IncludedMinutes > 0;

    public DateTime NextPeriodEnd() => BillingCycles.Advance(CurrentPeriodEnd, BillingCycle);
}

public class PaymentRecord
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime PaidAt { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string Method { get; set; } = "BankTransfer";
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public int? RecordedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public string Source { get; set; } = PaymentSources.Manual;
    public string? StripeInvoiceId { get; set; }

    /// <summary>The charge behind this payment, and the receipt for it. A refund or a chargeback is
    /// raised against these, never against the invoice, so without them a reversal cannot be
    /// matched back to the payment it undoes.</summary>
    public string? StripePaymentIntentId { get; set; }
    public string? StripeChargeId { get; set; }
    public string? ReceiptUrl { get; set; }

    /// <summary>How much of this payment has been given back. The row is kept rather than deleted:
    /// a payment that was made and then refunded is two facts, and erasing the first loses the
    /// history that explains the second.</summary>
    public decimal AmountRefunded { get; set; }
    public DateTime? RefundedAt { get; set; }

    public bool FromStripe => string.Equals(Source, PaymentSources.Stripe, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the customer actually kept — what a statement should total.</summary>
    public decimal AmountNet => Math.Max(0, Amount - AmountRefunded);
    public bool IsRefunded => AmountRefunded > 0;
}

/// <summary>
/// One attempt to pay, opened the moment the customer is sent to Stripe Checkout and closed by
/// whatever happens next.
///
/// This exists so the payment record starts at the beginning rather than at "paid". Three
/// independent paths already make sure a completed payment lands — the webhook, the customer's own
/// return, and the reconciliation sweep — but none of them leave any trace of an attempt that did
/// not complete, which is exactly the case somebody rings up about.
/// </summary>
public class PaymentAttempt
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string StripeSessionId { get; set; } = "";
    public int? PlanId { get; set; }
    public string PlanName { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Status { get; set; } = PaymentAttemptStatus.Started;
    public int? StartedByUserId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? SettledAt { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string? StripeInvoiceId { get; set; }
    public string? StripePaymentIntentId { get; set; }

    /// <summary>Why it ended the way it did, in words a support conversation can use.</summary>
    public string? Outcome { get; set; }

    public bool IsSettled => !string.Equals(Status, PaymentAttemptStatus.Started, StringComparison.OrdinalIgnoreCase);
    public bool Succeeded => string.Equals(Status, PaymentAttemptStatus.Paid, StringComparison.OrdinalIgnoreCase);
}

public static class PaymentAttemptStatus
{
    /// <summary>The customer has been sent to Stripe and has not come back yet. A row that stays
    /// here for more than a few minutes is either an abandoned checkout or a delivery that never
    /// arrived — and the difference is worth being able to see.</summary>
    public const string Started = "Started";

    /// <summary>Stripe collected, and the plan has been applied.</summary>
    public const string Paid = "Paid";

    /// <summary>The Checkout session expired without payment. Nothing was charged.</summary>
    public const string Abandoned = "Abandoned";

    /// <summary>Stripe tried to collect and could not — a declined card, most often.</summary>
    public const string Failed = "Failed";
}

public static class PaymentSources
{
    /// <summary>Entered by hand in the super admin console.</summary>
    public const string Manual = "Manual";
    /// <summary>Collected by Stripe and mirrored in by the webhook.</summary>
    public const string Stripe = "Stripe";
}

public static class PaymentMethods
{
    public static readonly string[] All = ["BankTransfer", "Card", "Cash", "Cheque", "Other"];
}

/// <summary>One tier in the catalogue: what a customer can be put on. Assigning it copies these
/// numbers onto the organization's subscription, where they can then be overridden per customer.</summary>
public class PricingPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    public string BillingCycle { get; set; } = BillingCycles.Monthly;
    public int IncludedMinutes { get; set; }
    public decimal OverageRatePerMinute { get; set; }
    public string? StripeProductId { get; set; }
    public string? StripePriceId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public int? ModifiedByUserId { get; set; }

    /// <summary>Without a Stripe price this tier can be assigned and billed by hand, but Stripe
    /// cannot collect for it — so the console offers to create one.</summary>
    public bool IsStripeReady => !string.IsNullOrWhiteSpace(StripePriceId);

    /// <summary>How many organizations are on this tier (joined; not a stored column).</summary>
    public int SubscriberCount { get; set; }
}

/// <summary>One billing period that has closed, and the arithmetic behind what it cost. This is
/// the evidence a charge is explained from — never recomputed from call logs afterwards, so a
/// customer asking about an old invoice gets the numbers that were actually used.</summary>
public class UsagePeriod
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string PlanName { get; set; } = "";
    public decimal BaseAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public int IncludedMinutes { get; set; }
    public int MinutesUsed { get; set; }
    public int OverageMinutes { get; set; }
    public decimal OverageRatePerMinute { get; set; }
    public decimal OverageAmount { get; set; }
    public string Status { get; set; } = UsagePeriodStatus.Pending;
    public string? StripeInvoiceId { get; set; }
    public DateTime ClosedAt { get; set; }

    public bool IsBilled => string.Equals(Status, UsagePeriodStatus.Billed, StringComparison.OrdinalIgnoreCase);
}

public static class UsagePeriodStatus
{
    /// <summary>Totalled, waiting to be added to the next invoice.</summary>
    public const string Pending = "Pending";
    /// <summary>It reached an invoice, or there was nothing to charge.</summary>
    public const string Billed = "Billed";
}

/// <summary>A Stripe invoice as mirrored locally, so charges can be listed without a Stripe
/// round-trip and survive the account ever being detached.</summary>
public class OrganizationInvoice
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string StripeInvoiceId { get; set; } = "";
    public string? Number { get; set; }
    public string Status { get; set; } = "draft";
    public string Currency { get; set; } = "USD";
    public decimal Subtotal { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal AmountDue { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public string? HostedInvoiceUrl { get; set; }
    public string? InvoicePdfUrl { get; set; }
    public DateTime? IssuedAt { get; set; }
    public DateTime? PaidAt { get; set; }

    public List<OrganizationInvoiceLine> Lines { get; set; } = [];

    public bool IsPaid => string.Equals(Status, "paid", StringComparison.OrdinalIgnoreCase);

    /// <summary>What is still owed on this invoice.
    ///
    /// Not <see cref="AmountDue"/>, which mirrors Stripe's <c>amount_due</c> — the amount the
    /// invoice was raised for, which does not drop to zero on payment. Reading that as a balance
    /// tells a customer who has paid in full that they still owe the whole invoice.</summary>
    public decimal AmountOutstanding => Math.Max(0, Total - AmountPaid);
}

public class OrganizationInvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public string Description { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public decimal Amount { get; set; }
    public string Kind { get; set; } = InvoiceLineKinds.Other;
    public int SortOrder { get; set; }
}

public static class SuspensionReasons
{
    /// <summary>Written by the automatic overdue sweep. Recognised on the way back, so that paying
    /// up re-enables an automatically disabled account while a deliberate manual suspension is only
    /// ever lifted by hand. Both applications compare against it, so it lives here.</summary>
    public const string Overdue = "Subscription overdue";
}

/// <summary>The opening words of the invoice line this platform adds for minutes used beyond a
/// plan. Written by the API when it attaches the charge and matched when mirroring the invoice
/// back, so a charge the platform raised is never mistaken for the plan's own line.</summary>
public static class OverageCharge
{
    public const string DescriptionPrefix = "AI minutes over plan";
}

public static class InvoiceLineKinds
{
    /// <summary>The recurring plan charge itself.</summary>
    public const string Subscription = "Subscription";
    /// <summary>Minutes used beyond the plan in an earlier period.</summary>
    public const string Overage = "Overage";
    public const string Other = "Other";
}
