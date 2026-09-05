namespace AiReceptionist.Shared;

/// <summary>
/// The billing schema: pricing tiers, Stripe linkage, closed usage periods and mirrored invoices.
///
/// This file is compiled into <b>both</b> server applications (linked from each .csproj) and run
/// idempotently by each one's database initializer. It is shared rather than duplicated because
/// both apps write here: the super admin console owns tiers and manual payments, while the tenant
/// API owns the Stripe webhook, which must be able to write an invoice the moment Stripe calls —
/// a table that only the other application creates would make that a race on first deploy.
///
/// Mirrors <c>database/03_platform.sql</c>; keep both in step.
/// </summary>
public static class BillingSchema
{
    /// <summary>The number of AI minutes a call consumes. Each call is rounded up to a whole
    /// minute, which is how the tenant dashboard has always counted talk time — billing and the
    /// dashboard must never disagree about a number the customer is charged for.</summary>
    public const string MinutesExpression = "ISNULL(SUM(CEILING(DurationSeconds / 60.0)), 0)";

    /// <summary>Each statement runs as its own batch: a column added by an earlier statement is
    /// not visible to a later one within the same batch.</summary>
    public static readonly string[] Statements =
    [
        // -------------------------------------------------------------------
        // Account enable/disable. An organization with IsActive = 0 is suspended: its users
        // cannot sign in and its AI agent's live-call tools are refused.
        // -------------------------------------------------------------------
        "IF COL_LENGTH('Organizations','IsActive') IS NULL ALTER TABLE Organizations ADD IsActive BIT NOT NULL DEFAULT 1;",
        "IF COL_LENGTH('Organizations','SuspendedAt') IS NULL ALTER TABLE Organizations ADD SuspendedAt DATETIME2 NULL;",
        "IF COL_LENGTH('Organizations','SuspendedReason') IS NULL ALTER TABLE Organizations ADD SuspendedReason NVARCHAR(500) NULL;",

        // -------------------------------------------------------------------
        // One subscription per organization, and its payment history. Both predate Stripe (the
        // console recorded payments by hand) and are extended further down rather than replaced,
        // so an existing installation keeps the record it already has.
        // -------------------------------------------------------------------
        """
        IF OBJECT_ID('OrganizationSubscriptions') IS NULL
        CREATE TABLE OrganizationSubscriptions (
            Id INT IDENTITY PRIMARY KEY,
            OrganizationId INT NOT NULL UNIQUE REFERENCES Organizations(Id),
            PlanName NVARCHAR(100) NOT NULL DEFAULT 'Standard',
            BillingCycle NVARCHAR(20) NOT NULL DEFAULT 'Monthly',   -- Monthly | Yearly
            Amount DECIMAL(18,2) NOT NULL DEFAULT 0,
            Currency NVARCHAR(10) NOT NULL DEFAULT 'USD',
            StartedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            CurrentPeriodStart DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            CurrentPeriodEnd DATETIME2 NOT NULL,
            GraceDays INT NOT NULL DEFAULT 7,
            AutoSuspend BIT NOT NULL DEFAULT 1,
            IncludedMinutes INT NOT NULL DEFAULT 0,
            Notes NVARCHAR(MAX) NULL,
            ModifiedAt DATETIME2 NULL,
            ModifiedByUserId INT NULL
        );
        """,

        "IF COL_LENGTH('OrganizationSubscriptions','IncludedMinutes') IS NULL ALTER TABLE OrganizationSubscriptions ADD IncludedMinutes INT NOT NULL DEFAULT 0;",

        """
        IF OBJECT_ID('OrganizationPayments') IS NULL
        CREATE TABLE OrganizationPayments (
            Id INT IDENTITY PRIMARY KEY,
            OrganizationId INT NOT NULL REFERENCES Organizations(Id),
            Amount DECIMAL(18,2) NOT NULL,
            Currency NVARCHAR(10) NOT NULL DEFAULT 'USD',
            PaidAt DATETIME2 NOT NULL,
            PeriodStart DATETIME2 NOT NULL,
            PeriodEnd DATETIME2 NOT NULL,
            Method NVARCHAR(50) NOT NULL DEFAULT 'BankTransfer',
            Reference NVARCHAR(200) NULL,
            Notes NVARCHAR(MAX) NULL,
            RecordedByUserId INT NULL,
            CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
        );
        """,

        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OrgPayments_Org_PaidAt')
        CREATE INDEX IX_OrgPayments_Org_PaidAt ON OrganizationPayments(OrganizationId, PaidAt DESC);
        """,

        // -------------------------------------------------------------------
        // Restricting the agent without locking the customer out. Suspension
        // (Organizations.IsActive) stops sign-in too, which is the wrong tool when the point is to
        // stop the agent answering while the customer can still log in and settle the bill.
        // -------------------------------------------------------------------
        "IF COL_LENGTH('Organizations','AgentRestricted') IS NULL ALTER TABLE Organizations ADD AgentRestricted BIT NOT NULL DEFAULT 0;",
        "IF COL_LENGTH('Organizations','AgentRestrictedAt') IS NULL ALTER TABLE Organizations ADD AgentRestrictedAt DATETIME2 NULL;",
        "IF COL_LENGTH('Organizations','AgentRestrictedReason') IS NULL ALTER TABLE Organizations ADD AgentRestrictedReason NVARCHAR(500) NULL;",

        // -------------------------------------------------------------------
        // The tier catalogue. One row is one thing a customer can be put on; an organization's
        // subscription copies the numbers so that repricing a tier never silently re-bills
        // everyone already on it.
        // -------------------------------------------------------------------
        """
        IF OBJECT_ID('PricingPlans') IS NULL
        CREATE TABLE PricingPlans (
            Id INT IDENTITY PRIMARY KEY,
            Name NVARCHAR(100) NOT NULL,
            Description NVARCHAR(500) NULL,
            Currency NVARCHAR(10) NOT NULL DEFAULT 'USD',
            Amount DECIMAL(18,2) NOT NULL DEFAULT 0,
            BillingCycle NVARCHAR(20) NOT NULL DEFAULT 'Monthly',   -- Monthly | Yearly
            IncludedMinutes INT NOT NULL DEFAULT 0,
            -- Charged per minute past IncludedMinutes, on the following invoice. Four decimals
            -- because a per-minute rate is commonly fractions of a cent.
            OverageRatePerMinute DECIMAL(18,4) NOT NULL DEFAULT 0,
            StripeProductId NVARCHAR(100) NULL,
            StripePriceId NVARCHAR(100) NULL,
            SortOrder INT NOT NULL DEFAULT 0,
            IsActive BIT NOT NULL DEFAULT 1,
            IsDeleted BIT NOT NULL DEFAULT 0,
            CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            ModifiedAt DATETIME2 NULL,
            ModifiedByUserId INT NULL
        );
        """,

        // -------------------------------------------------------------------
        // Per-organization subscription: the tier it is on, the numbers actually in force (which
        // may be overridden per customer), and its Stripe linkage.
        // -------------------------------------------------------------------
        "IF COL_LENGTH('OrganizationSubscriptions','PlanId') IS NULL ALTER TABLE OrganizationSubscriptions ADD PlanId INT NULL;",
        "IF COL_LENGTH('OrganizationSubscriptions','OverageRatePerMinute') IS NULL ALTER TABLE OrganizationSubscriptions ADD OverageRatePerMinute DECIMAL(18,4) NOT NULL DEFAULT 0;",
        "IF COL_LENGTH('OrganizationSubscriptions','StripeCustomerId') IS NULL ALTER TABLE OrganizationSubscriptions ADD StripeCustomerId NVARCHAR(100) NULL;",
        "IF COL_LENGTH('OrganizationSubscriptions','StripeSubscriptionId') IS NULL ALTER TABLE OrganizationSubscriptions ADD StripeSubscriptionId NVARCHAR(100) NULL;",
        "IF COL_LENGTH('OrganizationSubscriptions','StripeStatus') IS NULL ALTER TABLE OrganizationSubscriptions ADD StripeStatus NVARCHAR(40) NULL;",

        // Overage that has been totalled but not yet charged. It sits here between the period
        // closing and the next invoice being raised, which is exactly what "added to your next
        // bill" means; it is cleared once it reaches a Stripe invoice.
        "IF COL_LENGTH('OrganizationSubscriptions','PendingOverageMinutes') IS NULL ALTER TABLE OrganizationSubscriptions ADD PendingOverageMinutes INT NOT NULL DEFAULT 0;",
        "IF COL_LENGTH('OrganizationSubscriptions','PendingOverageAmount') IS NULL ALTER TABLE OrganizationSubscriptions ADD PendingOverageAmount DECIMAL(18,2) NOT NULL DEFAULT 0;",

        // A tier the customer has chosen to move to at the end of the period they are in. It is
        // parked here rather than applied on the spot because the allowance is what the closing
        // period is measured against: swapping it mid-period would re-measure minutes already
        // spent and wipe out an overrun the customer has genuinely incurred.
        "IF COL_LENGTH('OrganizationSubscriptions','PendingPlanId') IS NULL ALTER TABLE OrganizationSubscriptions ADD PendingPlanId INT NULL;",

        // -------------------------------------------------------------------
        // One row per billing period that has closed: what the plan allowed, what was used, and
        // what the overrun came to. This is the evidence behind every charge — the tenant's
        // billing page and the console both read it rather than recomputing from call logs,
        // so a customer's explanation of an old charge cannot drift as their plan changes.
        // -------------------------------------------------------------------
        """
        IF OBJECT_ID('OrganizationUsagePeriods') IS NULL
        CREATE TABLE OrganizationUsagePeriods (
            Id INT IDENTITY PRIMARY KEY,
            OrganizationId INT NOT NULL REFERENCES Organizations(Id),
            PeriodStart DATETIME2 NOT NULL,
            PeriodEnd DATETIME2 NOT NULL,
            PlanName NVARCHAR(100) NOT NULL DEFAULT '',
            BaseAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
            Currency NVARCHAR(10) NOT NULL DEFAULT 'USD',
            IncludedMinutes INT NOT NULL DEFAULT 0,
            MinutesUsed INT NOT NULL DEFAULT 0,
            OverageMinutes INT NOT NULL DEFAULT 0,
            OverageRatePerMinute DECIMAL(18,4) NOT NULL DEFAULT 0,
            OverageAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
            -- Pending = totalled, waiting for the next invoice. Billed = it reached one (or there
            -- was nothing to charge). Never re-opened: a closed period is a historical fact.
            Status NVARCHAR(20) NOT NULL DEFAULT 'Pending',
            StripeInvoiceId NVARCHAR(100) NULL,
            ClosedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
        );
        """,

        // Closing a period is driven by a worker AND by two Stripe webhooks, so it can be
        // attempted several times over. This index is what makes the second attempt a no-op.
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_OrgUsagePeriods_Org_End')
        CREATE UNIQUE INDEX UX_OrgUsagePeriods_Org_End
        ON OrganizationUsagePeriods(OrganizationId, PeriodEnd);
        """,

        // -------------------------------------------------------------------
        // Stripe invoices, mirrored locally. Kept so the console and the tenant's billing page can
        // show what was charged and why without a Stripe round-trip on every page load, and so the
        // history survives if the Stripe account is ever detached.
        // -------------------------------------------------------------------
        """
        IF OBJECT_ID('OrganizationInvoices') IS NULL
        CREATE TABLE OrganizationInvoices (
            Id INT IDENTITY PRIMARY KEY,
            OrganizationId INT NOT NULL REFERENCES Organizations(Id),
            StripeInvoiceId NVARCHAR(100) NOT NULL UNIQUE,
            Number NVARCHAR(100) NULL,
            Status NVARCHAR(40) NOT NULL DEFAULT 'draft',
            Currency NVARCHAR(10) NOT NULL DEFAULT 'USD',
            Subtotal DECIMAL(18,2) NOT NULL DEFAULT 0,
            Total DECIMAL(18,2) NOT NULL DEFAULT 0,
            AmountPaid DECIMAL(18,2) NOT NULL DEFAULT 0,
            AmountDue DECIMAL(18,2) NOT NULL DEFAULT 0,
            PeriodStart DATETIME2 NULL,
            PeriodEnd DATETIME2 NULL,
            HostedInvoiceUrl NVARCHAR(1000) NULL,
            InvoicePdfUrl NVARCHAR(1000) NULL,
            IssuedAt DATETIME2 NULL,
            PaidAt DATETIME2 NULL,
            CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            UpdatedAt DATETIME2 NULL
        );
        """,

        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OrgInvoices_Org_Issued')
        CREATE INDEX IX_OrgInvoices_Org_Issued ON OrganizationInvoices(OrganizationId, IssuedAt DESC);
        """,

        // The line items are what turns "£71.04" into "your plan, plus 142 minutes over it".
        """
        IF OBJECT_ID('OrganizationInvoiceLines') IS NULL
        CREATE TABLE OrganizationInvoiceLines (
            Id INT IDENTITY PRIMARY KEY,
            InvoiceId INT NOT NULL REFERENCES OrganizationInvoices(Id),
            Description NVARCHAR(500) NOT NULL DEFAULT '',
            Quantity INT NOT NULL DEFAULT 1,
            Amount DECIMAL(18,2) NOT NULL DEFAULT 0,
            Kind NVARCHAR(20) NOT NULL DEFAULT 'Other',   -- Subscription | Overage | Other
            SortOrder INT NOT NULL DEFAULT 0
        );
        """,

        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OrgInvoiceLines_Invoice')
        CREATE INDEX IX_OrgInvoiceLines_Invoice ON OrganizationInvoiceLines(InvoiceId, SortOrder);
        """,

        // -------------------------------------------------------------------
        // Payments gain a provenance. Manual entries (the console's existing "record a payment")
        // and Stripe-collected ones live in one history so a customer's payment record is whole.
        // -------------------------------------------------------------------
        "IF COL_LENGTH('OrganizationPayments','Source') IS NULL ALTER TABLE OrganizationPayments ADD Source NVARCHAR(20) NOT NULL DEFAULT 'Manual';",
        "IF COL_LENGTH('OrganizationPayments','StripeInvoiceId') IS NULL ALTER TABLE OrganizationPayments ADD StripeInvoiceId NVARCHAR(100) NULL;",
        "IF COL_LENGTH('OrganizationPayments','StripePaymentIntentId') IS NULL ALTER TABLE OrganizationPayments ADD StripePaymentIntentId NVARCHAR(100) NULL;",

        // Stripe re-sends a webhook until it is acknowledged, so the same paid invoice can arrive
        // repeatedly. A filtered unique index makes the duplicate insert fail rather than credit
        // the customer twice; manual payments (NULL) are unaffected.
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_OrgPayments_StripeInvoice')
        CREATE UNIQUE INDEX UX_OrgPayments_StripeInvoice ON OrganizationPayments(StripeInvoiceId)
        WHERE StripeInvoiceId IS NOT NULL;
        """,

        // -------------------------------------------------------------------
        // Webhook deliveries seen. Stripe guarantees at-least-once delivery, so an event id is
        // claimed before it is acted on and the handler is skipped if it has been done already.
        //
        // "Already" means *successfully*. A claim used to be permanent the moment it was taken,
        // which meant a handler that threw — or a process that died holding the claim — refused
        // every redelivery of an event that had never actually been applied. For an invoice.paid
        // that is a collected payment the platform never records.
        // -------------------------------------------------------------------
        """
        IF OBJECT_ID('StripeWebhookEvents') IS NULL
        CREATE TABLE StripeWebhookEvents (
            Id NVARCHAR(100) NOT NULL PRIMARY KEY,
            Type NVARCHAR(100) NOT NULL,
            ReceivedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
            ProcessedAt DATETIME2 NULL,
            Error NVARCHAR(1000) NULL
        );
        """,

        // How many times this event has been picked up, and when the current attempt took the
        // claim. ClaimedAt is what makes an abandoned attempt recoverable: a row still unprocessed
        // long after it was claimed belongs to a process that is no longer running.
        "IF COL_LENGTH('StripeWebhookEvents','Attempts') IS NULL ALTER TABLE StripeWebhookEvents ADD Attempts INT NOT NULL DEFAULT 0;",
        "IF COL_LENGTH('StripeWebhookEvents','ClaimedAt') IS NULL ALTER TABLE StripeWebhookEvents ADD ClaimedAt DATETIME2 NULL;",
        "IF COL_LENGTH('StripeWebhookEvents','Abandoned') IS NULL ALTER TABLE StripeWebhookEvents ADD Abandoned BIT NOT NULL DEFAULT 0;",

        // Existing rows predate the columns above. Anything already processed cleanly is backfilled
        // as a finished single attempt so it is never replayed; anything left in error keeps
        // Attempts = 0 and becomes eligible for the retry path on its next delivery.
        """
        UPDATE StripeWebhookEvents
        SET Attempts = 1, ClaimedAt = ReceivedAt
        WHERE Attempts = 0 AND ProcessedAt IS NOT NULL AND Error IS NULL;
        """,

        // The reconciliation sweep and the operator's "what is stuck" view both read this.
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StripeWebhookEvents_Unfinished')
        CREATE INDEX IX_StripeWebhookEvents_Unfinished
        ON StripeWebhookEvents (ProcessedAt, Abandoned) INCLUDE (Type, Error, Attempts, ReceivedAt);
        """,
    ];
}
