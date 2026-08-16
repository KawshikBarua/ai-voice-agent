namespace AiReceptionist.SuperAdmin.Models;

/// <summary>One organization as shown on the platform dashboard and organization list.
/// Revenue and outstanding follow the same definition the tenant dashboard uses (paid /
/// unpaid appointment amounts) so both screens agree.</summary>
public class OrganizationRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string Currency { get; set; } = "USD";
    public string Timezone { get; set; } = "UTC";
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public string? SuspendedReason { get; set; }

    /// <summary>Narrower than suspension: the agent stops taking calls, the dashboard keeps
    /// working, so a customer who has been cut off can still sign in and settle up.</summary>
    public bool AgentRestricted { get; set; }
    public DateTime? AgentRestrictedAt { get; set; }
    public string? AgentRestrictedReason { get; set; }

    public int UserCount { get; set; }
    public int CustomerCount { get; set; }
    public int AppointmentCount { get; set; }
    public int UpcomingAppointments { get; set; }
    public decimal Revenue { get; set; }
    public decimal Revenue30d { get; set; }
    public decimal Outstanding { get; set; }

    public int TotalCalls { get; set; }
    public int Calls30d { get; set; }
    public int MissedCalls { get; set; }
    public int TransferredCalls { get; set; }
    public DateTime? LastCallAt { get; set; }

    public string? RetellAgentId { get; set; }
    public string? RetellPhoneNumber { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public bool AgentEnabled { get; set; }

    public string? PlanName { get; set; }
    public string? BillingCycle { get; set; }
    public decimal? SubscriptionAmount { get; set; }
    public string? SubscriptionCurrency { get; set; }
    public DateTime? CurrentPeriodEnd { get; set; }
    public int? GraceDays { get; set; }
    public bool? AutoSuspend { get; set; }

    public int? PlanId { get; set; }
    public int IncludedMinutes { get; set; }
    public decimal OverageRatePerMinute { get; set; }
    public string? StripeStatus { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public int PendingOverageMinutes { get; set; }
    public decimal PendingOverageAmount { get; set; }

    /// <summary>Minutes in the window now running — measured from the last closed period, the same
    /// anchor billing uses, so this and the customer's own page agree.</summary>
    public int MinutesUsedThisPeriod { get; set; }

    public bool IsAgentConnected => !string.IsNullOrWhiteSpace(RetellAgentId);
    public bool HasSubscription => CurrentPeriodEnd.HasValue;

    /// <summary>Stripe is collecting for this organization rather than the operator chasing it.</summary>
    public bool IsStripeLinked => !string.IsNullOrWhiteSpace(StripeSubscriptionId);
    public bool IsMetered => IncludedMinutes > 0;

    /// <summary>Over the plan's allowance right now. Not yet charged — it lands on the next invoice
    /// once the period closes.</summary>
    public int MinutesOver => IsMetered ? Math.Max(0, MinutesUsedThisPeriod - IncludedMinutes) : 0;

    /// <summary>0–1 for the meter; over-allowance is reported separately rather than by a bar
    /// that runs off the end.</summary>
    public double UsageRatio => IsMetered
        ? Math.Min(1d, MinutesUsedThisPeriod / (double)IncludedMinutes)
        : 0d;

    public DateTime? PaidThrough => CurrentPeriodEnd?.AddDays(GraceDays ?? 0);

    public BillingState BillingState =>
        !HasSubscription ? BillingState.NotConfigured
        : DateTime.UtcNow <= CurrentPeriodEnd!.Value ? BillingState.Paid
        : DateTime.UtcNow <= PaidThrough!.Value ? BillingState.DueSoon
        : BillingState.Overdue;

    /// <summary>What the admin is being asked to act on: overdue and still enabled means the
    /// account is running unpaid.</summary>
    public bool NeedsAttention => BillingState == BillingState.Overdue && IsActive;
}

/// <summary>Platform-wide roll-up shown at the top of the dashboard.</summary>
public class PlatformSummary
{
    public int TotalOrganizations { get; set; }
    public int ActiveOrganizations { get; set; }
    public int SuspendedOrganizations { get; set; }
    public int ConnectedAgents { get; set; }
    public int OverdueOrganizations { get; set; }
    public decimal TenantRevenue { get; set; }
    public decimal SubscriptionRevenue30d { get; set; }
    public int TotalCalls { get; set; }
    public int Calls30d { get; set; }
    public int TotalAppointments { get; set; }
    public bool RetellApiKeyConfigured { get; set; }
}

public class DashboardViewModel
{
    public PlatformSummary Summary { get; set; } = new();
    public IReadOnlyList<OrganizationRow> Organizations { get; set; } = [];

    public IEnumerable<OrganizationRow> NeedingAttention =>
        Organizations.Where(o => o.NeedsAttention).OrderBy(o => o.CurrentPeriodEnd);
}

public class OrganizationListViewModel
{
    public IReadOnlyList<OrganizationRow> Organizations { get; set; } = [];
    public string? Search { get; set; }
    /// <summary>all | active | suspended | overdue | connected | disconnected</summary>
    public string Filter { get; set; } = "all";
}

/// <summary>A single call as listed on the organization detail screen.</summary>
public class CallRow
{
    public int Id { get; set; }
    public string FromNumber { get; set; } = "";
    public string Status { get; set; } = "";
    public int DurationSeconds { get; set; }
    public string? Summary { get; set; }
    public DateTime StartedAt { get; set; }
    public string? CustomerName { get; set; }
}

public class AppointmentRow
{
    public int Id { get; set; }
    public DateTime StartAt { get; set; }
    public string Status { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public decimal Amount { get; set; }
    public string? CustomerName { get; set; }
    public string? ServiceName { get; set; }
}

public class MonthlyPoint
{
    public int Month { get; set; }
    public int Value { get; set; }
}

public class OrganizationDetailViewModel
{
    public OrganizationRow Organization { get; set; } = new();
    public OrganizationAgent? Agent { get; set; }
    public Subscription? Subscription { get; set; }
    public IReadOnlyList<Payment> Payments { get; set; } = [];

    /// <summary>The tiers this organization can be put on.</summary>
    public IReadOnlyList<PricingPlan> Plans { get; set; } = [];
    /// <summary>Closed periods — what was used and what it came to.</summary>
    public IReadOnlyList<UsagePeriod> UsagePeriods { get; set; } = [];
    public IReadOnlyList<OrganizationInvoice> Invoices { get; set; } = [];
    public bool StripeConfigured { get; set; }

    public IReadOnlyList<CallRow> RecentCalls { get; set; } = [];
    public IReadOnlyList<AppointmentRow> RecentAppointments { get; set; } = [];
    public IReadOnlyList<MonthlyPoint> CallsPerMonth { get; set; } = [];
    public bool RetellApiKeyConfigured { get; set; }

    /// <summary>Twelve months of call volume, zero-filled, for the sparkline.</summary>
    public IEnumerable<int> CallSeries =>
        Enumerable.Range(1, 12).Select(m => CallsPerMonth.FirstOrDefault(p => p.Month == m)?.Value ?? 0);
}

// ---------- form inputs ----------

public class LoginInput
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class RetellConnectionInput
{
    /// <summary>Blank means "keep the stored key" so URLs can be edited without re-entering it.</summary>
    public string? ApiKey { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? WebhookBaseUrl { get; set; }
    public string? DefaultVoiceId { get; set; }
    public bool VerifySignature { get; set; } = true;
}

/// <summary>The prompt wording as submitted from the editor. Every section is sent, whether it was
/// touched or not; the API decides which of them are genuine overrides.</summary>
public class PromptTemplateInput
{
    public string? Persona { get; set; }
    public string? CoreRules { get; set; }
    public string? ConversationGuide { get; set; }
    public string? FieldServiceGuide { get; set; }
    public string? ToolPolicy { get; set; }
}

/// <summary>The two numbers the platform operator owns. Blank clears a number.</summary>
public class AgentNumbersInput
{
    public int OrganizationId { get; set; }
    public string? TransferNumber { get; set; }
    public string? RetellPhoneNumber { get; set; }
}

public class SubscriptionInput
{
    public int OrganizationId { get; set; }
    public string PlanName { get; set; } = "Standard";
    public string BillingCycle { get; set; } = BillingCycles.Monthly;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime CurrentPeriodStart { get; set; } = DateTime.UtcNow.Date;
    public DateTime CurrentPeriodEnd { get; set; } = DateTime.UtcNow.Date.AddMonths(1);
    public int GraceDays { get; set; } = 7;
    public bool AutoSuspend { get; set; } = true;
    /// <summary>0 = the plan is not metered on minutes.</summary>
    public int IncludedMinutes { get; set; }
    /// <summary>Charged per minute past the allowance, on the following invoice.</summary>
    public decimal OverageRatePerMinute { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Creating or editing a tier in the catalogue.</summary>
public class PlanInput
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    public string BillingCycle { get; set; } = BillingCycles.Monthly;
    public int IncludedMinutes { get; set; }
    public decimal OverageRatePerMinute { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Create the matching price in Stripe so it can collect for this tier.</summary>
    public bool CreateStripePrice { get; set; }
}

/// <summary>Putting one organization on a tier. The blank overrides mean "use the tier's own
/// numbers"; filling one in prices this customer differently without touching the tier.</summary>
public class AssignPlanInput
{
    public int OrganizationId { get; set; }
    public int PlanId { get; set; }
    public decimal? Amount { get; set; }
    public int? IncludedMinutes { get; set; }
    public decimal? OverageRatePerMinute { get; set; }
}

public class PricingViewModel
{
    public IReadOnlyList<PricingPlan> Plans { get; set; } = [];
    public PricingPlan? Editing { get; set; }
    public bool StripeConfigured { get; set; }
}

public class PaymentInput
{
    public int OrganizationId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidAt { get; set; } = DateTime.UtcNow.Date;
    public string Method { get; set; } = "BankTransfer";
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    /// <summary>Roll the billing period forward by one cycle. Off when recording a
    /// back-dated or partial payment that should not extend access.</summary>
    public bool AdvancePeriod { get; set; } = true;
}
