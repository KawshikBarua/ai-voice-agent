using System.Data;
using Microsoft.Data.SqlClient;

namespace AiReceptionist.SuperAdmin.Data;

public interface IDbConnectionFactory
{
    IDbConnection Create();
}

/// <summary>Opens connections to the same AiDB the tenant API uses — the super admin app is a
/// second front end over one database, not a separate store.</summary>
public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("AiDB")
            ?? throw new InvalidOperationException("Connection string 'AiDB' is not configured.");
    }

    public IDbConnection Create() => new SqlConnection(_connectionString);
}
