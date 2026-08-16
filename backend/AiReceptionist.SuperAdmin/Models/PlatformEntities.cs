using AiReceptionist.Shared;

namespace AiReceptionist.SuperAdmin.Models;

/// <summary>The signed-in platform operator. Backed by a row in the shared Users table whose
/// Role is 'SuperAdmin' — the tenant application no longer exposes anything to that role, so
/// these accounts exist purely to sign in here.</summary>
public class PlatformUser
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "";
}

/// <summary>Billing state of one organization, derived rather than stored: an organization is
/// paid while now is inside the current period plus its grace days.</summary>
public enum BillingState
{
    /// <summary>No subscription has been set up yet — nothing to collect, nothing overdue.</summary>
    NotConfigured,
    Paid,
    /// <summary>Period has ended but the grace window has not lapsed.</summary>
    DueSoon,
    Overdue,
}

/// <summary>The subscription row plus the standing this console judges it by. The columns live in
/// <see cref="SubscriptionRecord"/>, shared with the tenant API; only the console needs to reason
/// about paid/overdue, so that stays here.</summary>
public class Subscription : SubscriptionRecord
{
    /// <summary>Last moment the account is considered paid up.</summary>
    public DateTime PaidThrough => CurrentPeriodEnd.AddDays(GraceDays);

    public BillingState StateAt(DateTime utcNow) =>
        utcNow <= CurrentPeriodEnd ? BillingState.Paid
        : utcNow <= PaidThrough ? BillingState.DueSoon
        : BillingState.Overdue;

    /// <summary>Negative once the period has ended.</summary>
    public int DaysUntilDue(DateTime utcNow) => (int)Math.Floor((CurrentPeriodEnd - utcNow).TotalDays);
}

public class Payment : PaymentRecord
{
    // joined
    public string? RecordedByName { get; set; }
}

/// <summary>The single platform-wide Retell credential. Every tenant agent is created under this
/// one Retell account, so the connection is not per-organization.</summary>
public class RetellConnection
{
    public int Id { get; set; }
    public string? ApiKey { get; set; }
    public string ApiBaseUrl { get; set; } = "https://api.retellai.com";
    public string? WebhookBaseUrl { get; set; }
    public string? DefaultVoiceId { get; set; }
    public bool VerifySignature { get; set; } = true;
    public DateTime? ModifiedAt { get; set; }
    public int? ModifiedByUserId { get; set; }
}

/// <summary>One tenant's Retell agent binding, as stored in AgentConfig.</summary>
public class OrganizationAgent
{
    public int OrganizationId { get; set; }
    public string Voice { get; set; } = "";
    public string Language { get; set; } = "";
    public string? TransferNumber { get; set; }
    public string? RetellAgentId { get; set; }
    public string? RetellLlmId { get; set; }
    public string? RetellKnowledgeBaseId { get; set; }
    public string? RetellPhoneNumber { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public bool Enabled { get; set; }

    /// <summary>Connected means an agent exists on Retell for this tenant. Once true the admin
    /// may only re-sync or disconnect — never "connect" a second agent.</summary>
    public bool IsConnected => !string.IsNullOrWhiteSpace(RetellAgentId);
}
