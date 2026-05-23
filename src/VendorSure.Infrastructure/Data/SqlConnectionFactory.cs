using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using VendorSure.Services.Data;

namespace VendorSure.Infrastructure.Data;

/// <summary>
/// SQL Server implementation of <see cref="IDbConnectionFactory"/>.
/// </summary>
/// <remarks>
/// The connection string is resolved lazily on first connection attempt
/// rather than at construction time. This keeps DI from throwing during
/// host startup if the connection string is missing — the startup
/// reachability check catches the eventual failure and logs it, so the app
/// boots and the operator can fix it via configuration.
/// </remarks>
public sealed class SqlConnectionFactory : IDbConnectionFactory
{
    private readonly IOptions<DatabaseOptions> _options;
    private readonly IConfiguration _configuration;

    public SqlConnectionFactory(
        IOptions<DatabaseOptions> options,
        IConfiguration configuration)
    {
        _options = options;
        _configuration = configuration;
    }

    public async Task<IDbConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var connectionStringName = _options.Value.ConnectionStringName;
        var connectionString = _configuration.GetConnectionString(connectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' not found in configuration. " +
                $"Add it under ConnectionStrings in appsettings.json.");
        }

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
