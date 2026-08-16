using AiReceptionist.SuperAdmin.Models;
using Dapper;

namespace AiReceptionist.SuperAdmin.Data.Repositories;

public interface IPlatformAuthRepository
{
    /// <summary>Only ever returns SuperAdmin accounts: a tenant user's credentials must not open
    /// the platform console even though both live in the same Users table.</summary>
    Task<PlatformUser?> FindSuperAdminByEmailAsync(string email);
}

public class PlatformAuthRepository : IPlatformAuthRepository
{
    private readonly IDbConnectionFactory _db;
    public PlatformAuthRepository(IDbConnectionFactory db) => _db = db;

    public async Task<PlatformUser?> FindSuperAdminByEmailAsync(string email)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<PlatformUser>(@"
            SELECT Id, OrganizationId, FullName, Email, PasswordHash, Role
            FROM Users
            WHERE Email = @email AND Role = 'SuperAdmin' AND IsDeleted = 0", new { email });
    }
}
