using Dapper;
using Microsoft.Data.SqlClient;

namespace AiReceptionist.Api.Data;

/// <summary>Creates the AiDB database, schema and demo seed data on startup (idempotent).</summary>
public static class DbInitializer
{
    public static void Initialize(IConfiguration configuration, ILogger logger)
    {
        var masterCs = configuration.GetConnectionString("Master")!;
        var aidbCs = configuration.GetConnectionString("AiDB")!;

        using (var master = new SqlConnection(masterCs))
        {
            master.Execute("IF DB_ID('AiDB') IS NULL CREATE DATABASE AiDB;");
        }

        using var db = new SqlConnection(aidbCs);
        db.Execute(Schema);

        // Billing: subscriptions, tiers, Stripe linkage, closed usage periods and mirrored
        // invoices. Nominally the super admin console's territory, but this API serves the Stripe
        // webhook and the tenant billing page, so it cannot wait for the console to have started.
        // The statements are shared source (see BillingSchema) and idempotent in either order.
        foreach (var statement in Shared.BillingSchema.Statements)
            db.Execute(statement);

        var orgCount = db.ExecuteScalar<int>("SELECT COUNT(*) FROM Organizations");
        if (orgCount == 0)
        {
            Seed(db);
            logger.LogInformation("AiDB seeded with demo organization.");
        }

        // One-time cleanup: canonicalize stored phone numbers (idempotent).
        db.Execute("UPDATE Customers SET Phone = REPLACE(REPLACE(REPLACE(REPLACE(Phone,' ',''),'-',''),'(',''),')','') WHERE Phone LIKE '%[ ()-]%'");

        // Seed the single central Retell connection from appsettings on first run, so an
        // existing appsettings-based setup migrates into the DB with no manual step.
        var hasConnection = db.ExecuteScalar<int>("SELECT COUNT(*) FROM RetellConnection");
        if (hasConnection == 0)
        {
            db.Execute(@"
                INSERT INTO RetellConnection (ApiKey, ApiBaseUrl, WebhookBaseUrl, DefaultVoiceId, VerifySignature)
                VALUES (@ApiKey, @ApiBaseUrl, @WebhookBaseUrl, @DefaultVoiceId, @VerifySignature);",
                new
                {
                    ApiKey = configuration["Retell:ApiKey"],
                    ApiBaseUrl = configuration["Retell:ApiBaseUrl"] ?? "https://api.retellai.com",
                    WebhookBaseUrl = configuration["Retell:WebhookBaseUrl"],
                    DefaultVoiceId = configuration["Retell:DefaultVoiceId"],
                    VerifySignature = configuration.GetValue("Retell:VerifySignature", true),
                });
            logger.LogInformation("Seeded central Retell connection from appsettings.");
        }

        // Ensure a SuperAdmin exists (platform operator; sees the Retell connection panel).
        var superAdmins = db.ExecuteScalar<int>("SELECT COUNT(*) FROM Users WHERE Role='SuperAdmin' AND IsDeleted=0");
        var firstOrgId = db.ExecuteScalar<int?>("SELECT TOP 1 Id FROM Organizations ORDER BY Id");
        if (superAdmins == 0 && firstOrgId.HasValue)
        {
            var superHash = BCrypt.Net.BCrypt.HashPassword("Super123!");
            db.Execute(@"
                INSERT INTO Users (OrganizationId, FullName, Email, PasswordHash, Role, EmailVerified)
                VALUES (@orgId, 'Platform Admin', 'superadmin@demo.com', @superHash, 'SuperAdmin', 1);",
                new { orgId = firstOrgId.Value, superHash });
            logger.LogInformation("Seeded SuperAdmin user superadmin@demo.com.");
        }

        logger.LogInformation("AiDB is ready.");
    }

    private static void Seed(SqlConnection db)
    {
        var orgId = db.ExecuteScalar<int>(@"
            INSERT INTO Organizations (Name, Industry, Address, Phone, Email, Currency, Timezone, BusinessHoursJson, ProductsEnabled, OnboardingCompleted)
            OUTPUT INSERTED.Id
            VALUES ('Demo Clinic', 'Medical Clinic', '123 Main Street, Springfield', '+1 555 010 0100',
                    'hello@democlinic.com', 'USD', 'America/New_York',
                    '{""mon-fri"":""09:00-17:00"",""sat"":""10:00-14:00"",""sun"":""closed""}', 1, 1);");

        var hash = BCrypt.Net.BCrypt.HashPassword("Admin123!");
        db.Execute(@"
            INSERT INTO Users (OrganizationId, FullName, Email, PasswordHash, Role, EmailVerified)
            VALUES (@orgId, 'Victoria Adams', 'admin@demo.com', @hash, 'OrgAdmin', 1);",
            new { orgId, hash });

        db.Execute(@"
            INSERT INTO Services (OrganizationId, Name, Description, DurationMinutes, MinPrice, MaxPrice, IsEmergency) VALUES
            (@orgId, 'Consultation', 'General consultation with a specialist', 30, 50, 80, 0),
            (@orgId, 'Cardiology Check', 'Full cardiology screening', 40, 120, 180, 0),
            (@orgId, 'Dental Cleaning', 'Routine dental cleaning', 60, 90, 120, 0),
            (@orgId, 'Emergency Visit', 'Same-day urgent visit', 30, 150, 250, 1);",
            new { orgId });

        db.Execute(@"
            INSERT INTO Products (OrganizationId, Name, Sku, Category, Price, Quantity) VALUES
            (@orgId, 'Ibuprofen 200mg', 'MED-001', 'Medication', 8.50, 120),
            (@orgId, 'Vitamin B12', 'MED-002', 'Supplement', 12.00, 80),
            (@orgId, 'Faringosept', 'MED-003', 'Medication', 6.75, 60);",
            new { orgId });

        db.Execute(@"
            INSERT INTO Customers (OrganizationId, Name, Phone, Email, FirstVisit, LastVisit, TotalVisits) VALUES
            (@orgId, 'John Carter', '+1 555 010 0001', 'john.carter@mail.com', DATEADD(month,-6,GETUTCDATE()), DATEADD(day,-10,GETUTCDATE()), 5),
            (@orgId, 'Emma Wilson', '+1 555 010 0002', 'emma.w@mail.com', DATEADD(month,-3,GETUTCDATE()), DATEADD(day,-2,GETUTCDATE()), 3),
            (@orgId, 'Liam Brooks', '+1 555 010 0003', NULL, DATEADD(month,-1,GETUTCDATE()), NULL, 1);",
            new { orgId });

        db.Execute(@"
            DECLARE @c1 INT = (SELECT TOP 1 Id FROM Customers WHERE OrganizationId=@orgId ORDER BY Id);
            DECLARE @c2 INT = (SELECT Id FROM Customers WHERE OrganizationId=@orgId AND Name='Emma Wilson');
            DECLARE @s1 INT = (SELECT TOP 1 Id FROM Services WHERE OrganizationId=@orgId ORDER BY Id);
            DECLARE @s2 INT = (SELECT Id FROM Services WHERE OrganizationId=@orgId AND Name='Cardiology Check');
            DECLARE @staff INT = (SELECT TOP 1 Id FROM Users WHERE OrganizationId=@orgId);

            INSERT INTO Appointments (OrganizationId, CustomerId, ServiceId, StaffUserId, StartAt, EndAt, Status, PaymentStatus, Amount, Notes) VALUES
            (@orgId, @c1, @s2, @staff, DATEADD(hour, 14, CAST(CAST(GETUTCDATE() AS date) AS datetime2)), DATEADD(hour, 15, CAST(CAST(GETUTCDATE() AS date) AS datetime2)), 'Confirmed', 'Paid', 150, 'Cardiologist – Dr. Richard Michels'),
            (@orgId, @c2, @s1, @staff, DATEADD(hour, 11, CAST(CAST(GETUTCDATE() AS date) AS datetime2)), DATEADD(hour, 12, CAST(CAST(GETUTCDATE() AS date) AS datetime2)), 'Scheduled', 'Unpaid', 65, 'Dentist – Dr. Marie Jordan'),
            (@orgId, @c1, @s1, @staff, DATEADD(day, 2, GETUTCDATE()), DATEADD(day, 2, DATEADD(minute, 30, GETUTCDATE())), 'Scheduled', 'Unpaid', 50, NULL);

            INSERT INTO CallLogs (OrganizationId, CustomerId, FromNumber, Status, DurationSeconds, Summary, StartedAt) VALUES
            (@orgId, @c1, '+1 555 010 0001', 'Completed', 245, 'Booked cardiology appointment for today 2 PM.', DATEADD(hour,-3,GETUTCDATE())),
            (@orgId, @c2, '+1 555 010 0002', 'Completed', 130, 'Asked about dental cleaning prices.', DATEADD(hour,-6,GETUTCDATE())),
            (@orgId, NULL, '+1 555 010 0099', 'Missed', 0, NULL, DATEADD(hour,-8,GETUTCDATE()));

            INSERT INTO KnowledgeBase (OrganizationId, Category, Title, Content, SortOrder) VALUES
            (@orgId, 'BusinessInfo', 'About us', 'Demo Clinic is a family medical clinic in Springfield offering consultations, cardiology and dental care.', 1),
            (@orgId, 'FAQ', 'Do you accept walk-ins?', 'We recommend booking an appointment, but walk-ins are accepted based on availability.', 2),
            (@orgId, 'Policy', 'Cancellation policy', 'Appointments can be cancelled or rescheduled free of charge up to 12 hours in advance.', 3),
            (@orgId, 'EmergencyRule', 'Emergencies', 'For medical emergencies, always advise the caller to hang up and dial 911.', 4);

            INSERT INTO AgentConfig (OrganizationId, Voice, Language, Greeting, TransferNumber, Enabled, EnabledToolsJson) VALUES
            (@orgId, 'retell-Grace', 'en-US',
             N'Hi, thanks for calling Demo Clinic — you''re through to our AI assistant. How can I help?',
             '+1 555 010 0199', 1, '[""book_appointment"",""cancel_appointment"",""reschedule_appointment"",""quote_price"",""transfer_call""]');",
            new { orgId });

        SeedHistory(db, orgId);
    }

    /// <summary>Back-fills a year of calls and completed appointments so the dashboard's trends,
    /// talk-time balance and revenue chart have something to draw on a fresh install. Demo data
    /// only — it runs once, in the same block that creates the demo organization.</summary>
    private static void SeedHistory(SqlConnection db, int orgId)
    {
        db.Execute(@"
            DECLARE @c1 INT = (SELECT TOP 1 Id FROM Customers WHERE OrganizationId=@orgId ORDER BY Id);
            DECLARE @c2 INT = (SELECT Id FROM Customers WHERE OrganizationId=@orgId AND Name='Emma Wilson');
            DECLARE @s1 INT = (SELECT TOP 1 Id FROM Services WHERE OrganizationId=@orgId ORDER BY Id);
            DECLARE @s2 INT = (SELECT Id FROM Services WHERE OrganizationId=@orgId AND Name='Cardiology Check');
            DECLARE @staff INT = (SELECT TOP 1 Id FROM Users WHERE OrganizationId=@orgId);

            -- One row per day since the start of the year, up to today.
            DECLARE @day DATE = DATEFROMPARTS(YEAR(GETUTCDATE()), 1, 1);
            DECLARE @today DATE = CAST(GETUTCDATE() AS DATE);
            DECLARE @i INT = 0;

            WHILE @day <= @today
            BEGIN
                -- Sunday is closed, so no calls; weekdays are busier than Saturdays.
                DECLARE @dow INT = DATEPART(weekday, @day);
                DECLARE @calls INT = CASE WHEN @dow = 1 THEN 0 WHEN @dow = 7 THEN 2 ELSE 4 + (@i % 5) END;
                DECLARE @n INT = 0;

                WHILE @n < @calls
                BEGIN
                    -- Calls land between 09:00 and 17:00, clustered late morning.
                    DECLARE @hour INT = 9 + ((@i * 3 + @n * 2) % 8);
                    DECLARE @roll INT = (@i * 7 + @n * 13) % 10;
                    DECLARE @status NVARCHAR(30) =
                        CASE WHEN @roll < 7 THEN 'Completed' WHEN @roll < 9 THEN 'Transferred' ELSE 'Missed' END;

                    INSERT INTO CallLogs (OrganizationId, CustomerId, FromNumber, Status, DurationSeconds, Summary, StartedAt)
                    VALUES (@orgId,
                            CASE WHEN @roll % 3 = 0 THEN @c1 WHEN @roll % 3 = 1 THEN @c2 ELSE NULL END,
                            '+1 555 010 0' + RIGHT('000' + CAST(100 + @roll AS NVARCHAR(4)), 3),
                            @status,
                            CASE WHEN @status = 'Missed' THEN 0 ELSE 90 + (@roll * 37) % 260 END,
                            CASE WHEN @status = 'Missed' THEN NULL ELSE 'Caller enquiry handled by the AI receptionist.' END,
                            DATEADD(hour, @hour, CAST(@day AS DATETIME2)));
                    SET @n = @n + 1;
                END

                -- A couple of completed, paid appointments a week keeps the revenue chart honest.
                IF @dow IN (3, 5) AND @day < @today
                    INSERT INTO Appointments (OrganizationId, CustomerId, ServiceId, StaffUserId, StartAt, EndAt,
                                              Status, PaymentStatus, Amount, CreatedAt)
                    VALUES (@orgId, CASE WHEN @i % 2 = 0 THEN @c1 ELSE @c2 END,
                            CASE WHEN @i % 2 = 0 THEN @s2 ELSE @s1 END, @staff,
                            DATEADD(hour, 10, CAST(@day AS DATETIME2)), DATEADD(hour, 11, CAST(@day AS DATETIME2)),
                            'Completed', 'Paid', 60 + (@i % 6) * 25, DATEADD(day, -2, CAST(@day AS DATETIME2)));

                SET @day = DATEADD(day, 1, @day);
                SET @i = @i + 1;
            END",
            new { orgId }, commandTimeout: 120);

        // Two closures ahead of today so the dashboard's holiday card has something to warn about.
        db.Execute(@"
            INSERT INTO Holidays (OrganizationId, [Date], Name) VALUES
            (@orgId, DATEADD(day, 9, CAST(GETUTCDATE() AS DATE)), 'Staff training day'),
            (@orgId, DATEADD(day, 34, CAST(GETUTCDATE() AS DATE)), 'Public holiday — clinic closed');",
            new { orgId });
    }

    private const string Schema = @"
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
    IsDeleted BIT NOT NULL DEFAULT 0
);
IF COL_LENGTH('Organizations','MaxConcurrentAppointments') IS NULL
ALTER TABLE Organizations ADD MaxConcurrentAppointments INT NOT NULL DEFAULT 1;

-- Account enable/disable, owned by the super admin console. Declared here too so the API can
-- read it whichever app starts first (see database/03_platform.sql).
IF COL_LENGTH('Organizations','IsActive') IS NULL
ALTER TABLE Organizations ADD IsActive BIT NOT NULL DEFAULT 1;
IF COL_LENGTH('Organizations','SuspendedAt') IS NULL
ALTER TABLE Organizations ADD SuspendedAt DATETIME2 NULL;
IF COL_LENGTH('Organizations','SuspendedReason') IS NULL
ALTER TABLE Organizations ADD SuspendedReason NVARCHAR(500) NULL;

IF OBJECT_ID('Users') IS NULL
CREATE TABLE Users (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    FullName NVARCHAR(200) NOT NULL,
    Email NVARCHAR(256) NOT NULL,
    PasswordHash NVARCHAR(500) NOT NULL,
    Role NVARCHAR(50) NOT NULL DEFAULT 'ReadOnly',
    EmailVerified BIT NOT NULL DEFAULT 0,
    MfaEnabled BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Users_Email')
CREATE UNIQUE INDEX IX_Users_Email ON Users(Email) WHERE IsDeleted = 0;

IF OBJECT_ID('RefreshTokens') IS NULL
CREATE TABLE RefreshTokens (
    Id INT IDENTITY PRIMARY KEY,
    UserId INT NOT NULL REFERENCES Users(Id),
    Token NVARCHAR(500) NOT NULL,
    ExpiresAt DATETIME2 NOT NULL,
    Revoked BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

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
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Customers_Org_Phone')
CREATE INDEX IX_Customers_Org_Phone ON Customers(OrganizationId, Phone);

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

IF OBJECT_ID('Appointments') IS NULL
CREATE TABLE Appointments (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    CustomerId INT NOT NULL REFERENCES Customers(Id),
    ServiceId INT NULL REFERENCES Services(Id),
    StaffUserId INT NULL REFERENCES Users(Id),
    StartAt DATETIME2 NOT NULL,
    EndAt DATETIME2 NOT NULL,
    Status NVARCHAR(30) NOT NULL DEFAULT 'Scheduled',
    PaymentStatus NVARCHAR(30) NOT NULL DEFAULT 'Unpaid',
    Amount DECIMAL(18,2) NOT NULL DEFAULT 0,
    Notes NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    ModifiedAt DATETIME2 NULL,
    IsDeleted BIT NOT NULL DEFAULT 0
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Appointments_Org_Start')
CREATE INDEX IX_Appointments_Org_Start ON Appointments(OrganizationId, StartAt) INCLUDE (Status);

-- Field-service support (plumbers, electricians, HVAC, locksmiths, cleaners)
IF COL_LENGTH('Appointments','ServiceAddress') IS NULL ALTER TABLE Appointments ADD ServiceAddress NVARCHAR(500) NULL;
IF COL_LENGTH('Appointments','IsEmergency') IS NULL ALTER TABLE Appointments ADD IsEmergency BIT NOT NULL DEFAULT 0;

IF OBJECT_ID('CallLogs') IS NULL
CREATE TABLE CallLogs (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    CustomerId INT NULL REFERENCES Customers(Id),
    RetellCallId NVARCHAR(100) NULL,
    FromNumber NVARCHAR(50) NOT NULL,
    Direction NVARCHAR(20) NOT NULL DEFAULT 'Inbound',
    Status NVARCHAR(30) NOT NULL DEFAULT 'Completed',
    DurationSeconds INT NOT NULL DEFAULT 0,
    Transcript NVARCHAR(MAX) NULL,
    RecordingUrl NVARCHAR(1000) NULL,
    Summary NVARCHAR(MAX) NULL,
    StartedAt DATETIME2 NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);

-- Marks calls whose transcript has already been mined for caller intent
-- (transferred-call follow-up). NULL = not yet processed by the intent worker.
IF COL_LENGTH('CallLogs','IntentProcessedAt') IS NULL
ALTER TABLE CallLogs ADD IntentProcessedAt DATETIME2 NULL;

-- Suggested actions extracted from a transferred call's transcript (SRS §15/§19).
-- A human confirms or dismisses these; the AI never books directly from a transfer.
IF OBJECT_ID('CallActionSuggestions') IS NULL
CREATE TABLE CallActionSuggestions (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    CallLogId INT NOT NULL REFERENCES CallLogs(Id),
    Action NVARCHAR(20) NOT NULL,                      -- Book | Cancel | Reschedule | None
    Confidence NVARCHAR(10) NOT NULL DEFAULT 'low',    -- low | medium | high
    CustomerName NVARCHAR(200) NULL,
    Phone NVARCHAR(50) NULL,
    ServiceName NVARCHAR(200) NULL,
    StartAtLocal DATETIME2 NULL,                        -- proposed local start (book / reschedule)
    Reasoning NVARCHAR(MAX) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Pending',    -- Pending | Confirmed | Dismissed | Failed
    ResultAppointmentId INT NULL,
    ResolvedByUserId INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    ResolvedAt DATETIME2 NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CallActionSuggestions_Org_Status')
CREATE INDEX IX_CallActionSuggestions_Org_Status ON CallActionSuggestions(OrganizationId, Status);

-- Dates the business is closed, overriding Organizations.BusinessHoursJson for that day.
-- Fed into the AI prompt and knowledge base, and enforced by the live booking tools.
IF OBJECT_ID('Holidays') IS NULL
CREATE TABLE Holidays (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    [Date] DATE NOT NULL,
    Name NVARCHAR(200) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Holidays_Org_Date')
CREATE UNIQUE INDEX IX_Holidays_Org_Date ON Holidays(OrganizationId, [Date]) WHERE IsDeleted = 0;

IF OBJECT_ID('KnowledgeBase') IS NULL
CREATE TABLE KnowledgeBase (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    Category NVARCHAR(50) NOT NULL DEFAULT 'FAQ',
    Title NVARCHAR(300) NOT NULL,
    Content NVARCHAR(MAX) NOT NULL,
    SortOrder INT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    ModifiedAt DATETIME2 NULL,
    IsDeleted BIT NOT NULL DEFAULT 0
);

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
    RetellPhoneNumber NVARCHAR(50) NULL,
    LastSyncedAt DATETIME2 NULL,
    Enabled BIT NOT NULL DEFAULT 1,
    EnabledToolsJson NVARCHAR(MAX) NOT NULL DEFAULT '[]',
    ModifiedAt DATETIME2 NULL
);

-- Upgrade path for databases created before the Retell integration
IF COL_LENGTH('AgentConfig','RetellLlmId') IS NULL ALTER TABLE AgentConfig ADD RetellLlmId NVARCHAR(100) NULL;
IF COL_LENGTH('AgentConfig','RetellKnowledgeBaseId') IS NULL ALTER TABLE AgentConfig ADD RetellKnowledgeBaseId NVARCHAR(100) NULL;
IF COL_LENGTH('AgentConfig','RetellPhoneNumber') IS NULL ALTER TABLE AgentConfig ADD RetellPhoneNumber NVARCHAR(50) NULL;
IF COL_LENGTH('AgentConfig','LastSyncedAt') IS NULL ALTER TABLE AgentConfig ADD LastSyncedAt DATETIME2 NULL;

IF OBJECT_ID('TimelineEvents') IS NULL
CREATE TABLE TimelineEvents (
    Id INT IDENTITY PRIMARY KEY,
    OrganizationId INT NOT NULL REFERENCES Organizations(Id),
    CustomerId INT NOT NULL REFERENCES Customers(Id),
    EventType NVARCHAR(50) NOT NULL,
    Notes NVARCHAR(MAX) NULL,
    Source NVARCHAR(30) NOT NULL DEFAULT 'System',
    UserId INT NULL,
    OccurredAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

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

-- Platform-wide wording of the AI system prompt, edited in the super admin console. One row for
-- the whole platform: a change here re-tunes every tenant's agent. A NULL section means the built-in
-- default (PromptDefaults) is used, so clearing a box restores it.
IF OBJECT_ID('PromptTemplate') IS NULL
CREATE TABLE PromptTemplate (
    Id INT IDENTITY PRIMARY KEY,
    Persona NVARCHAR(MAX) NULL,
    CoreRules NVARCHAR(MAX) NULL,
    ConversationGuide NVARCHAR(MAX) NULL,
    FieldServiceGuide NVARCHAR(MAX) NULL,
    ToolPolicy NVARCHAR(MAX) NULL,
    ModifiedAt DATETIME2 NULL,
    ModifiedByUserId INT NULL
);
";
}
