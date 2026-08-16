using System.Data;
using Microsoft.Data.SqlClient;

namespace AiReceptionist.Api.Data;

public interface IDbConnectionFactory
{
    IDbConnection Create();
}

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
