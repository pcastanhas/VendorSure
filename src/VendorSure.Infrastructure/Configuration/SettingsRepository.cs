using Dapper;
using VendorSure.Domain.Configuration;
using VendorSure.Services.Configuration;
using VendorSure.Services.Data;

namespace VendorSure.Infrastructure.Configuration;

internal sealed class SettingsRepository : ISettingsRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public SettingsRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<Setting>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                id              AS Id,
                [key]           AS [Key],
                description     AS Description,
                required        AS Required,
                sensitive       AS Sensitive,
                value           AS Value
            FROM dbo.settings
            ORDER BY [key];";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync<Setting>(command);
        return rows.ToList();
    }

    public async Task<Setting?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                id              AS Id,
                [key]           AS [Key],
                description     AS Description,
                required        AS Required,
                sensitive       AS Sensitive,
                value           AS Value
            FROM dbo.settings
            WHERE [key] = @key;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { key }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<Setting>(command);
    }

    public async Task<bool> UpdateValueAsync(string key, string? value, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE dbo.settings
            SET value = @value
            WHERE [key] = @key;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { key, value }, cancellationToken: ct);
        var rowsAffected = await connection.ExecuteAsync(command);
        return rowsAffected > 0;
    }
}
