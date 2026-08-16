-- Platform + billing schema additions for AiDB.
--
-- Both server applications run these statements automatically at startup, idempotently, from the
-- shared source file backend/Shared/BillingSchema.cs (AiReceptionist.Api's DbInitializer and
-- AiReceptionist.SuperAdmin's PlatformDbInitializer). This script is the readable mirror of that
-- file — keep the two in step. Safe to re-run.
USE AiDB;
GO

-- ---------------------------------------------------------------------------
-- Account enable/disable. An organization with IsActive = 0 is suspended: its
-- users cannot sign in and its AI agent's live-call tools are refused, so a
-- suspended tenant stops costing money the moment it is disabled.
-- ---------------------------------------------------------------------------
IF COL_LENGTH('Organizations', 'IsActive') IS NULL
    ALTER TABLE Organizations ADD IsActive BIT NOT NULL DEFAULT 1;
GO
IF COL_LENGTH('Organizations', 'SuspendedAt') IS NULL
    ALTER TABLE Organizations ADD SuspendedAt DATETIME2 NULL;
GO
IF COL_LENGTH('Organizations', 'SuspendedReason') IS NULL
    ALTER TABLE Organizations ADD SuspendedReason NVARCHAR(500) NULL;
GO

-- ---------------------------------------------------------------------------
-- Restricting the agent without locking the customer out. Suspension above stops
-- sign-in too, which is the wrong tool when the point is to stop the agent
-- answering while the customer can still log in and settle the bill.
-- ---------------------------------------------------------------------------
IF COL_LENGTH('Organizations', 'AgentRestricted') IS NULL
    ALTER TABLE Organizations ADD AgentRestricted BIT NOT NULL DEFAULT 0;
GO
IF COL_LENGTH('Organizations', 'AgentRestrictedAt') IS NULL
    ALTER TABLE Organizations ADD AgentRestrictedAt DATETIME2 NULL;
GO
IF COL_LENGTH('Organizations', 'AgentRestrictedReason') IS NULL
    ALTER TABLE Organizations ADD AgentRestrictedReason NVARCHAR(500) NULL;
GO

-- ---------------------------------------------------------------------------
-- One subscription per organization. The numbers in force live here rather than
-- being read through to the tier, so repricing a tier never silently re-bills
-- everyone already on it.
-- ---------------------------------------------------------------------------
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
    -- Days past CurrentPeriodEnd before the account counts as overdue.
    GraceDays INT NOT NULL DEFAULT 7,
    -- When set, the sweep disables the account once the grace period lapses.
    AutoSuspend BIT NOT NULL DEFAULT 1,
    -- AI talk-time the plan includes per billing period. 0 means "not metered": the
    -- tenant dashboard then reports usage without a remaining balance.
    IncludedMinutes INT NOT NULL DEFAULT 0,
    Notes NVARCHAR(MAX) NULL,
    ModifiedAt DATETIME2 NULL,
    ModifiedByUserId INT NULL
);
GO

IF COL_LENGTH('OrganizationSubscriptions', 'IncludedMinutes') IS NULL
    ALTER TABLE OrganizationSubscriptions ADD IncludedMinutes INT NOT NULL DEFAULT 0;
GO

-- ---------------------------------------------------------------------------
-- Payment history. Append-only: each row is one payment against one billing
-- period, whether recorded by hand in the console or collected by Stripe.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('OrganizationPayments') IS NULL
CREATE TABLE OrganizationPayments (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    Amount DECIMAL(18,2) NOT NULL,
    Currency NVARCHAR(10) NOT NULL DEFAULT 'USD',
    PaidAt DATETIME2 NOT NULL,
    PeriodStart DATETIME2 NOT NULL,
    PeriodEnd DATETIME2 NOT NULL,
    Method NVARCHAR(50) NOT NULL DEFAULT 'BankTransfer',  -- BankTransfer | Card | Cash | Cheque | Other
    Reference NVARCHAR(200) NULL,
    Notes NVARCHAR(MAX) NULL,
    RecordedByUserId INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OrgPayments_Org_PaidAt')
CREATE INDEX IX_OrgPayments_Org_PaidAt ON OrganizationPayments(OrganizationId, PaidAt DESC);
GO

-- ---------------------------------------------------------------------------
-- The tier catalogue. One row is one thing a customer can be put on.
-- ---------------------------------------------------------------------------
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
GO

-- ---------------------------------------------------------------------------
-- Subscription: tier assignment, overage rate and Stripe linkage.
-- ---------------------------------------------------------------------------
IF COL_LENGTH('OrganizationSubscriptions', 'PlanId') IS NULL
    ALTER TABLE OrganizationSubscriptions ADD PlanId INT NULL;
GO
IF COL_LENGTH('OrganizationSubscriptions', 'OverageRatePerMinute') IS NULL
    ALTER TABLE OrganizationSubscriptions ADD OverageRatePerMinute DECIMAL(18,4) NOT NULL DEFAULT 0;
GO
IF COL_LENGTH('OrganizationSubscriptions', 'StripeCustomerId') IS NULL
    ALTER TABLE OrganizationSubscriptions ADD StripeCustomerId NVARCHAR(100) NULL;
GO
IF COL_LENGTH('OrganizationSubscriptions', 'StripeSubscriptionId') IS NULL
    ALTER TABLE OrganizationSubscriptions ADD StripeSubscriptionId NVARCHAR(100) NULL;
GO
IF COL_LENGTH('OrganizationSubscriptions', 'StripeStatus') IS NULL
    ALTER TABLE OrganizationSubscriptions ADD StripeStatus NVARCHAR(40) NULL;
GO

-- Overage that has been totalled but not yet charged. It sits here between the period
-- closing and the next invoice being raised, which is exactly what "added to your next
-- bill" means; it is cleared once it reaches a Stripe invoice.
IF COL_LENGTH('OrganizationSubscriptions', 'PendingOverageMinutes') IS NULL
    ALTER TABLE OrganizationSubscriptions ADD PendingOverageMinutes INT NOT NULL DEFAULT 0;
GO
IF COL_LENGTH('OrganizationSubscriptions', 'PendingOverageAmount') IS NULL
    ALTER TABLE OrganizationSubscriptions ADD PendingOverageAmount DECIMAL(18,2) NOT NULL DEFAULT 0;
GO

-- ---------------------------------------------------------------------------
-- One row per billing period that has closed: what the plan allowed, what was
-- used, and what the overrun came to. This is the evidence behind every charge.
-- ---------------------------------------------------------------------------
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
GO

-- Closing a period is driven by a worker AND by two Stripe webhooks, so it can be
-- attempted several times over. This index is what makes the second attempt a no-op.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_OrgUsagePeriods_Org_End')
CREATE UNIQUE INDEX UX_OrgUsagePeriods_Org_End ON OrganizationUsagePeriods(OrganizationId, PeriodEnd);
GO

-- ---------------------------------------------------------------------------
-- Stripe invoices, mirrored locally, with their line items — what turns a total
-- into "your plan, plus the minutes you went over it".
-- ---------------------------------------------------------------------------
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
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OrgInvoices_Org_Issued')
CREATE INDEX IX_OrgInvoices_Org_Issued ON OrganizationInvoices(OrganizationId, IssuedAt DESC);
GO

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
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_OrgInvoiceLines_Invoice')
CREATE INDEX IX_OrgInvoiceLines_Invoice ON OrganizationInvoiceLines(InvoiceId, SortOrder);
GO

-- ---------------------------------------------------------------------------
-- Payments gain a provenance, so manual and Stripe-collected entries share one
-- history.
-- ---------------------------------------------------------------------------
IF COL_LENGTH('OrganizationPayments', 'Source') IS NULL
    ALTER TABLE OrganizationPayments ADD Source NVARCHAR(20) NOT NULL DEFAULT 'Manual';
GO
IF COL_LENGTH('OrganizationPayments', 'StripeInvoiceId') IS NULL
    ALTER TABLE OrganizationPayments ADD StripeInvoiceId NVARCHAR(100) NULL;
GO
IF COL_LENGTH('OrganizationPayments', 'StripePaymentIntentId') IS NULL
    ALTER TABLE OrganizationPayments ADD StripePaymentIntentId NVARCHAR(100) NULL;
GO

-- Stripe re-sends a webhook until it is acknowledged, so the same paid invoice can arrive
-- repeatedly. A filtered unique index makes the duplicate insert fail rather than credit
-- the customer twice; manual payments (NULL) are unaffected.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_OrgPayments_StripeInvoice')
CREATE UNIQUE INDEX UX_OrgPayments_StripeInvoice ON OrganizationPayments(StripeInvoiceId)
WHERE StripeInvoiceId IS NOT NULL;
GO

-- ---------------------------------------------------------------------------
-- Webhook deliveries seen. Stripe guarantees at-least-once delivery, so an event
-- id is claimed before it is acted on and the handler skipped if already done.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('StripeWebhookEvents') IS NULL
CREATE TABLE StripeWebhookEvents (
    Id NVARCHAR(100) NOT NULL PRIMARY KEY,
    Type NVARCHAR(100) NOT NULL,
    ReceivedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    ProcessedAt DATETIME2 NULL,
    Error NVARCHAR(1000) NULL
);
GO
