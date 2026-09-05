using Dapper;
using Microsoft.Data.SqlClient;

namespace AiReceptionist.SuperAdmin.Data;

/// <summary>Creates the platform-owned tables and columns inside the existing AiDB (idempotent).
/// The tenant API owns the database itself and the tenant schema; this only adds what the platform
/// needs, so the two can start in either order once AiDB exists. The statements themselves live in
/// <see cref="Shared.BillingSchema"/>, which the tenant API compiles and runs as well — both apps
/// write these tables, so neither may depend on the other having started first.</summary>
public static class PlatformDbInitializer
{
    public static void Initialize(IConfiguration configuration, ILogger logger)
    {
        var connectionString = configuration.GetConnectionString("AiDB")
            ?? throw new InvalidOperationException("Connection string 'AiDB' is not configured.");

        using var db = new SqlConnection(connectionString);
        db.Open();

        if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'Organizations'") == 0)
        {
            logger.LogWarning(
                "AiDB has no Organizations table yet. Start the tenant API (AiReceptionist.Api) once to " +
                "create the database and tenant schema, then restart this app.");
            return;
        }

        // Subscriptions, payments, tiers, Stripe linkage, usage periods and mirrored invoices.
        // Shared with the tenant API, which runs the same statements — whichever app starts first
        // creates them, and each statement is its own batch so a column added by one is visible
        // to the next.
        foreach (var statement in Shared.BillingSchema.Statements)
            db.Execute(statement);

        // The public marketing site's editable copy. Console-only, so unlike the billing tables
        // it is not shared with the tenant API — nothing else reads or writes it.
        foreach (var statement in LandingSchema.Statements)
            db.Execute(statement);

        // Sign-in reuses the shared Users table, so without a SuperAdmin row nobody can get in.
        // The tenant API seeds one on first run; say so rather than failing silently at the login form.
        var superAdmins = db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Users WHERE Role = 'SuperAdmin' AND IsDeleted = 0");
        if (superAdmins == 0)
            logger.LogWarning(
                "No SuperAdmin user exists in AiDB, so nobody can sign in here. The tenant API seeds " +
                "superadmin@demo.com on first run, or insert a Users row with Role = 'SuperAdmin'.");

        logger.LogInformation("Platform schema is ready ({Count} SuperAdmin account(s)).", superAdmins);
    }
}
