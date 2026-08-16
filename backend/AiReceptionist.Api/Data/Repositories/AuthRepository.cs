using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface IAuthRepository
{
    Task<User?> FindByEmailAsync(string email);
    Task<User?> FindByIdAsync(int id);
    Task StoreRefreshTokenAsync(RefreshToken token);
    Task<RefreshToken?> FindRefreshTokenAsync(string token);
    Task RevokeRefreshTokenAsync(string token);

    /// <summary>Revokes every outstanding refresh token for a user. Used when a already-revoked
    /// token is replayed: that means the token leaked, so the whole family is burned.</summary>
    Task RevokeAllForUserAsync(int userId);

    Task UpdatePasswordAsync(int userId, string passwordHash);

    /// <summary>True if an active user already owns this email (case-insensitive, matches the
    /// unique index on Users.Email WHERE IsDeleted = 0).</summary>
    Task<bool> EmailExistsAsync(string email);

    /// <summary>Registers a new client business: inserts the Organization and its first admin
    /// User in a single transaction so a half-created tenant can never be left behind. Returns the
    /// created user with its generated Id and OrganizationId populated.</summary>
    Task<User> RegisterOrganizationAsync(Organization org, User admin);

    /// <summary>False when the super admin console has disabled the organization (typically for
    /// non-payment). Checked at sign-in and at token refresh so a disabled tenant loses access
    /// within one access-token lifetime.</summary>
    Task<bool> IsOrganizationActiveAsync(int orgId);
}

public class AuthRepository : IAuthRepository
{
    private readonly IDbConnectionFactory _db;
    public AuthRepository(IDbConnectionFactory db) => _db = db;

    public async Task<User?> FindByEmailAsync(string email)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<User>(
            "SELECT * FROM Users WHERE Email = @email AND IsDeleted = 0", new { email });
    }

    public async Task<User?> FindByIdAsync(int id)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<User>(
            "SELECT * FROM Users WHERE Id = @id AND IsDeleted = 0", new { id });
    }

    public async Task<bool> IsOrganizationActiveAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT ISNULL(IsActive, 1) FROM Organizations WHERE Id = @orgId AND IsDeleted = 0",
            new { orgId });
    }

    // Refresh tokens are stored as SHA-256 hashes (see TokenHash) — the raw value never
    // touches the database, so a leaked backup cannot be replayed as a live session.
    public async Task StoreRefreshTokenAsync(RefreshToken token)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "INSERT INTO RefreshTokens (UserId, Token, ExpiresAt) VALUES (@UserId, @Token, @ExpiresAt)",
            new { token.UserId, Token = TokenHash.Compute(token.Token), token.ExpiresAt });
    }

    public async Task<RefreshToken?> FindRefreshTokenAsync(string token)
    {
        using var conn = _db.Create();
        return await conn.QueryFirstOrDefaultAsync<RefreshToken>(
            "SELECT TOP 1 * FROM RefreshTokens WHERE Token = @hash ORDER BY Id DESC",
            new { hash = TokenHash.Compute(token) });
    }

    public async Task RevokeRefreshTokenAsync(string token)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("UPDATE RefreshTokens SET Revoked = 1 WHERE Token = @hash",
            new { hash = TokenHash.Compute(token) });
    }

    public async Task RevokeAllForUserAsync(int userId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE RefreshTokens SET Revoked = 1 WHERE UserId = @userId AND Revoked = 0", new { userId });
    }

    public async Task UpdatePasswordAsync(int userId, string passwordHash)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("UPDATE Users SET PasswordHash = @passwordHash WHERE Id = @userId",
            new { userId, passwordHash });
    }

    public async Task<bool> EmailExistsAsync(string email)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Users WHERE Email = @email AND IsDeleted = 0", new { email }) > 0;
    }

    public async Task<User> RegisterOrganizationAsync(Organization org, User admin)
    {
        using var conn = _db.Create();
        conn.Open();
        using var tx = conn.BeginTransaction();

        var orgId = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Organizations (Name, Industry, Phone, Email)
            OUTPUT INSERTED.Id
            VALUES (@Name, @Industry, @Phone, @Email)", org, tx);

        admin.OrganizationId = orgId;
        admin.Id = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Users (OrganizationId, FullName, Email, PasswordHash, Role, EmailVerified)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @FullName, @Email, @PasswordHash, @Role, 0)", admin, tx);

        tx.Commit();
        return admin;
    }
}
