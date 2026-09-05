-- AI Receptionist Platform — multi-tenant schema for AiDB.
-- Every tenant-owned table carries OrganizationId (SRS §4) and uses soft delete (SRS §21).
-- NOTE: the API also runs this schema automatically at startup (DbInitializer), idempotently.
USE AiDB;
GO

IF OBJECT_ID('Organizations') IS NULL
CREATE TABLE Organizations (
    Id INT IDENTITY PRIMARY KEY,
    Name NVARCHAR(200) NOT NULL,
    Industry NVARCHAR(100) NOT NULL DEFAULT '',
    Logo NVARCHAR(500) NULL,
    Address NVARCHAR(500) NULL,
    Phone NVARCHAR(50) NULL,
    Email NVARCHAR(256) NULL,
    Currency NVARCHAR(10) NOT NULL DEFAULT 'USD',
    TaxRate DECIMAL(5,2) NOT NULL DEFAULT 0,
    Timezone NVARCHAR(100) NOT NULL DEFAULT 'UTC',
    BusinessHoursJson NVARCHAR(MAX) NULL,
    MaxConcurrentAppointments INT NOT NULL DEFAULT 1,
    ProductsEnabled BIT NOT NULL DEFAULT 1,
    OnboardingCompleted BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0,
    -- Account enable/disable, owned by the super admin console (see 03_platform.sql).
    IsActive BIT NOT NULL DEFAULT 1,
    SuspendedAt DATETIME2 NULL,
    SuspendedReason NVARCHAR(500) NULL
);
GO

IF OBJECT_ID('Users') IS NULL
CREATE TABLE Users (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    FullName NVARCHAR(200) NOT NULL,
    Email NVARCHAR(256) NOT NULL,
    PasswordHash NVARCHAR(500) NOT NULL,
    Role NVARCHAR(50) NOT NULL DEFAULT 'ReadOnly',   -- SuperAdmin | OrgAdmin | Manager | Receptionist | ReadOnly
    EmailVerified BIT NOT NULL DEFAULT 0,
    MfaEnabled BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Users_Email')
CREATE UNIQUE INDEX IX_Users_Email ON Users(Email) WHERE IsDeleted = 0;
GO

IF OBJECT_ID('RefreshTokens') IS NULL
CREATE TABLE RefreshTokens (
    Id INT IDENTITY PRIMARY KEY,
    UserId INT NOT NULL REFERENCES Users(Id),
    Token NVARCHAR(500) NOT NULL,
    ExpiresAt DATETIME2 NOT NULL,
    Revoked BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

IF OBJECT_ID('Customers') IS NULL
CREATE TABLE Customers (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    Name NVARCHAR(200) NOT NULL,
    Phone NVARCHAR(50) NOT NULL,
    Email NVARCHAR(256) NULL,
    Address NVARCHAR(500) NULL,
    Notes NVARCHAR(MAX) NULL,
    FirstVisit DATETIME2 NULL,
    LastVisit DATETIME2 NULL,
    TotalVisits INT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Customers_Org_Phone')
CREATE INDEX IX_Customers_Org_Phone ON Customers(OrganizationId, Phone);
GO

IF OBJECT_ID('Services') IS NULL
CREATE TABLE Services (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    Name NVARCHAR(200) NOT NULL,
    Description NVARCHAR(MAX) NULL,
    DurationMinutes INT NOT NULL DEFAULT 30,
    MinPrice DECIMAL(18,2) NOT NULL DEFAULT 0,
    MaxPrice DECIMAL(18,2) NOT NULL DEFAULT 0,
    IsEmergency BIT NOT NULL DEFAULT 0,
    IsAvailable BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO

IF OBJECT_ID('Products') IS NULL
CREATE TABLE Products (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    Name NVARCHAR(200) NOT NULL,
    Sku NVARCHAR(100) NULL,
    Description NVARCHAR(MAX) NULL,
    Category NVARCHAR(100) NULL,
    Price DECIMAL(18,2) NOT NULL DEFAULT 0,
    Quantity INT NOT NULL DEFAULT 0,
    IsAvailable BIT NOT NULL DEFAULT 1,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO

IF OBJECT_ID('Appointments') IS NULL
CREATE TABLE Appointments (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    CustomerId INT NOT NULL REFERENCES Customers(Id),
    ServiceId INT NULL REFERENCES Services(Id),
    StaffUserId INT NULL REFERENCES Users(Id),
    StartAt DATETIME2 NOT NULL,
    EndAt DATETIME2 NOT NULL,
    Status NVARCHAR(30) NOT NULL DEFAULT 'Scheduled', -- Scheduled | Confirmed | Completed | Cancelled | Missed
    PaymentStatus NVARCHAR(30) NOT NULL DEFAULT 'Unpaid',
    Amount DECIMAL(18,2) NOT NULL DEFAULT 0,
    ServiceAddress NVARCHAR(500) NULL,     -- where the technician goes (field-service trades)
    IsEmergency BIT NOT NULL DEFAULT 0,
    Notes NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    ModifiedAt DATETIME2 NULL,
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Appointments_Org_Start')
CREATE INDEX IX_Appointments_Org_Start ON Appointments(OrganizationId, StartAt) INCLUDE (Status);
GO

-- Who is handling the appointment (see the Employees table below). Nullable: bookings taken
-- before the roster existed carry no assignment, and the availability rules account for them.
IF COL_LENGTH('Appointments','EmployeeId') IS NULL ALTER TABLE Appointments ADD EmployeeId INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Appointments_Org_Employee_Start')
CREATE INDEX IX_Appointments_Org_Employee_Start ON Appointments(OrganizationId, EmployeeId, StartAt) INCLUDE (EndAt, Status, IsDeleted);
GO

-- ---------------------------------------------------------------- team roster
-- The people a caller can actually be booked with. Capacity for a slot is how many of them
-- are on duty and free at that moment, which is what allows two 12:00 appointments when two
-- employees are working and refuses both when neither is. An organization with no rows here
-- falls back to Organizations.MaxConcurrentAppointments, so this is additive.
IF OBJECT_ID('Employees') IS NULL
CREATE TABLE Employees (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    Name NVARCHAR(200) NOT NULL,
    JobTitle NVARCHAR(150) NULL,
    Phone NVARCHAR(50) NULL,
    Email NVARCHAR(256) NULL,
    WorkingHoursJson NVARCHAR(MAX) NULL,               -- same shape as Organizations.BusinessHoursJson; NULL = follow the business hours
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    ModifiedAt DATETIME2 NULL,
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Employees_Org')
CREATE INDEX IX_Employees_Org ON Employees(OrganizationId) INCLUDE (IsActive);

GO

-- Days an employee is away. Inclusive of both ends, local calendar dates.
IF OBJECT_ID('EmployeeTimeOff') IS NULL
CREATE TABLE EmployeeTimeOff (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    EmployeeId INT NOT NULL REFERENCES Employees(Id),
    StartDate DATE NOT NULL,
    EndDate DATE NOT NULL,
    Reason NVARCHAR(200) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_EmployeeTimeOff_Org_Employee')
CREATE INDEX IX_EmployeeTimeOff_Org_Employee ON EmployeeTimeOff(OrganizationId, EmployeeId, StartDate);

GO

IF OBJECT_ID('CallLogs') IS NULL
CREATE TABLE CallLogs (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    CustomerId INT NULL REFERENCES Customers(Id),
    RetellCallId NVARCHAR(100) NULL,
    FromNumber NVARCHAR(50) NOT NULL,
    Direction NVARCHAR(20) NOT NULL DEFAULT 'Inbound',
    Status NVARCHAR(30) NOT NULL DEFAULT 'Completed', -- Completed | Missed | Transferred
    DurationSeconds INT NOT NULL DEFAULT 0,
    Transcript NVARCHAR(MAX) NULL,
    RecordingUrl NVARCHAR(1000) NULL,
    Summary NVARCHAR(MAX) NULL,
    StartedAt DATETIME2 NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO

-- The billing period this call's minutes were counted into, named by that period's end.
-- NULL means not yet counted. Minutes used to be totalled purely by StartedAt falling
-- inside the window being closed, which silently lost any row that arrived after that
-- close had run — a call still in progress at a period boundary, or a batch backfilled
-- from Retell afterwards. Stamping the row makes "counted" a property of the call rather
-- than of the clock, so a late arrival is swept into the next close instead of nobody's.
--
-- The backfill sits inside the same IF so it runs exactly once: every call within a period
-- already closed is marked as counted by it. Without that, the first close after this ships
-- would sweep up each customer's whole call history and bill them for all of it.
IF COL_LENGTH('CallLogs','BilledPeriodEnd') IS NULL
BEGIN
    ALTER TABLE CallLogs ADD BilledPeriodEnd DATETIME2 NULL;

    IF OBJECT_ID('OrganizationUsagePeriods') IS NOT NULL
    EXEC('
        UPDATE cl SET cl.BilledPeriodEnd = already.PeriodEnd
        FROM CallLogs cl
        CROSS APPLY (
            SELECT MIN(up.PeriodEnd) AS PeriodEnd
            FROM OrganizationUsagePeriods up
            WHERE up.OrganizationId = cl.OrganizationId
              AND up.PeriodEnd > cl.StartedAt
        ) already
        WHERE cl.BilledPeriodEnd IS NULL AND already.PeriodEnd IS NOT NULL;');
END
GO

-- Retell retries a webhook until it is acknowledged, and two deliveries of the same
-- call_ended can be in flight at once. Deduplication was a read-then-insert with nothing
-- behind it, so both could pass the check and both insert — billing the call twice.
-- Any duplicate already on record is one call counted twice, so the later rows are
-- soft-deleted first; the CREATE would otherwise fail on the very data it prevents.
UPDATE cl SET IsDeleted = 1
FROM CallLogs cl
JOIN (
    SELECT Id, ROW_NUMBER() OVER (PARTITION BY OrganizationId, RetellCallId ORDER BY Id) AS Seq
    FROM CallLogs
    WHERE RetellCallId IS NOT NULL AND IsDeleted = 0
) ranked ON ranked.Id = cl.Id
WHERE ranked.Seq > 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CallLogs_Org_RetellCallId')
CREATE UNIQUE INDEX UX_CallLogs_Org_RetellCallId
ON CallLogs (OrganizationId, RetellCallId)
WHERE RetellCallId IS NOT NULL AND IsDeleted = 0;
GO

-- The period close claims rows with this; the live usage figure counts them.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CallLogs_Org_Unbilled')
CREATE INDEX IX_CallLogs_Org_Unbilled
ON CallLogs (OrganizationId, BilledPeriodEnd, StartedAt)
INCLUDE (DurationSeconds, IsDeleted);
GO

-- Dates the business is closed, overriding Organizations.BusinessHoursJson for that day.
-- Fed into the AI prompt and knowledge base, and enforced by the live booking tools.
IF OBJECT_ID('Holidays') IS NULL
CREATE TABLE Holidays (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    [Date] DATE NOT NULL,                             -- local calendar date in the tenant timezone
    Name NVARCHAR(200) NOT NULL,                      -- e.g. 'Christmas Day', 'Staff training'
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Holidays_Org_Date')
CREATE UNIQUE INDEX IX_Holidays_Org_Date ON Holidays(OrganizationId, [Date]) WHERE IsDeleted = 0;
GO


-- ------------------------------------------------------- notifications
-- Browsers that asked to be told when the diary changes. Web Push, so a booking taken at 2am
-- reaches the owner's phone with the dashboard shut and without costing anything to send.
IF OBJECT_ID('PushDevices') IS NULL
CREATE TABLE PushDevices (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    UserId INT NOT NULL REFERENCES Users(Id),
    Endpoint NVARCHAR(500) NOT NULL,
    P256dh NVARCHAR(200) NOT NULL,
    Auth NVARCHAR(100) NOT NULL,
    Label NVARCHAR(200) NULL,
    UrgentOnly BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    LastNotifiedAt DATETIME2 NULL,
    FailureCount INT NOT NULL DEFAULT 0,
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PushDevices_Endpoint')
CREATE UNIQUE INDEX IX_PushDevices_Endpoint ON PushDevices(Endpoint) WHERE IsDeleted = 0;

GO

-- Things that happened unattended, and whether anyone has since looked. Urgent rows are chased
-- (re-notified) until AcknowledgedAt is set, which is what stops a missed emergency staying missed.
IF OBJECT_ID('Alerts') IS NULL
CREATE TABLE Alerts (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    Kind NVARCHAR(40) NOT NULL,
    Severity NVARCHAR(20) NOT NULL DEFAULT 'Info',
    Title NVARCHAR(200) NOT NULL,
    Body NVARCHAR(500) NOT NULL,
    Url NVARCHAR(300) NULL,
    AppointmentId INT NULL,
    AcknowledgedAt DATETIME2 NULL,
    AcknowledgedByUserId INT NULL,
    NotifiedCount INT NOT NULL DEFAULT 0,
    LastNotifiedAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- The escalation worker's query: unacknowledged urgent rows, oldest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Alerts_Pending')
CREATE INDEX IX_Alerts_Pending ON Alerts(AcknowledgedAt, Severity, LastNotifiedAt) INCLUDE (OrganizationId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Alerts_Org_Created')
CREATE INDEX IX_Alerts_Org_Created ON Alerts(OrganizationId, CreatedAt DESC);
GO

IF OBJECT_ID('KnowledgeBase') IS NULL
CREATE TABLE KnowledgeBase (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    Category NVARCHAR(50) NOT NULL DEFAULT 'FAQ',     -- BusinessInfo | FAQ | Policy | EmergencyRule | Custom
    Title NVARCHAR(300) NOT NULL,
    Content NVARCHAR(MAX) NOT NULL,
    SortOrder INT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    ModifiedAt DATETIME2 NULL,
    IsDeleted BIT NOT NULL DEFAULT 0
);
GO

IF OBJECT_ID('AgentConfig') IS NULL
CREATE TABLE AgentConfig (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL UNIQUE REFERENCES Organizations(Id),
    Voice NVARCHAR(100) NOT NULL DEFAULT 'retell-Grace',
    Language NVARCHAR(20) NOT NULL DEFAULT 'en-US',
    Greeting NVARCHAR(1000) NOT NULL DEFAULT '',
    TransferNumber NVARCHAR(50) NULL,
    RetellAgentId NVARCHAR(100) NULL,
    RetellLlmId NVARCHAR(100) NULL,
    RetellKnowledgeBaseId NVARCHAR(100) NULL,
    RetellPhoneNumber NVARCHAR(50) NULL,
    LastSyncedAt DATETIME2 NULL,
    Enabled BIT NOT NULL DEFAULT 1,
    EnabledToolsJson NVARCHAR(MAX) NOT NULL DEFAULT '[]',
    ModifiedAt DATETIME2 NULL
);
GO

IF OBJECT_ID('TimelineEvents') IS NULL
CREATE TABLE TimelineEvents (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    CustomerId INT NOT NULL REFERENCES Customers(Id),
    EventType NVARCHAR(50) NOT NULL,                  -- AiCall | AppointmentBooked | AppointmentCompleted | PaymentCompleted | ...
    Notes NVARCHAR(MAX) NULL,
    Source NVARCHAR(30) NOT NULL DEFAULT 'System',    -- AI | User | System
    UserId INT NULL,
    OccurredAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

IF OBJECT_ID('AuditLogs') IS NULL
CREATE TABLE AuditLogs (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL,
    UserId INT NULL,
    Action NVARCHAR(200) NOT NULL,
    Details NVARCHAR(MAX) NULL,
    IpAddress NVARCHAR(64) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);
GO

-- Single platform-wide Retell connection (one shared account for every tenant).
-- Managed centrally by SuperAdmin; empty fields fall back to appsettings (Retell:*).
IF OBJECT_ID('RetellConnection') IS NULL
CREATE TABLE RetellConnection (
    Id INT IDENTITY PRIMARY KEY,
    ApiKey NVARCHAR(300) NULL,
    ApiBaseUrl NVARCHAR(300) NOT NULL DEFAULT 'https://api.retellai.com',
    WebhookBaseUrl NVARCHAR(500) NULL,
    DefaultVoiceId NVARCHAR(100) NULL,
    VerifySignature BIT NOT NULL DEFAULT 1,
    ModifiedAt DATETIME2 NULL,
    ModifiedByUserId INT NULL
);
GO

-- Platform-wide wording of the AI system prompt, edited in the super admin console.
-- One row for the whole platform: a change here re-tunes every tenant's agent. A NULL
-- section falls back to the built-in default in PromptDefaults, so clearing a box restores it.
IF OBJECT_ID('PromptTemplate') IS NULL
CREATE TABLE PromptTemplate (
    Id INT IDENTITY PRIMARY KEY,
    Persona NVARCHAR(MAX) NULL,
    CoreRules NVARCHAR(MAX) NULL,
    ConversationGuide NVARCHAR(MAX) NULL,      -- businesses the customer comes to
    FieldServiceGuide NVARCHAR(MAX) NULL,      -- trades that travel to the customer
    ToolPolicy NVARCHAR(MAX) NULL,
    ModifiedAt DATETIME2 NULL,
    ModifiedByUserId INT NULL
);
GO
