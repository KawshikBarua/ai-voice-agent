using AiReceptionist.Api.Domain;
using AiReceptionist.Shared;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiReceptionist.Api.Data;

/// <summary>
/// Stands up test tenants frozen in a particular billing state, so the screens and the call gate
/// can be checked against it without waiting a month for the clock to get there.
///
/// Every date is worked out from today, never written down: a scenario pinned to a calendar date
/// is only the scenario it claims to be for the week it was written. Re-running a scenario rebuilds
/// its billing from scratch against the current date, so it can be put back after anything —
/// recording a payment in the console, say — has moved it on.
///
/// Run with: <c>dotnet run --project backend/AiReceptionist.Api -- seed-scenario overdue</c>
/// </summary>
public static class ScenarioSeeder
{
    public static readonly string[] Names = ["overdue"];

    public static async Task<int> RunAsync(string? name, IConfiguration config, ILogger logger)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "overdue":
                await Overdue.RunAsync(config, logger);
                return 0;
            default:
                Console.WriteLine($"Unknown scenario '{name}'. Available: {string.Join(", ", Names)}.");
                return 1;
        }
    }

    /// <summary>
    /// A customer who has used every minute of their allowance, has not paid the last renewal, and
    /// is past the grace days on top of it.
    ///
    /// The shape, in the order it happened:
    /// <list type="number">
    /// <item>Two months ago they subscribed and paid. The first period came in under the allowance.</item>
    /// <item>A month ago they paid again, then ran past the allowance; that overrun was totalled at
    /// the close and is waiting on the next bill.</item>
    /// <item>The renewal that fell due 19 days ago was never paid. The grace days ran out 12 days
    /// ago, and from then on the agent stopped answering — so the current period's minutes, all of
    /// the allowance and then some, were all run up in the week before the cut-off.</item>
    /// </list>
    ///
    /// Nothing here sets a "blocked" flag. The account is active and its agent is not restricted;
    /// the call gate reaches its own verdict from the subscription, which is the point of seeding
    /// it this way. What the closes would have produced (the usage periods and the carried overage)
    /// is filed with the same arithmetic <c>BillingService</c> uses, so the first page load finds
    /// nothing left to close.
    /// </summary>
    private static class Overdue
    {
        private const string BusinessName = "Harbor Lane Auto Repair";
        // .test is reserved for testing, so this can never reach a real inbox.
        private const string AdminEmail = "admin@harborlane-auto.test";
        private const string Password = "HarborLane#Overdue2026";

        private const int DaysSinceRenewalDue = 19;
        private const int GraceDays = 7;

        /// <summary>Minutes either side of the allowance each period ends on.</summary>
        private const int FirstPeriodUnder = 32;
        private const int SecondPeriodOver = 61;
        private const int CurrentPeriodOver = 27;

        public static async Task RunAsync(IConfiguration config, ILogger logger)
        {
            await using var db = new SqlConnection(config.GetConnectionString("AiDB"));
            await db.OpenAsync();

            var plan = await MeteredPlanAsync(db);

            var orgId = await db.ExecuteScalarAsync<int?>(
                "SELECT TOP 1 OrganizationId FROM Users WHERE Email=@AdminEmail AND IsDeleted=0",
                new { AdminEmail });

            var rebuilt = orgId is not null;
            orgId ??= await CreateTenantAsync(db);

            // Only the records here are reset. A Stripe subscription a previous run paid for is
            // still live in Stripe, carries this organization's id, and will be found again by the
            // reconciliation sweep — which records its payments and moves access on as if the
            // scenario had never been reset.
            var stripeSubscription = await db.ExecuteScalarAsync<string?>(
                "SELECT StripeSubscriptionId FROM OrganizationSubscriptions WHERE OrganizationId=@orgId",
                new { orgId });
            if (!string.IsNullOrWhiteSpace(stripeSubscription))
                logger.LogWarning(
                    "This account was subscribed in Stripe ({SubscriptionId}). Cancel that subscription in " +
                    "the Stripe dashboard, or its invoices will be applied to this account again.",
                    stripeSubscription);

            await using var tx = (SqlTransaction)await db.BeginTransactionAsync();
            await ClearBillingAsync(db, tx, orgId.Value);
            var timeline = await SeedBillingAsync(db, tx, orgId.Value, plan);
            await tx.CommitAsync();

            logger.LogInformation("Seeded the overdue scenario on organization {OrgId}.", orgId);
            Report(orgId.Value, rebuilt, plan, timeline);
        }

        private sealed record Plan(int Id, string Name, string Currency, decimal Amount,
            string BillingCycle, int IncludedMinutes, decimal OverageRatePerMinute);

        private sealed record Timeline(DateTime Started, DateTime FirstEnd, DateTime RenewalDue,
            DateTime GraceEnded, DateTime CurrentEnd, int SecondOverMinutes, decimal SecondOverAmount,
            int CurrentMinutes);

        /// <summary>
        /// The tier the scenario sits on. It has to be metered — "used all the minutes" means
        /// nothing on a plan with no allowance — so the cheapest metered tier published is used,
        /// and the demo Starter tier is created only when there is none.
        /// </summary>
        private static async Task<Plan> MeteredPlanAsync(SqlConnection db)
        {
            const string select = @"
                SELECT TOP 1 Id, Name, Currency, Amount, BillingCycle, IncludedMinutes, OverageRatePerMinute
                FROM PricingPlans
                WHERE IsActive = 1 AND IsDeleted = 0 AND IncludedMinutes > 0 AND OverageRatePerMinute > 0
                ORDER BY CASE WHEN BillingCycle = 'Monthly' THEN 0 ELSE 1 END, Amount, SortOrder, Id";

            var plan = await db.QueryFirstOrDefaultAsync<Plan>(select);
            if (plan is not null) return plan;

            await db.ExecuteAsync(@"
                INSERT INTO PricingPlans
                    (Name, Description, Currency, Amount, BillingCycle, IncludedMinutes,
                     OverageRatePerMinute, SortOrder, IsActive)
                VALUES ('Starter', 'Demo tier — 500 AI minutes a month.', 'USD', 49.00, 'Monthly',
                        500, 0.1200, 1, 1);");
            return await db.QueryFirstAsync<Plan>(select);
        }

        private static async Task<int> CreateTenantAsync(SqlConnection db)
        {
            var orgId = await db.ExecuteScalarAsync<int>(@"
                INSERT INTO Organizations
                    (Name, Industry, Address, Phone, Email, Currency, Timezone, BusinessHoursJson,
                     MaxConcurrentAppointments, ProductsEnabled, OnboardingCompleted)
                OUTPUT INSERTED.Id
                VALUES (@BusinessName, 'Auto Repair', '42 Harbor Lane, Springfield', '+1 555 010 0142',
                        @AdminEmail, 'USD', 'America/New_York',
                        '{""mon"":""08:00-18:00"",""tue"":""08:00-18:00"",""wed"":""08:00-18:00"",""thu"":""08:00-18:00"",""fri"":""08:00-18:00"",""sat"":""09:00-13:00""}',
                        3, 0, 1);",
                new { BusinessName, AdminEmail });

            await db.ExecuteAsync(@"
                INSERT INTO Users (OrganizationId, FullName, Email, PasswordHash, Role, EmailVerified)
                VALUES (@orgId, 'Harbor Lane Administrator', @AdminEmail, @hash, @role, 1);",
                new { orgId, AdminEmail, role = Roles.OrgAdmin, hash = BCrypt.Net.BCrypt.HashPassword(Password) });

            await db.ExecuteAsync(@"
                INSERT INTO Services (OrganizationId, Name, Description, DurationMinutes, MinPrice, MaxPrice, IsEmergency) VALUES
                (@orgId, 'Oil Change', 'Synthetic or conventional oil and filter change.', 45, 49, 99, 0),
                (@orgId, 'Brake Inspection & Repair', 'Pads, rotors and fluid checked; parts quoted before work starts.', 90, 120, 450, 0),
                (@orgId, 'Diagnostic Check', 'Warning lights and fault codes read and explained.', 60, 80, 140, 0);

                INSERT INTO Customers (OrganizationId, Name, Phone) VALUES
                (@orgId, 'Marcus Reed', '+1 555 010 0151'),
                (@orgId, 'Priya Nair', '+1 555 010 0152'),
                (@orgId, 'Tom Becker', '+1 555 010 0153');

                INSERT INTO AgentConfig (OrganizationId, Voice, Language, Greeting, TransferNumber, Enabled, EnabledToolsJson)
                VALUES (@orgId, 'retell-Grace', 'en-US',
                        N'Thanks for calling Harbor Lane Auto Repair — you''re through to our assistant. How can I help?',
                        '+1 555 010 0199', 1,
                        '[""identify_customer"",""check_availability"",""book_appointment"",""cancel_appointment"",""reschedule_appointment"",""check_appointment""]');",
                new { orgId });

            return orgId;
        }

        /// <summary>Takes the scenario's billing back to nothing, so a re-run is anchored on today
        /// rather than stacked on top of the last one. Only ever this one organization.</summary>
        private static async Task ClearBillingAsync(SqlConnection db, SqlTransaction tx, int orgId) =>
            await db.ExecuteAsync(@"
                DELETE s FROM CallActionSuggestions s
                JOIN CallLogs cl ON cl.Id = s.CallLogId WHERE cl.OrganizationId = @orgId;
                DELETE FROM CallLogs WHERE OrganizationId = @orgId;
                DELETE FROM OrganizationUsagePeriods WHERE OrganizationId = @orgId;
                DELETE FROM OrganizationPayments WHERE OrganizationId = @orgId;
                DELETE FROM OrganizationPaymentAttempts WHERE OrganizationId = @orgId;
                DELETE l FROM OrganizationInvoiceLines l
                JOIN OrganizationInvoices i ON i.Id = l.InvoiceId WHERE i.OrganizationId = @orgId;
                DELETE FROM OrganizationInvoices WHERE OrganizationId = @orgId;
                DELETE FROM OrganizationSubscriptions WHERE OrganizationId = @orgId;
                UPDATE Organizations
                SET IsActive = 1, SuspendedAt = NULL, SuspendedReason = NULL,
                    AgentRestricted = 0, AgentRestrictedAt = NULL, AgentRestrictedReason = NULL,
                    TrialStartedAt = NULL, TrialEndsAt = NULL
                WHERE Id = @orgId;",
                new { orgId }, tx);

        private static async Task<Timeline> SeedBillingAsync(SqlConnection db, SqlTransaction tx,
            int orgId, Plan plan)
        {
            // Whole days, so the dates read cleanly on every screen. The renewal is placed first
            // and the start derived from it by whole cycles, which is how the windows are built.
            var today = DateTime.UtcNow.Date;
            var renewalTarget = today.AddDays(-DaysSinceRenewalDue);
            var started = BillingCycles.IsYearly(plan.BillingCycle)
                ? renewalTarget.AddYears(-2) : renewalTarget.AddMonths(-2);
            var firstEnd = BillingCycles.Advance(started, plan.BillingCycle);
            var renewalDue = BillingCycles.Advance(firstEnd, plan.BillingCycle);
            var graceEnded = renewalDue.AddDays(GraceDays);
            var currentEnd = BillingCycles.Advance(renewalDue, plan.BillingCycle);

            var firstMinutes = plan.IncludedMinutes - FirstPeriodUnder;
            var secondMinutes = plan.IncludedMinutes + SecondPeriodOver;
            var currentMinutes = plan.IncludedMinutes + CurrentPeriodOver;

            var customers = (await db.QueryAsync<int>(
                "SELECT Id FROM Customers WHERE OrganizationId=@orgId AND IsDeleted=0 ORDER BY Id",
                new { orgId }, tx)).ToArray();

            // Calls up to each closed period are stamped as counted by it, exactly as the close
            // would have stamped them. The current period's are left unclaimed: they are the
            // "used this period" figure, and the next bill.
            await InsertCallsAsync(db, tx, orgId, customers, started, firstEnd, firstMinutes, firstEnd);
            await InsertCallsAsync(db, tx, orgId, customers, firstEnd, renewalDue, secondMinutes, renewalDue);
            await InsertCallsAsync(db, tx, orgId, customers, renewalDue, graceEnded, currentMinutes, null);

            var secondOverAmount = StripeMoney.Round(SecondPeriodOver * plan.OverageRatePerMinute, plan.Currency);

            await db.ExecuteAsync(@"
                INSERT INTO OrganizationUsagePeriods
                    (OrganizationId, PeriodStart, PeriodEnd, PlanName, BaseAmount, Currency,
                     IncludedMinutes, MinutesUsed, OverageMinutes, OverageRatePerMinute, OverageAmount,
                     Status, ClosedAt)
                VALUES
                    (@orgId, @started, @firstEnd, @Name, @Amount, @Currency,
                     @IncludedMinutes, @firstMinutes, 0, @OverageRatePerMinute, 0, @billed, @firstEnd),
                    (@orgId, @firstEnd, @renewalDue, @Name, @Amount, @Currency,
                     @IncludedMinutes, @secondMinutes, @overMinutes, @OverageRatePerMinute, @secondOverAmount,
                     @pending, @renewalDue);

                -- The access window stops at the renewal that was never paid; that, plus the grace
                -- days, is the line the call gate reads.
                INSERT INTO OrganizationSubscriptions
                    (OrganizationId, PlanId, PlanName, BillingCycle, Amount, Currency, StartedAt,
                     CurrentPeriodStart, CurrentPeriodEnd, GraceDays, AutoSuspend, IncludedMinutes,
                     OverageRatePerMinute, PendingOverageMinutes, PendingOverageAmount, Notes)
                VALUES
                    (@orgId, @Id, @Name, @BillingCycle, @Amount, @Currency, @started,
                     @firstEnd, @renewalDue, @graceDays, 1, @IncludedMinutes,
                     @OverageRatePerMinute, @overMinutes, @secondOverAmount,
                     'Test scenario: allowance used up, renewal unpaid, past the grace period.');

                -- Both earlier periods were paid; the renewal is the one missing.
                INSERT INTO OrganizationPayments
                    (OrganizationId, Amount, Currency, PaidAt, PeriodStart, PeriodEnd, Method, Reference, Source)
                VALUES
                    (@orgId, @Amount, @Currency, @started, @started, @firstEnd, 'BankTransfer', 'SCENARIO-1', @manual),
                    (@orgId, @Amount, @Currency, DATEADD(day, -1, @firstEnd), @firstEnd, @renewalDue,
                     'BankTransfer', 'SCENARIO-2', @manual);",
                new
                {
                    orgId, plan.Id, plan.Name, plan.Amount, plan.Currency, plan.BillingCycle,
                    plan.IncludedMinutes, plan.OverageRatePerMinute,
                    started, firstEnd, renewalDue, firstMinutes, secondMinutes,
                    overMinutes = SecondPeriodOver, secondOverAmount, graceDays = GraceDays,
                    billed = UsagePeriodStatus.Billed, pending = UsagePeriodStatus.Pending,
                    manual = PaymentSources.Manual,
                }, tx);

            return new Timeline(started, firstEnd, renewalDue, graceEnded, currentEnd,
                SecondPeriodOver, secondOverAmount, currentMinutes);
        }

        /// <summary>
        /// Calls spread across business hours in <paramref name="from"/>–<paramref name="to"/>,
        /// adding up to exactly <paramref name="minutes"/> of talk time. Durations vary, but are
        /// fixed by the call's position so every run produces the same figures.
        /// </summary>
        private static async Task InsertCallsAsync(SqlConnection db, SqlTransaction tx, int orgId,
            int[] customers, DateTime from, DateTime to, int minutes, DateTime? billedPeriodEnd)
        {
            var days = Math.Max(1, (int)(to - from).TotalDays);
            var remaining = minutes * 60;
            var calls = new List<object>();

            for (var i = 0; remaining > 0; i++)
            {
                var seconds = Math.Min(remaining, 95 + (i * 53) % 330);
                remaining -= seconds;

                var day = i % days;
                var hour = 8 + (i * 5 + i / days) % 10;
                var minute = (i * 17) % 60;

                calls.Add(new
                {
                    orgId,
                    customerId = customers.Length > 0 && i % 3 != 2 ? customers[i % customers.Length] : (int?)null,
                    from = $"+1 555 010 0{160 + i % 40}",
                    status = i % 9 == 4 ? "Transferred" : "Completed",
                    seconds,
                    startedAt = from.AddDays(day).AddHours(hour).AddMinutes(minute),
                    billedPeriodEnd,
                });
            }

            await db.ExecuteAsync(@"
                INSERT INTO CallLogs (OrganizationId, CustomerId, FromNumber, Status, DurationSeconds,
                                      Summary, StartedAt, CreatedAt, BilledPeriodEnd)
                VALUES (@orgId, @customerId, @from, @status, @seconds,
                        'Caller enquiry handled by the AI receptionist.', @startedAt, @startedAt,
                        @billedPeriodEnd);",
                calls, tx);
        }

        private static void Report(int orgId, bool rebuilt, Plan plan, Timeline t)
        {
            var c = plan.Currency;
            Console.WriteLine();
            Console.WriteLine(rebuilt
                ? $"Overdue scenario rebuilt for {BusinessName} (organization {orgId}), anchored on today."
                : $"Overdue scenario created: {BusinessName} (organization {orgId}).");
            Console.WriteLine();
            Console.WriteLine($"  Sign in:  {AdminEmail}   (password in {nameof(ScenarioSeeder)}.cs)");
            Console.WriteLine();
            Console.WriteLine($"  Plan:            {plan.Name}, {plan.Amount:0.00} {c} {plan.BillingCycle.ToLowerInvariant()}, " +
                              $"{plan.IncludedMinutes} min included, {plan.OverageRatePerMinute:0.00##} {c}/min over");
            Console.WriteLine($"  Subscribed:      {t.Started:d MMM yyyy} — two periods paid");
            Console.WriteLine($"  Renewal due:     {t.RenewalDue:d MMM yyyy} — NOT paid");
            Console.WriteLine($"  Grace ended:     {t.GraceEnded:d MMM yyyy} ({GraceDays} days) — calls refused since");
            Console.WriteLine($"  Carried overage: {t.SecondOverMinutes} min = {t.SecondOverAmount:0.00} {c} from the last closed period");
            Console.WriteLine($"  This period:     {t.CurrentMinutes} of {plan.IncludedMinutes} min used (0 left), " +
                              $"closes {t.CurrentEnd:d MMM yyyy}");
            Console.WriteLine();
            Console.WriteLine("Re-run this command at any time to put the account back into this state.");
            Console.WriteLine();
        }
    }
}
