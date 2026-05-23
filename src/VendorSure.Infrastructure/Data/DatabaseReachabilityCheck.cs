using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VendorSure.Services.Data;

namespace VendorSure.Infrastructure.Data;

/// <summary>
/// Pings the database with <c>SELECT 1</c> once at startup and logs the
/// outcome. Failures are <em>logged, not thrown</em> — the app boots either
/// way so the operator can use the rest of the surface (e.g. read this log
/// to find out their connection string is wrong) without the process
/// crash-looping.
/// </summary>
internal sealed class DatabaseReachabilityCheck : IHostedService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DatabaseReachabilityCheck> _logger;

    public DatabaseReachabilityCheck(
        IDbConnectionFactory factory,
        ILogger<DatabaseReachabilityCheck> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var connection = await _factory.CreateOpenConnectionAsync(cancellationToken);
            using var command = ((SqlConnection)connection).CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            await command.ExecuteScalarAsync(cancellationToken);

            _logger.LogInformation(
                "Connected to VenSure database (server={Server}, database={Database})",
                ((SqlConnection)connection).DataSource,
                ((SqlConnection)connection).Database);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "VenSure database unreachable at startup: {Message}",
                ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
