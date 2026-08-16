namespace AiReceptionist.Api.Domain;

public class Organization
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";
    public string? Logo { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal TaxRate { get; set; }
    public string Timezone { get; set; } = "UTC";
    public string? BusinessHoursJson { get; set; }
    /// <summary>How many appointments may overlap at once (e.g. number of staff/rooms).</summary>
    public int MaxConcurrentAppointments { get; set; } = 1;
    public bool ProductsEnabled { get; set; } = true;
    public bool OnboardingCompleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Set from the super admin console (AiReceptionist.SuperAdmin), typically when a
    /// subscription goes unpaid. A disabled organization cannot sign in, cannot refresh a token
    /// and its AI agent's live-call tools are refused. Never writable by the tenant itself.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime? SuspendedAt { get; set; }
    public string? SuspendedReason { get; set; }

    /// <summary>Also set from the super admin console, but narrower than suspension: the AI agent
    /// stops serving live calls while the dashboard keeps working, so a customer who has been cut
    /// off can still sign in, see why, and settle the bill. Never writable by the tenant.</summary>
    public bool AgentRestricted { get; set; }
    public DateTime? AgentRestrictedAt { get; set; }
    public string? AgentRestrictedReason { get; set; }
}

public class User
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.ReadOnly;
    public bool EmailVerified { get; set; }
    public bool MfaEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string OrgAdmin = "OrgAdmin";
    public const string Manager = "Manager";
    public const string Receptionist = "Receptionist";
    public const string ReadOnly = "ReadOnly";

    /// <summary>Day-to-day operations: appointments, customers. SuperAdmin is the platform
    /// operator and is included everywhere — it must never be less privileged than a tenant admin.</summary>
    public const string Staff = $"{SuperAdmin},{OrgAdmin},{Manager},{Receptionist}";

    /// <summary>Configuration: catalogue, knowledge base, agent settings, deletions.</summary>
    public const string Management = $"{SuperAdmin},{OrgAdmin},{Manager}";
}

public class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public bool Revoked { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public DateTime? FirstVisit { get; set; }
    public DateTime? LastVisit { get; set; }
    public int TotalVisits { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public class Service
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int DurationMinutes { get; set; } = 30;
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
    public bool IsEmergency { get; set; }
    public bool IsAvailable { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public class Product
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public static class AppointmentStatus
{
    public const string Scheduled = "Scheduled";
    public const string Confirmed = "Confirmed";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Missed = "Missed";
}

public class Appointment
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int CustomerId { get; set; }
    public int? ServiceId { get; set; }
    public int? StaffUserId { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string Status { get; set; } = AppointmentStatus.Scheduled;
    public string PaymentStatus { get; set; } = "Unpaid";
    public decimal Amount { get; set; }
    /// <summary>Where the work happens — required for field-service trades (plumbing,
    /// electrical, HVAC, locksmith, cleaning) whose technician travels to the caller.</summary>
    public string? ServiceAddress { get; set; }
    /// <summary>Caller-reported urgency captured during the call.</summary>
    public bool IsEmergency { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public bool IsDeleted { get; set; }

    // joined
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? ServiceName { get; set; }
    public string? StaffName { get; set; }
}

public class CallLog
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int? CustomerId { get; set; }
    public string? RetellCallId { get; set; }
    public string FromNumber { get; set; } = "";
    public string Direction { get; set; } = "Inbound";
    public string Status { get; set; } = "Completed"; // Completed | Missed | Transferred
    public int DurationSeconds { get; set; }
    public string? Transcript { get; set; }
    public string? RecordingUrl { get; set; }
    public string? Summary { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
    /// <summary>Set once the transcript has been mined for caller intent (transferred calls).</summary>
    public DateTime? IntentProcessedAt { get; set; }

    public string? CustomerName { get; set; }
}

/// <summary>A booking action inferred from a transferred call's transcript, awaiting
/// human confirmation. The AI never books directly on a transfer (SRS §19) — staff
/// review and confirm/dismiss these from the Calls screen.</summary>
public class CallActionSuggestion
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int CallLogId { get; set; }
    public string Action { get; set; } = "None"; // Book | Cancel | Reschedule | None
    public string Confidence { get; set; } = "low"; // low | medium | high
    public string? CustomerName { get; set; }
    public string? Phone { get; set; }
    public string? ServiceName { get; set; }
    public DateTime? StartAtLocal { get; set; }
    public string? Reasoning { get; set; }
    public string Status { get; set; } = "Pending"; // Pending | Confirmed | Dismissed | Failed
    public int? ResultAppointmentId { get; set; }
    public int? ResolvedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    // joined
    public DateTime? CallStartedAt { get; set; }
    public string? FromNumber { get; set; }
}

/// <summary>A single calendar date the business is closed — a public holiday, a shutdown day
/// or a staff day. Overrides the weekly business hours for that date: the AI refuses to offer
/// or book any time on it, and the closure is stated in the prompt and knowledge base so the
/// agent can tell callers why.</summary>
public class Holiday
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    /// <summary>Local calendar date in the tenant's timezone. Date only — the time
    /// component is always midnight and is never read.</summary>
    public DateTime Date { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public class KnowledgeBaseEntry
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string Category { get; set; } = "FAQ"; // BusinessInfo | FAQ | Policy | EmergencyRule | Custom
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public class AgentConfig
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string Voice { get; set; } = "nova";
    public string Language { get; set; } = "en-US";
    public string Greeting { get; set; } = "";
    public string? TransferNumber { get; set; }
    public string? RetellAgentId { get; set; }
    public string? RetellLlmId { get; set; }
    public string? RetellKnowledgeBaseId { get; set; }
    public string? RetellPhoneNumber { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public bool Enabled { get; set; } = true;
    public string EnabledToolsJson { get; set; } = "[]";
    public DateTime? ModifiedAt { get; set; }
}

public class TimelineEvent
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int CustomerId { get; set; }
    public string EventType { get; set; } = ""; // AiCall | AppointmentBooked | ...
    public string? Notes { get; set; }
    public string Source { get; set; } = "System"; // AI | User | System
    public int? UserId { get; set; }
    public DateTime OccurredAt { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int? UserId { get; set; }
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Platform-wide Retell AI connection — a single shared credential for the whole
/// platform (one Retell account), managed centrally by SuperAdmin. Every tenant's agent is
/// created under this one account, so the connection is not stored per-organization. Stored
/// as a single row; falls back to appsettings (Retell:*) when a field is left empty.</summary>
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

/// <summary>Platform-wide wording of the system prompt — the sections that are the same for every
/// tenant (how the agent speaks, the rules, the shape of a call, when it may call a tool). Edited
/// by SuperAdmin and stored as a single row, so one change re-tunes every tenant's agent.
/// A blank section falls back to <see cref="Services.PromptDefaults"/>: that is what makes
/// "restore the platform default" a matter of clearing the box rather than pasting text back.</summary>
public class PromptTemplate
{
    public int Id { get; set; }
    public string? Persona { get; set; }
    public string? CoreRules { get; set; }
    /// <summary>Call shape for businesses the customer comes to (clinics, salons, workshops).</summary>
    public string? ConversationGuide { get; set; }
    /// <summary>Call shape for trades that travel to the customer (plumbers, electricians, HVAC).</summary>
    public string? FieldServiceGuide { get; set; }
    public string? ToolPolicy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public int? ModifiedByUserId { get; set; }
}
