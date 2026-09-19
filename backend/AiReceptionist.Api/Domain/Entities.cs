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

    /// <summary>A free trial granted from the super admin console, for however many days the
    /// operator chose. While it runs the AI agent answers without a plan or a payment; once
    /// <see cref="TrialEndsAt"/> passes it stops, unless a plan has been taken out by then.
    /// Both are null for an organization that has never been given one.</summary>
    public DateTime? TrialStartedAt { get; set; }
    public DateTime? TrialEndsAt { get; set; }

    /// <summary>The trial is running right now. A null end date is not a trial, so this is false
    /// for every organization that was never given one.</summary>
    public bool IsOnTrial => TrialEndsAt > DateTime.UtcNow;

    /// <summary>A trial was given and has run out. The distinction matters: this account has been
    /// tried and lapsed, which is a different thing to say than "no plan has ever been taken
    /// out".</summary>
    public bool TrialExpired => TrialEndsAt is not null && !IsOnTrial;

    /// <summary>Days left on the trial, rounded up so the last part-day still counts as one.</summary>
    public int TrialDaysRemaining =>
        IsOnTrial ? (int)Math.Ceiling((TrialEndsAt!.Value - DateTime.UtcNow).TotalDays) : 0;
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
    /// <summary>Whether the browser was asked to keep this cookie past the end of the session —
    /// the "Keep me signed in" choice. Carried across every rotation, because a refresh must not
    /// quietly promote a session that was meant to end when the browser closed.</summary>
    public bool Persistent { get; set; }
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
    /// <summary>Which member of the roster is handling this appointment. This is what makes two
    /// bookings in the same hour legitimate — one per employee. Null on bookings taken before the
    /// organization had a roster, and on organizations that never build one.</summary>
    public int? EmployeeId { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string Status { get; set; } = AppointmentStatus.Scheduled;
    public string PaymentStatus { get; set; } = "Unpaid";
    public decimal Amount { get; set; }
    /// <summary>Where the work happens — required for field-service trades (plumbing,
    /// electrical, HVAC, locksmith, cleaning) whose technician travels to the caller.</summary>
    public string? ServiceAddress { get; set; }
    /// <summary>How <see cref="ServiceAddress"/> fared against the branches' coverage areas
    /// (see <see cref="BusinessLocation"/>): "Covered", or "OutOfArea" for a booking taken outside
    /// the radius that staff still have to ring back and confirm. Null when the organization has
    /// configured no coverage areas, or when no address was given — the ordinary case, and the
    /// reason nothing about existing bookings changes.</summary>
    public string? AreaStatus { get; set; }

    /// <summary>What OpenStreetMap made of the address, as JSON: the place it resolved to, its
    /// coordinates, which branch covers it, how far away that is, and any landmark within walking
    /// distance. Kept beside <see cref="ServiceAddress"/> rather than replacing it, so the words
    /// the caller actually used are never lost behind a tidied-up version of them.</summary>
    public string? ServiceLocationJson { get; set; }

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
    public string? EmployeeName { get; set; }
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

/// <summary>
/// One branch of the business and the area it will travel to from it.
///
/// The problem this solves is a caller giving an address nobody can serve — either because it is
/// not a real place or because it is an hour outside the nearest branch. A booking taken on one
/// costs a wasted visit, so the address is checked against these areas while the caller is still
/// on the phone (see <c>IServiceAreaService</c>).
///
/// An organization with no rows here has no coverage rules at all and every address is accepted,
/// exactly as before this existed.
/// </summary>
public class BusinessLocation
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    /// <summary>What the owner calls this branch — "Manhattan", "North depot". Defaults to the
    /// city when they do not name it.</summary>
    public string Name { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2, upper case. Narrows the geocoder to the countries the business
    /// actually works in, which makes a lookup both faster and much harder to confuse — there is a
    /// Manchester in England and another in New Hampshire.</summary>
    public string CountryCode { get; set; } = "";
    public string CountryName { get; set; } = "";
    public string City { get; set; } = "";
    /// <summary>The branch's centre, resolved server-side from the city as it is saved. Never
    /// taken from the browser: it is what every coverage decision is measured from, so a client
    /// that could set it could put itself inside anyone's area.</summary>
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>How far from that centre this branch will travel. Ignored while
    /// <see cref="CoversEntireCity"/> is set.</summary>
    public double CoverageRadiusMiles { get; set; } = 10;
    /// <summary>The rectangle OpenStreetMap draws around this city, stored when the branch is
    /// saved. This — not the city's name — is what "covers the whole city" is measured against:
    /// an address in London reports its city as "City of Westminster", so no amount of name
    /// matching would ever put the two together, while both sit plainly inside one box.
    /// Null on a branch saved before this existed, or one the geocoder gave no usable box for;
    /// the name comparison in <see cref="Common.Geo.SameCity"/> covers those.</summary>
    public double? BoundsSouth { get; set; }
    public double? BoundsNorth { get; set; }
    public double? BoundsWest { get; set; }
    public double? BoundsEast { get; set; }

    /// <summary>Anywhere in the city counts as covered, however far out it is — the answer for a
    /// business whose patch is administrative rather than a circle on a map.</summary>
    public bool CoversEntireCity { get; set; }
    /// <summary>Off for now — a branch that is shut, or not open yet. Never used to cover an
    /// address, but kept so past bookings still say where they came from.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public bool IsDeleted { get; set; }
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

/// <summary>A person on the organization's roster — the barber, the technician, the dentist.
///
/// The roster is what lets one business take two appointments in the same hour: capacity for a
/// slot is the number of employees on duty for it and not already booked, rather than one number
/// for the whole business. An organization with an empty roster keeps the older behaviour
/// (<see cref="Organization.MaxConcurrentAppointments"/>), so this is additive for existing tenants.
///
/// Deliberately not a <see cref="User"/>: most employees never sign in, and the people who do sign
/// in (the receptionist, the owner's accountant) are usually not people a caller can be booked with.</summary>
public class Employee
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    /// <summary>This person's own weekly hours, in the same format as
    /// <see cref="Organization.BusinessHoursJson"/>. Null means they simply work the business
    /// hours. Their effective window is always the intersection of the two, so an employee can
    /// never be booked outside the hours the business is open.</summary>
    public string? WorkingHoursJson { get; set; }
    /// <summary>Off the roster for now — left, or on open-ended leave. An inactive employee is
    /// never counted as available and is never assigned a booking.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public bool IsDeleted { get; set; }

    // joined
    /// <summary>Appointments still to come that are assigned to this person. Shown on the team
    /// screen so nobody is taken off the roster out from under a caller already booked with them.</summary>
    public int UpcomingAppointments { get; set; }
}

/// <summary>A stretch of days one employee is away — holiday, sick leave, training. Inclusive of
/// both ends, in the tenant's local calendar. The AI treats the person as absent on those dates:
/// if nobody else is on duty the slot is never offered, and nothing is booked into it.</summary>
public class EmployeeTimeOff
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>Local calendar date; the time component is always midnight and is never read.</summary>
    public DateTime StartDate { get; set; }
    /// <summary>Local calendar date, inclusive — a single day off carries the same value as
    /// <see cref="StartDate"/>.</summary>
    public DateTime EndDate { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsDeleted { get; set; }

    // joined
    public string? EmployeeName { get; set; }
}

/// <summary>
/// One browser that has asked to be told when something happens — a phone on the counter, a
/// laptop at home. Registered per user per device, so the owner who signs in on two machines is
/// reached on both, and a device is pruned the moment the push service says it is gone.
///
/// The three opaque strings are the whole of a Web Push subscription: an endpoint URL at the
/// browser vendor's push service, and two keys the message is encrypted to. Nothing here is a
/// credential of ours and none of it can be used to push to anyone else's device.
/// </summary>
public class PushDevice
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public int UserId { get; set; }
    /// <summary>The push service URL for this device. Unique — re-subscribing the same browser
    /// returns the same endpoint, so it is what makes registration idempotent.</summary>
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    /// <summary>What the device called itself, so a person with several can tell them apart when
    /// turning one off.</summary>
    public string? Label { get; set; }
    /// <summary>Only send this device the things that cannot wait — emergencies and same-day
    /// bookings. Someone who watches the dashboard all day does not want a buzz per booking.</summary>
    public bool UrgentOnly { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
    /// <summary>Consecutive delivery failures. A push service answering 404/410 means the
    /// subscription is dead and the row is removed outright; this counts the softer failures.</summary>
    public int FailureCount { get; set; }
    public bool IsDeleted { get; set; }
}

public static class AlertKind
{
    public const string AppointmentBooked = "AppointmentBooked";
    public const string AppointmentCancelled = "AppointmentCancelled";
    public const string AppointmentRescheduled = "AppointmentRescheduled";
}

public static class AlertSeverity
{
    /// <summary>Worth knowing about. Notified once, and never chased.</summary>
    public const string Info = "Info";
    /// <summary>Cannot wait — an emergency, or something happening today. Notified with the
    /// notification pinned on screen, and repeated until a human acknowledges it.</summary>
    public const string Urgent = "Urgent";
}

/// <summary>
/// Something that happened while nobody was looking, and the record of whether anybody has since
/// looked at it.
///
/// A notification on its own is not enough for an emergency: it can arrive while the phone is face
/// down and be gone by the time it is picked up. So an urgent alert is a small piece of state
/// rather than an event — it stays unacknowledged, shows on the dashboard, and is re-sent on a
/// timer until someone actually opens it. Acknowledging is the only thing that stops it.
/// </summary>
public class Alert
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public string Kind { get; set; } = AlertKind.AppointmentBooked;
    public string Severity { get; set; } = AlertSeverity.Info;
    /// <summary>The notification's heading — short, because a phone truncates it.</summary>
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Where tapping the notification lands, relative to the app root.</summary>
    public string? Url { get; set; }
    public int? AppointmentId { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public int? AcknowledgedByUserId { get; set; }
    /// <summary>How many times this has been pushed, including the first. Capped, so a business
    /// that closes for the night is not buzzed all night by something nobody will action.</summary>
    public int NotifiedCount { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    // joined
    public string? AcknowledgedByName { get; set; }
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
    public string Voice { get; set; } = RetellVoices.Default.Id;
    public string Language { get; set; } = "en-US";
    public string Greeting { get; set; } = "";
    public string? TransferNumber { get; set; }
    public string? RetellAgentId { get; set; }
    public string? RetellLlmId { get; set; }
    public string? RetellKnowledgeBaseId { get; set; }
    public string? RetellPhoneNumber { get; set; }

    // Where a disconnect parks the Retell ids it just detached. Disconnecting deliberately leaves
    // the resources alive on the shared Retell account so it stays reversible, but that means the
    // next Connect would otherwise create a second agent, LLM and knowledge base and abandon the
    // first — one orphaned set per connect/disconnect cycle. Connect deletes whatever is parked
    // here before it creates anything, which is what keeps one tenant to one set of resources.
    public string? DetachedRetellAgentId { get; set; }
    public string? DetachedRetellLlmId { get; set; }
    public string? DetachedRetellKnowledgeBaseId { get; set; }

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
