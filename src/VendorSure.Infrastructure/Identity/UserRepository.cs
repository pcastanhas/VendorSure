using Dapper;
using VendorSure.Domain.Identity;
using VendorSure.Services.Data;
using VendorSure.Services.Identity;

namespace VendorSure.Infrastructure.Identity;

internal sealed class UserRepository : IUserRepository
{
    private const string SelectColumns = @"
        id          AS Id,
        entraid     AS Entraid,
        name        AS Name,
        group_id    AS GroupId,
        is_admin    AS IsAdmin,
        is_active   AS IsActive";

    private readonly IDbConnectionFactory _connectionFactory;

    public UserRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.users ORDER BY name;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync<User>(command);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<UserListItem>> ListWithGroupNamesAsync(CancellationToken ct = default)
    {
        // INNER JOIN — every user has a group (FK NOT NULL), so no need for
        // LEFT JOIN or COALESCE. The group_id column on users is also
        // queryable from the embedded User entity.
        const string sql = @"
            SELECT
                u.id          AS Id,
                u.entraid     AS Entraid,
                u.name        AS Name,
                u.group_id    AS GroupId,
                u.is_admin    AS IsAdmin,
                u.is_active   AS IsActive,
                g.name        AS GroupName
            FROM dbo.users u
            INNER JOIN dbo.user_groups g ON g.id = u.group_id
            ORDER BY u.name;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync<UserWithGroupNameRow>(command);

        return rows.Select(r => new UserListItem(
            new User
            {
                Id = r.Id,
                Entraid = r.Entraid,
                Name = r.Name,
                GroupId = r.GroupId,
                IsAdmin = r.IsAdmin,
                IsActive = r.IsActive,
            },
            r.GroupName)).ToList();
    }

    // Flat shape Dapper materialises into, projected to UserListItem.
    // Kept private — one-off projection, not a domain concept.
    private sealed class UserWithGroupNameRow
    {
        public int Id { get; init; }
        public string Entraid { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int GroupId { get; init; }
        public bool IsAdmin { get; init; }
        public bool IsActive { get; init; }
        public string GroupName { get; init; } = string.Empty;
    }

    public async Task<User?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.users WHERE id = @id;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { id }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<User>(command);
    }

    public async Task<User?> GetByEntraidAsync(string entraid, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.users WHERE entraid = @entraid;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { entraid }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<User>(command);
    }

    public async Task<CreateUserResult> CreateAsync(User user, CancellationToken ct = default)
    {
        // Conditional INSERT: both rules (active group, no entraid collision)
        // are checked as part of the same statement. If either check fails
        // the INSERT is skipped and SCOPE_IDENTITY returns NULL, so we
        // disambiguate with focused existence probes.
        const string insertSql = @"
            INSERT INTO dbo.users (entraid, name, group_id, is_admin, is_active)
            SELECT @Entraid, @Name, @GroupId, @IsAdmin, @IsActive
            WHERE EXISTS (SELECT 1 FROM dbo.user_groups WHERE id = @GroupId AND is_active = 1)
              AND NOT EXISTS (SELECT 1 FROM dbo.users WHERE entraid = @Entraid);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var insertCommand = new CommandDefinition(insertSql, user, cancellationToken: ct);
        var newId = await connection.QuerySingleOrDefaultAsync<int?>(insertCommand);
        if (newId is not null)
        {
            return new CreateUserResult(CreateUserOutcome.Created, newId);
        }

        // INSERT skipped. Figure out which rule rejected it.
        // Check the entraid collision first: it's the more specific signal,
        // and if it's hit we don't want to mask it under the group check.
        const string entraidExistsSql =
            "SELECT COUNT(*) FROM dbo.users WHERE entraid = @Entraid;";
        var entraidExistsCommand = new CommandDefinition(
            entraidExistsSql, new { user.Entraid }, cancellationToken: ct);
        var entraidTaken =
            await connection.ExecuteScalarAsync<int>(entraidExistsCommand) > 0;
        if (entraidTaken)
        {
            return new CreateUserResult(CreateUserOutcome.RejectedEntraidConflict, null);
        }

        return new CreateUserResult(CreateUserOutcome.RejectedInactiveGroup, null);
    }

    public async Task<UpdateUserResult> UpdateAsync(User user, CancellationToken ct = default)
    {
        // The WHERE clause enforces both rules atomically:
        //   - target group must be active, AND
        //   - no DIFFERENT user already holds the proposed entraid (a user
        //     keeping their own entraid is fine; that's the id <> @Id check).
        const string updateSql = @"
            UPDATE dbo.users
            SET entraid    = @Entraid,
                name       = @Name,
                group_id   = @GroupId,
                is_admin   = @IsAdmin,
                is_active  = @IsActive
            WHERE id = @Id
              AND EXISTS (SELECT 1 FROM dbo.user_groups WHERE id = @GroupId AND is_active = 1)
              AND NOT EXISTS (SELECT 1 FROM dbo.users WHERE entraid = @Entraid AND id <> @Id);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var updateCommand = new CommandDefinition(updateSql, user, cancellationToken: ct);
        var rowsAffected = await connection.ExecuteAsync(updateCommand);
        if (rowsAffected > 0)
        {
            return UpdateUserResult.Updated;
        }

        // 0 rows updated. Three possibilities: row missing, group inactive,
        // entraid taken by a different user. Probe in that order.
        const string rowExistsSql =
            "SELECT COUNT(*) FROM dbo.users WHERE id = @Id;";
        var rowExistsCommand = new CommandDefinition(
            rowExistsSql, new { user.Id }, cancellationToken: ct);
        var rowExists =
            await connection.ExecuteScalarAsync<int>(rowExistsCommand) > 0;
        if (!rowExists)
        {
            return UpdateUserResult.NotFound;
        }

        // Row exists. Check entraid first (more specific).
        const string entraidConflictSql =
            "SELECT COUNT(*) FROM dbo.users WHERE entraid = @Entraid AND id <> @Id;";
        var entraidConflictCommand = new CommandDefinition(
            entraidConflictSql, new { user.Entraid, user.Id }, cancellationToken: ct);
        var entraidTaken =
            await connection.ExecuteScalarAsync<int>(entraidConflictCommand) > 0;
        if (entraidTaken)
        {
            return UpdateUserResult.RejectedEntraidConflict;
        }

        return UpdateUserResult.RejectedInactiveGroup;
    }
}
