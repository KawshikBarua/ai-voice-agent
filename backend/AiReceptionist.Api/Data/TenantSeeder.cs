using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiReceptionist.Api.Data;

/// <summary>
/// One business to stand up as a tenant: the organization profile, its first administrator, and
/// everything the AI agent is answered from — services, knowledge base, agent wording and the
/// coverage areas the booking rules are checked against.
///
/// Every field is what the business itself states. Where a business publishes nothing (a plumber
/// with no address on the site), the field is left unset rather than guessed at: an invented
/// timezone or an invented price is something the agent will read out to a caller as fact.
/// </summary>
public record TenantBlueprint(
    string BusinessName,
    string Industry,
    string AdminEmail,
    string Password,
    string AdminName,
    string Currency,
    string Timezone,
    string BusinessHoursJson,
    string Greeting,
    /// <summary>How many jobs can run at once — trucks on the road, crews out, plumbers in vans.
    /// The roster (Employees) is empty for all three, so this is what capacity is taken from.</summary>
    int MaxConcurrent,
    string? Address = null,
    string? Phone = null,
    string? TransferNumber = null,
    IReadOnlyList<SeedService>? Services = null,
    IReadOnlyList<SeedArticle>? Knowledge = null,
    /// <summary>Cities this business says it serves. Each becomes a branch covering that whole
    /// city, resolved through the same geocoder the Locations screen uses. Empty means the
    /// business publishes no service area, and no coverage rules are applied at all.</summary>
    IReadOnlyList<string>? CoverageCities = null,
    string CoverageCountryCode = "US");

/// <summary>A line on the price list. The prompt quotes <see cref="MinPrice"/>–<see cref="MaxPrice"/>
/// to callers verbatim, so a zero here would have the agent offering the work for nothing.</summary>
public record SeedService(
    string Name, string Description, int DurationMinutes,
    decimal MinPrice, decimal MaxPrice, bool IsEmergency = false);

public record SeedArticle(string Category, string Title, string Content);

/// <summary>
/// Stands up the three client businesses as working tenants, keyed on the administrator's email so
/// it can be run as often as you like: an account that already exists is reported and left alone,
/// and a tenant whose coverage areas failed to resolve last time (OpenStreetMap down, no network)
/// gets them filled in on the next run.
///
/// Run with: <c>dotnet run --project backend/AiReceptionist.Api -- seed-tenants</c>
/// </summary>
public static class TenantSeeder
{
    /// <summary>Placeholder numbers, in the 555-01xx range reserved for fiction — none of the
    /// three businesses publishes a transfer line, and a real number dialled by mistake belongs
    /// to a real stranger.</summary>
    private const string PlaceholderTransfer = "+1 555 010 0199";

    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var geocoding = services.GetRequiredService<IGeocodingService>();
        await using var db = new SqlConnection(config.GetConnectionString("AiDB"));

        var created = new List<(TenantBlueprint Tenant, int OrgId)>();
        var existing = new List<TenantBlueprint>();

        foreach (var tenant in Blueprints)
        {
            var email = tenant.AdminEmail.Trim().ToLowerInvariant();
            var userId = await db.ExecuteScalarAsync<int?>(
                "SELECT TOP 1 Id FROM Users WHERE Email=@email AND IsDeleted=0", new { email });

            if (userId is not null)
            {
                var orgId = await db.ExecuteScalarAsync<int>(
                    "SELECT OrganizationId FROM Users WHERE Id=@userId", new { userId });
                existing.Add(tenant);
                // Coverage is the one part that can fail for reasons outside the database, so it
                // is the one part re-checked on an account that is already there.
                await SeedCoverageAsync(db, geocoding, orgId, tenant, logger);
                continue;
            }

            var newOrgId = await CreateTenantAsync(db, geocoding, tenant, email, logger);
            created.Add((tenant, newOrgId));
        }

        Report(created, existing);
        return created.Count;
    }

    private static async Task<int> CreateTenantAsync(SqlConnection db, IGeocodingService geocoding,
        TenantBlueprint t, string email, ILogger logger)
    {
        var orgId = await db.ExecuteScalarAsync<int>(@"
            INSERT INTO Organizations
                (Name, Industry, Address, Phone, Email, Currency, Timezone, BusinessHoursJson,
                 MaxConcurrentAppointments, ProductsEnabled, OnboardingCompleted)
            OUTPUT INSERTED.Id
            VALUES (@BusinessName, @Industry, @Address, @Phone, @email, @Currency, @Timezone,
                    @BusinessHoursJson, @MaxConcurrent, 0, 1);",
            new
            {
                t.BusinessName, t.Industry, t.Address, t.Phone, email, t.Currency, t.Timezone,
                t.BusinessHoursJson, t.MaxConcurrent,
            });

        await db.ExecuteAsync(@"
            INSERT INTO Users (OrganizationId, FullName, Email, PasswordHash, Role, EmailVerified)
            VALUES (@orgId, @AdminName, @email, @hash, @role, 1);",
            new
            {
                orgId, t.AdminName, email, role = Roles.OrgAdmin,
                hash = BCrypt.Net.BCrypt.HashPassword(t.Password),
            });

        foreach (var s in t.Services ?? [])
            await db.ExecuteAsync(@"
                INSERT INTO Services (OrganizationId, Name, Description, DurationMinutes,
                                      MinPrice, MaxPrice, IsEmergency)
                VALUES (@orgId, @Name, @Description, @DurationMinutes, @MinPrice, @MaxPrice, @IsEmergency);",
                new { orgId, s.Name, s.Description, s.DurationMinutes, s.MinPrice, s.MaxPrice, s.IsEmergency });

        var order = 0;
        foreach (var a in t.Knowledge ?? [])
            await db.ExecuteAsync(@"
                INSERT INTO KnowledgeBase (OrganizationId, Category, Title, Content, SortOrder)
                VALUES (@orgId, @Category, @Title, @Content, @sortOrder);",
                new { orgId, a.Category, a.Title, a.Content, sortOrder = ++order });

        await db.ExecuteAsync(@"
            INSERT INTO AgentConfig (OrganizationId, Voice, Language, Greeting, TransferNumber,
                                     Enabled, EnabledToolsJson)
            VALUES (@orgId, 'retell-Grace', 'en-US', @Greeting, @transfer, 1, @tools);",
            new
            {
                orgId, t.Greeting,
                transfer = t.TransferNumber ?? t.Phone ?? PlaceholderTransfer,
                tools = """
                    ["identify_customer","check_availability","check_service_area","book_appointment","cancel_appointment","reschedule_appointment","check_appointment"]
                    """.Trim(),
            });

        await SeedCoverageAsync(db, geocoding, orgId, t, logger);
        await SubscribeAsync(db, orgId);

        logger.LogInformation("Seeded tenant {Business} (organization {OrgId}).", t.BusinessName, orgId);
        return orgId;
    }

    /// <summary>
    /// One branch per city the business says it serves, each covering that whole city.
    ///
    /// The centre and the city's bounding box are resolved through <see cref="IGeocodingService"/>
    /// — the same path the Locations screen takes — rather than written down here, because a
    /// coordinate typed into a seed file is a coordinate nobody ever checks again. OpenStreetMap
    /// being unreachable is not a failure worth aborting a seed for: the city is skipped with a
    /// warning and the next run picks it up, since a city already stored is never resolved twice.
    /// </summary>
    private static async Task SeedCoverageAsync(SqlConnection db, IGeocodingService geocoding,
        int orgId, TenantBlueprint t, ILogger logger)
    {
        if (t.CoverageCities is null or { Count: 0 }) return;

        var countryName = Countries.NameFor(t.CoverageCountryCode)
                          ?? throw new InvalidOperationException($"'{t.CoverageCountryCode}' is not a country code.");

        foreach (var city in t.CoverageCities)
        {
            var already = await db.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM BusinessLocations
                WHERE OrganizationId=@orgId AND City=@city AND IsDeleted=0", new { orgId, city });
            if (already > 0) continue;

            var matches = await geocoding.SearchCitiesAsync(t.CoverageCountryCode, city);
            var match = matches.FirstOrDefault(m => Geo.SameCity(m.City, city)) ?? matches.FirstOrDefault();
            if (match is null)
            {
                logger.LogWarning(
                    "Could not resolve '{City}, {Country}' for {Business} — coverage area skipped. " +
                    "Re-run this command to try again, or add it from Settings → Business → Locations.",
                    city, countryName, t.BusinessName);
                continue;
            }

            await db.ExecuteAsync(@"
                INSERT INTO BusinessLocations
                    (OrganizationId, Name, CountryCode, CountryName, City, Latitude, Longitude,
                     CoverageRadiusMiles, CoversEntireCity, IsActive,
                     BoundsSouth, BoundsNorth, BoundsWest, BoundsEast)
                VALUES (@orgId, @name, @code, @countryName, @city, @lat, @lon, 25, 1, 1,
                        @south, @north, @west, @east);",
                new
                {
                    orgId, name = match.City, code = t.CoverageCountryCode, countryName,
                    city = match.City, lat = match.Latitude, lon = match.Longitude,
                    south = match.Bounds?.South, north = match.Bounds?.North,
                    west = match.Bounds?.West, east = match.Bounds?.East,
                });
        }
    }

    /// <summary>
    /// Puts the tenant on a plan, because an organization on nothing is not entitled to take calls
    /// at all (see <c>ICallEntitlementService</c>) — a seeded account whose phone never rings is a
    /// puzzle rather than a starting point.
    ///
    /// The cheapest tier the operator has actually published is used. Only when there are none is
    /// one created, and then it carries no Stripe price: nothing seeded here should be collectable for.
    /// </summary>
    private static async Task SubscribeAsync(SqlConnection db, int orgId) =>
        await db.ExecuteAsync(@"
            DECLARE @planId INT = (SELECT TOP 1 Id FROM PricingPlans
                                   WHERE IsActive=1 AND IsDeleted=0 ORDER BY Amount, SortOrder, Id);

            IF @planId IS NULL
            BEGIN
                INSERT INTO PricingPlans
                    (Name, Description, Currency, Amount, BillingCycle, IncludedMinutes,
                     OverageRatePerMinute, SortOrder, IsActive)
                VALUES ('Starter', 'Demo tier — 500 AI minutes a month.', 'USD', 49.00, 'Monthly',
                        500, 0.1200, 1, 1);
                SET @planId = SCOPE_IDENTITY();
            END

            IF NOT EXISTS (SELECT 1 FROM OrganizationSubscriptions WHERE OrganizationId = @orgId)
            INSERT INTO OrganizationSubscriptions
                (OrganizationId, PlanId, PlanName, BillingCycle, Amount, Currency, StartedAt,
                 CurrentPeriodStart, CurrentPeriodEnd, IncludedMinutes, OverageRatePerMinute)
            SELECT @orgId, @planId, p.Name, p.BillingCycle, p.Amount, p.Currency, GETUTCDATE(),
                   GETUTCDATE(), DATEADD(month, 1, GETUTCDATE()), p.IncludedMinutes,
                   p.OverageRatePerMinute
            FROM PricingPlans p WHERE p.Id = @planId;",
            new { orgId });

    private static void Report(
        List<(TenantBlueprint Tenant, int OrgId)> created, List<TenantBlueprint> existing)
    {
        Console.WriteLine();
        if (created.Count > 0)
        {
            Console.WriteLine("Accounts created — sign in at the dashboard with these:");
            Console.WriteLine();
            foreach (var (t, orgId) in created)
            {
                Console.WriteLine($"  {t.BusinessName}  (organization {orgId})");
                Console.WriteLine($"    email:    {t.AdminEmail}");
                Console.WriteLine($"    password: {t.Password}");
                Console.WriteLine();
            }
            Console.WriteLine("Change every one of these before the accounts are used by anybody real.");
        }

        foreach (var t in existing)
            Console.WriteLine($"Already present, left untouched: {t.BusinessName} ({t.AdminEmail}).");

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- the businesses

    private static readonly TenantBlueprint[] Blueprints =
    [
        Silverback,
        JohnsonsElectric,
        QuickPlumb,
    ];

    /// <summary>
    /// Silverback Towing — family-owned towing, recovery and transportation, Burnaby BC.
    ///
    /// The only one of the three that publishes where it works, so it is the only one given
    /// coverage areas: the nine Greater Vancouver cities it names, each covered in full, which is
    /// the right shape for a patch that is municipal rather than a circle on a map.
    /// </summary>
    private static TenantBlueprint Silverback => new(
        BusinessName: "Silverback Towing",
        Industry: "Towing, Recovery & Transportation",
        AdminEmail: "admin@silverbacktowing.ca",
        Password: "Silverback#Tow2026",
        AdminName: "Silverback Towing Administrator",
        // Burnaby, British Columbia: Canadian dollars and Pacific time.
        Currency: "CAD",
        Timezone: "America/Vancouver",
        // Open 24 hours, which is the whole of this business's positioning. All seven days are
        // written out singly, the shape the Settings editor round-trips.
        BusinessHoursJson: """
            {"mon":"00:00-23:59","tue":"00:00-23:59","wed":"00:00-23:59","thu":"00:00-23:59","fri":"00:00-23:59","sat":"00:00-23:59","sun":"00:00-23:59"}
            """.Trim(),
        Greeting: "Silverback Towing, you're through to our assistant — we're open 24 hours. " +
                  "Are you somewhere safe, and whereabouts is the vehicle?",
        // "Large, well-equipped fleet" is as precise as the company gets about numbers, so this is
        // a working figure for how many jobs can run at once, not a stated fact.
        MaxConcurrent: 6,
        Address: "6938 Kingsway, Burnaby, BC V5E 1E6",
        Phone: "+1 614 528 9877",
        Services:
        [
            new("Emergency & Local Towing", "24/7 emergency and local towing anywhere in Greater Vancouver.", 60, 95, 180, IsEmergency: true),
            new("Long-Distance Towing", "Towing across British Columbia and beyond the Lower Mainland. Priced by distance.", 240, 250, 900),
            new("Accident Towing & Vehicle Recovery", "Collision recovery and removal, including vehicles off the roadway.", 90, 150, 400, IsEmergency: true),
            new("Heavy-Duty Towing & Recovery", "Buses, semis and heavy vehicles, with heavy-duty units and operators.", 150, 400, 1200, IsEmergency: true),
            new("Roadside Assistance", "Roadside help to get a vehicle moving again without a tow where possible.", 45, 80, 160, IsEmergency: true),
            new("Battery Boost", "Jump start and battery check at the roadside.", 30, 65, 110, IsEmergency: true),
            new("Tire Change", "Roadside change to the spare, or a tow where there is no usable spare.", 40, 75, 140, IsEmergency: true),
            new("Flatbed Towing", "Flatbed transport for low-clearance, all-wheel-drive, classic and non-rolling vehicles.", 75, 120, 260),
            new("Motorcycle Transportation", "Secured motorcycle transport on purpose-fitted equipment.", 60, 110, 220),
            new("RV Towing", "Motorhome and RV towing with the units and operators for the size.", 120, 300, 800),
            new("Private Property & Impound Towing", "Unauthorised vehicle removal for property owners and managers.", 60, 120, 250),
            new("Specialty Vehicle Transportation", "Classic, exotic and non-standard vehicles moved on suitable equipment.", 120, 200, 600),
            new("Equipment & Machinery Transportation", "Forklifts, bobcats, scissor lifts and similar machinery moved on site or between sites.", 180, 300, 950),
            new("Underground Parking Towing & Recovery", "Low-clearance recovery from underground and structured parking.", 90, 160, 350),
            new("Vehicle Unlock", "Keys locked in the vehicle, opened without damage where possible.", 30, 70, 130, IsEmergency: true),
        ],
        Knowledge:
        [
            new("BusinessInfo", "About Silverback Towing",
                "Silverback Towing is a family-owned towing, recovery and transportation company based in " +
                "Burnaby, British Columbia. The business has been in the same family's hands since the 1970s " +
                "and now runs one of BC's larger full-service towing fleets. Head office is at 6938 Kingsway, " +
                "Burnaby, and the phones are answered 24 hours a day."),
            new("BusinessInfo", "Where we work",
                "Our main patch is Greater Vancouver: Burnaby, Vancouver, New Westminster, Coquitlam, Delta, " +
                "Richmond, Surrey, Port Coquitlam and Port Moody. Long-distance towing and equipment " +
                "transportation go further afield — anywhere in British Columbia and beyond the Lower " +
                "Mainland — and those jobs are quoted by distance."),
            new("BusinessInfo", "Our fleet and what it can handle",
                "The fleet runs from small cars and motorcycles up to buses, semis, RVs, heavy equipment and " +
                "specialty vehicles. It includes flatbeds and heavy-duty towing units, and equipment for " +
                "machinery such as forklifts, bobcats and scissor lifts. Low-clearance work in underground " +
                "and structured parking is a regular part of the job rather than an exception."),
            new("FAQ", "Are you open right now?",
                "Yes. Silverback Towing operates 24 hours a day, every day, including weekends and public " +
                "holidays. Emergency and roadside calls are taken at any hour."),
            new("FAQ", "What do you need to know when I call?",
                "Where the vehicle is — a street address, a junction, or a highway and the nearest exit — " +
                "along with the make, model and whether the wheels turn and the vehicle steers. That last " +
                "part decides whether it goes on a flatbed. A contact number for the driver, and where the " +
                "vehicle is going, finishes the job off."),
            new("Policy", "Quotes and what a price depends on",
                "Local tows are quoted from the price list. Long-distance work, heavy-duty recovery and " +
                "equipment transport are quoted on the job, because they turn on distance, weight, access " +
                "and the equipment needed. Anything that cannot be priced properly over the phone is passed " +
                "to dispatch for a firm quote rather than guessed at."),
            new("EmergencyRule", "Collisions, injuries and unsafe roadside situations",
                "If anyone is hurt, or the vehicle is in live traffic, tell the caller to hang up and dial " +
                "911 first — that comes before any booking. Where the vehicle is drivable and safe, advise " +
                "moving onto the shoulder or the nearest safe spot, staying in the vehicle with the seatbelt " +
                "on and the hazards going, and waiting there. Never advise anyone to stand on a roadway or " +
                "to attempt a recovery themselves."),
        ],
        CoverageCities:
        [
            "Burnaby", "Vancouver", "New Westminster", "Coquitlam", "Delta",
            "Richmond", "Surrey", "Port Coquitlam", "Port Moody",
        ],
        CoverageCountryCode: "CA");

    /// <summary>
    /// Johnsons Electric House — commercial, industrial and renewable-energy electrical work.
    ///
    /// The company publishes no address, no phone number and no service area, so none is invented:
    /// there are no coverage areas, and the coverage rules are simply off for this tenant. Hours,
    /// currency and timezone are a working default for a project-based contractor, not a fact.
    /// </summary>
    private static TenantBlueprint JohnsonsElectric => new(
        BusinessName: "Johnsons Electric House",
        Industry: "Electrical & Renewable Energy Services",
        AdminEmail: "admin@johnsonselectrichouse.com",
        Password: "Johnsons#Volt2026",
        AdminName: "Johnsons Electric House Administrator",
        Currency: "USD",
        Timezone: "America/New_York",
        // Site hours for project work: an early start, Saturday mornings, Sunday shut.
        BusinessHoursJson: """
            {"mon":"07:00-17:30","tue":"07:00-17:30","wed":"07:00-17:30","thu":"07:00-17:30","fri":"07:00-17:30","sat":"08:00-14:00","sun":"closed"}
            """.Trim(),
        Greeting: "Johnsons Electric House, you're through to our assistant. " +
                  "Is this about a site visit, a fault, or a project you're planning?",
        // Four crews out at once — a working figure, not a published one.
        MaxConcurrent: 4,
        TransferNumber: PlaceholderTransfer,
        Services:
        [
            new("Electrical System Installation", "New electrical systems for commercial and industrial buildings, coordinated with the other trades on site.", 240, 600, 4500),
            new("Electrical Maintenance & Repair", "Scheduled and corrective work on existing electrical systems.", 120, 180, 900),
            new("Electrical Troubleshooting & Diagnostics", "Tracing a fault to its cause, with the readings and documentation to back it up.", 90, 150, 600),
            new("Commercial Electrical Services", "Electrical work for offices, retail and mixed-use commercial buildings.", 180, 350, 2500),
            new("Industrial Electrical Services", "Plant and process electrical work, including three-phase distribution and controls.", 240, 500, 3500),
            new("Solar Electrical Systems", "Electrical installation, testing and commissioning for solar PV arrays.", 300, 1200, 9000),
            new("Battery Energy Storage (BESS)", "Electrical work for battery energy storage — installation, connection and commissioning.", 300, 1500, 12000),
            new("Wind Energy Electrical Systems", "Electrical installation and maintenance on wind generation sites.", 300, 1500, 11000),
            new("Electrical Inspection", "Condition and compliance inspection with a written report.", 120, 200, 800),
            new("Preventive Electrical Maintenance", "Planned maintenance visits that catch faults before they take a system down.", 150, 250, 1200),
            new("Code & Safety Compliance Review", "Checking an installation against the applicable electrical code and safety regulations.", 120, 250, 1000),
            new("Emergency Electrical Callout", "Urgent attendance where a fault has taken power or a system out.", 120, 300, 1500, IsEmergency: true),
        ],
        Knowledge:
        [
            new("BusinessInfo", "About Johnsons Electric House",
                "Johnsons Electric House is an electrical and renewable-energy services company working on " +
                "commercial, industrial and energy projects. The work covers installation, maintenance, " +
                "repair and troubleshooting of electrical systems, delivered alongside the other project " +
                "teams and contractors on a site."),
            new("BusinessInfo", "Renewable energy work",
                "As well as traditional commercial and industrial electrical work, we handle the electrical " +
                "side of renewable generation and storage: solar PV systems, battery energy storage (BESS) " +
                "and wind energy electrical systems — installation, connection, testing and commissioning."),
            new("BusinessInfo", "Safety, code and documentation",
                "Electrical safety, the applicable codes and regulations, accurate records and inspection all " +
                "sit at the centre of how the work is done. Installations are documented, inspected and " +
                "maintained preventively rather than only when something fails."),
            new("FAQ", "Do you work on homes?",
                "Our work is commercial, industrial and renewable-energy projects rather than domestic " +
                "electrical work. Take the caller's details and pass a residential enquiry to the office " +
                "rather than turning it away on the call."),
            new("FAQ", "What happens when I book a visit?",
                "A first visit is an assessment: an engineer attends, establishes what the system is doing " +
                "and what it needs, and the work is quoted from that. Projects are scoped and priced " +
                "properly rather than over the phone."),
            new("Policy", "How work is quoted",
                "Callout, inspection and maintenance visits are quoted from the price list. Installation and " +
                "renewable-energy projects are quoted after a site assessment, because the price turns on " +
                "the scope, the access and the electrical infrastructure already there. Never commit to a " +
                "project figure on a call."),
            new("EmergencyRule", "Live faults, shocks, burning smells and downed lines",
                "If anyone has had a shock, or there is smoke, a burning smell or fire, the caller should " +
                "hang up and dial 911 before anything else. A downed or arcing power line is the utility's " +
                "emergency line, not ours, and the caller should stay well back from it. Where it is safe " +
                "to do so, advise isolating the affected circuit at the breaker and leaving it off until an " +
                "engineer attends. Never talk a caller through work on live equipment."),
        ]);

    /// <summary>
    /// Quick Plumb — residential plumbing, repairs and maintenance.
    ///
    /// Publishes a service area only as "its local area", which is not something coverage rules can
    /// be built from, so this tenant has none. Emergency work is flagged on the services it applies
    /// to, which is what puts a same-day job in front of the owner.
    /// </summary>
    private static TenantBlueprint QuickPlumb => new(
        BusinessName: "Quick Plumb",
        Industry: "Plumbing",
        AdminEmail: "admin@quickplumb.com",
        Password: "QuickPlumb#Flow2026",
        AdminName: "Quick Plumb Administrator",
        Currency: "USD",
        Timezone: "America/New_York",
        // Domestic call-out hours: a full weekday, a short Saturday, Sunday shut.
        BusinessHoursJson: """
            {"mon":"08:00-18:00","tue":"08:00-18:00","wed":"08:00-18:00","thu":"08:00-18:00","fri":"08:00-18:00","sat":"09:00-15:00","sun":"closed"}
            """.Trim(),
        Greeting: "Quick Plumb, you're through to our assistant. " +
                  "Is this something leaking or urgent right now, or would you like to book a visit?",
        // Three plumbers out at once — a working figure, not a published one.
        MaxConcurrent: 3,
        TransferNumber: PlaceholderTransfer,
        Services:
        [
            new("Emergency Plumbing Callout", "Urgent attendance for leaks, bursts and anything that cannot wait.", 90, 140, 400, IsEmergency: true),
            new("Plumbing Repair", "General repairs to household plumbing, diagnosed and fixed on the visit where possible.", 60, 95, 280),
            new("Leak Detection & Repair", "Finding a leak with proper equipment before anything is opened up, then repairing it.", 90, 130, 450, IsEmergency: true),
            new("Drain Cleaning", "Clearing blocked and slow drains, including machine clearing where needed.", 60, 110, 320),
            new("Water Heater Repair", "Diagnosis and repair of storage and tankless water heaters.", 90, 140, 480),
            new("Water Heater Installation", "Supply and fit of a replacement water heater, including removal of the old unit.", 180, 850, 2200),
            new("Sewer Line Services", "Sewer line inspection, clearing and repair.", 150, 250, 1800),
            new("Repiping", "Replacing failing or outdated pipework through part or all of a property.", 480, 1800, 8000),
            new("Water Line Replacement", "Replacing the main water line into the property.", 300, 900, 3500),
            new("Fixture Installation", "Fitting taps, sinks, toilets, showers and similar fixtures.", 90, 120, 450),
            new("Plumbing Maintenance", "A planned visit to check the system over and deal with small faults before they grow.", 60, 90, 220),
        ],
        Knowledge:
        [
            new("BusinessInfo", "About Quick Plumb",
                "Quick Plumb is a professional plumbing company handling residential plumbing, repairs and " +
                "maintenance. The work covers everything from a dripping tap to repiping a property, done by " +
                "experienced plumbers with modern diagnostic tools and equipment."),
            new("BusinessInfo", "Where we work",
                "Quick Plumb serves residential customers throughout its local service area. If a caller is " +
                "outside it, take their address and details and have the office confirm rather than turning " +
                "them away on the call."),
            new("FAQ", "How soon can someone come out?",
                "Urgent problems — a burst pipe, a leak that is doing damage, no water — are treated as " +
                "emergency callouts and fitted in the same day wherever there is a plumber free. Repairs, " +
                "installations and maintenance are booked into the diary at a time that suits the customer."),
            new("FAQ", "What should I do while I wait?",
                "For anything leaking, the first step is to turn the water off at the main stop valve, and to " +
                "put a bucket and towels under the leak. For a leak near electrics, the power to that area " +
                "should be switched off at the breaker before anyone goes near it."),
            new("FAQ", "What will it cost?",
                "Callouts, repairs and installations are quoted from the price list, as a range — what it " +
                "lands at depends on what is found once the plumber is there. Larger jobs such as repiping " +
                "or a water line replacement are quoted properly after the plumber has seen the property."),
            new("Policy", "Booking, access and cancellations",
                "Somebody over 18 needs to be at the property for the visit, and the plumber needs access to " +
                "the stop valve and the affected fixtures. Appointments can be moved or cancelled free of " +
                "charge up to 12 hours beforehand."),
            new("EmergencyRule", "Gas, flooding and water near electrics",
                "If the caller smells gas, tell them to hang up, leave the property and ring the gas " +
                "emergency line or 911 — we are not a gas emergency service. Where water is running near " +
                "electrical outlets or a consumer unit, they should stay out of the area and switch the power " +
                "off at the breaker only if it is safe to reach. For serious flooding, the water goes off at " +
                "the main stop valve first and the booking comes second."),
        ]);
}
