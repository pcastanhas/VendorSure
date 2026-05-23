using Dapper;
using VendorSure.Domain.Identity;
using VendorSure.Services.Data;
using VendorSure.Services.Identity;

namespace VendorSure.Infrastructure.Identity;

internal sealed class UserRepository : IUserRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public UserRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<User?> GetByEntraidAsync(string entraid, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                id          AS Id,
                entraid     AS Entraid,
                name        AS Name,
                group_id    AS GroupId,
                is_admin    AS IsAdmin,
                is_active   AS IsActive
            FROM dbo.users
            WHERE entraid = @entraid;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { entraid }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<User>(command);
    }
}
