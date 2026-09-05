using AiReceptionist.SuperAdmin.Models;
using Dapper;

namespace AiReceptionist.SuperAdmin.Data.Repositories;

/// <summary>
/// The sign-in accounts tenants use, from the platform operator's side.
///
/// Tenants normally create their own account by registering, which makes the first user an
/// OrgAdmin and nothing else. That leaves the operator unable to do the two things they are asked
/// for most: stand up a customer who was sold over the phone, and add a second person to an
/// account whose only admin has left. Both are account *creation*, which is why they live here
/// rather than behind the tenant API.
///
/// Passwords are hashed with the same library and the same default work factor the tenant API
/// uses, so an account made here is indistinguishable from one made by registering.
/// </summary>
public interface IUserAccountRepository
{
    /// <summary>Tenant sign-in accounts, newest first. Platform operator accounts are excluded:
    /// they are not "an account against an organization" in any sense a customer would recognise,
    /// and listing them here would invite editing them here.</summary>
    Task<IReadOnlyList<UserAccountRow>> ListAsync(string? search = null, int? orgId = null);

    /// <summary>Matches the tenant API's own check, including its filtered unique index: a soft
    /// deleted account frees its address for reuse.</summary>
    Task<bool> EmailExistsAsync(string email);

    /// <summary>Every organization an account can be attached to, for the picker.</summary>
    Task<IReadOnlyList<OrganizationOption>> ListOrganizationOptionsAsync();

    Task<OrganizationOption?> FindOrganizationAsync(int orgId);

    /// <summary>Creates the account, and the organization first when one is being created with it.
    /// Both in one transaction: a half-made customer with an organization nobody can sign in to is
    /// worse than a clean failure.</summary>
    Task<NewAccountResult> CreateAsync(NewAccount account);
}

/// <summary>What the caller has decided to create, with the organization question already
/// settled one way or the other.</summary>
public class NewAccount
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = AccountRoles.OrgAdmin;

    /// <summary>Set when attaching to an organization that already exists.</summary>
    public int? OrganizationId { get; set; }

    /// <summary>Set instead when one is being created alongside the account.</summary>
    public NewOrganization? Organization { get; set; }
}

public class NewOrganization
{
    public string Name { get; set; } = "";
    public string Industry { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
}

public record NewAccountResult(int UserId, int OrganizationId, string OrganizationName);

public class UserAccountRepository : IUserAccountRepository
{
    private readonly IDbConnectionFactory _db;
    public UserAccountRepository(IDbConnectionFactory db) => _db = db;

    public async Task<IReadOnlyList<UserAccountRow>> ListAsync(string? search = null, int? orgId = null)
    {
        using var conn = _db.Create();

        var sql = @"
            SELECT u.Id, u.OrganizationId, u.FullName, u.Email, u.Role, u.EmailVerified, u.CreatedAt,
                   o.Name AS OrganizationName, o.IsActive AS OrganizationActive
            FROM Users u
            JOIN Organizations o ON o.Id = u.OrganizationId
            WHERE u.IsDeleted = 0 AND o.IsDeleted = 0 AND u.Role <> 'SuperAdmin'";

        if (orgId is > 0) sql += " AND u.OrganizationId = @orgId";
        if (!string.IsNullOrWhiteSpace(search))
            sql += " AND (u.FullName LIKE @term OR u.Email LIKE @term OR o.Name LIKE @term)";

        sql += " ORDER BY u.CreatedAt DESC, u.Id DESC";

        var rows = await conn.QueryAsync<UserAccountRow>(sql,
            new { orgId, term = $"%{search?.Trim()}%" });
        return rows.ToList();
    }

    public async Task<bool> EmailExistsAsync(string email)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Users WHERE Email = @email AND IsDeleted = 0", new { email }) > 0;
    }

    public async Task<IReadOnlyList<OrganizationOption>> ListOrganizationOptionsAsync()
    {
        using var conn = _db.Create();
        var rows = await conn.QueryAsync<OrganizationOption>(@"
            SELECT o.Id, o.Name, o.IsActive,
                   (SELECT COUNT(*) FROM Users u
                      WHERE u.OrganizationId = o.Id AND u.IsDeleted = 0 AND u.Role <> 'SuperAdmin') AS UserCount
            FROM Organizations o
            WHERE o.IsDeleted = 0
            ORDER BY o.Name");
        return rows.ToList();
    }

    public async Task<OrganizationOption?> FindOrganizationAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<OrganizationOption>(@"
            SELECT o.Id, o.Name, o.IsActive,
                   (SELECT COUNT(*) FROM Users u
                      WHERE u.OrganizationId = o.Id AND u.IsDeleted = 0 AND u.Role <> 'SuperAdmin') AS UserCount
            FROM Organizations o
            WHERE o.Id = @orgId AND o.IsDeleted = 0", new { orgId });
    }

    public async Task<NewAccountResult> CreateAsync(NewAccount account)
    {
        using var conn = _db.Create();
        conn.Open();
        using var tx = conn.BeginTransaction();

        int orgId;
        string orgName;

        if (account.Organization is { } newOrg)
        {
            // The same columns the tenant's own sign-up writes, so an organization created here
            // starts life identical to one that registered itself — everything else on the row has
            // a schema default, and the tenant fills the rest in from its Settings screen.
            orgId = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO Organizations (Name, Industry, Phone, Email)
                OUTPUT INSERTED.Id
                VALUES (@Name, @Industry, @Phone, @Email)", newOrg, tx);
            orgName = newOrg.Name;
        }
        else
        {
            orgId = account.OrganizationId!.Value;

            // Read inside the transaction: the organization is being vouched for by an id that was
            // chosen on a screen rendered some time ago, and it may have been deleted since.
            orgName = await conn.ExecuteScalarAsync<string?>(
                "SELECT Name FROM Organizations WHERE Id = @orgId AND IsDeleted = 0",
                new { orgId }, tx)
                ?? throw new InvalidOperationException(
                    "That organization no longer exists. Refresh the list and try again.");
        }

        var userId = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Users (OrganizationId, FullName, Email, PasswordHash, Role, EmailVerified)
            OUTPUT INSERTED.Id
            VALUES (@orgId, @FullName, @Email, @PasswordHash, @Role, 1)",
            new { orgId, account.FullName, account.Email, account.PasswordHash, account.Role }, tx);

        tx.Commit();
        return new NewAccountResult(userId, orgId, orgName);
    }
}
