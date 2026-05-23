using Dapper;
using VendorSure.Domain.Identity;
using VendorSure.Services.Data;
using VendorSure.Services.Identity;

namespace VendorSure.Infrastructure.Identity;

internal sealed class UserGroupRepository : IUserGroupRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public UserGroupRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<UserGroup>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                id                      AS Id,
                name                    AS Name,
                is_active               AS IsActive,
                can_restart_workflow    AS CanRestartWorkflow,
                can_change_workflow     AS CanChangeWorkflow,
                can_submit_requests     AS CanSubmitRequests
            FROM dbo.user_groups
            ORDER BY name;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync<UserGroup>(command);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<UserGroupListItem>> ListWithUserCountsAsync(CancellationToken ct = default)
    {
        // LEFT JOIN against the COUNT subquery so groups with no users
        // still appear with AssignedUserCount = 0. The COALESCE handles
        // that NULL → 0.
        const string sql = @"
            SELECT
                g.id                       AS Id,
                g.name                     AS Name,
                g.is_active                AS IsActive,
                g.can_restart_workflow     AS CanRestartWorkflow,
                g.can_change_workflow      AS CanChangeWorkflow,
                g.can_submit_requests      AS CanSubmitRequests,
                COALESCE(uc.UserCount, 0)  AS AssignedUserCount
            FROM dbo.user_groups g
            LEFT JOIN (
                SELECT group_id, COUNT(*) AS UserCount
                FROM dbo.users
                GROUP BY group_id
            ) uc ON uc.group_id = g.id
            ORDER BY g.name;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync<GroupWithCountRow>(command);

        return rows.Select(r => new UserGroupListItem(
            new UserGroup
            {
                Id = r.Id,
                Name = r.Name,
                IsActive = r.IsActive,
                CanRestartWorkflow = r.CanRestartWorkflow,
                CanChangeWorkflow = r.CanChangeWorkflow,
                CanSubmitRequests = r.CanSubmitRequests,
            },
            r.AssignedUserCount)).ToList();
    }

    // Flat shape Dapper materialises into, then we project to UserGroupListItem.
    // Kept private to the repository — it's a one-off projection, not a
    // domain concept.
    private sealed class GroupWithCountRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public bool CanRestartWorkflow { get; init; }
        public bool CanChangeWorkflow { get; init; }
        public bool CanSubmitRequests { get; init; }
        public int AssignedUserCount { get; init; }
    }

    public async Task<UserGroup?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                id                      AS Id,
                name                    AS Name,
                is_active               AS IsActive,
                can_restart_workflow    AS CanRestartWorkflow,
                can_change_workflow     AS CanChangeWorkflow,
                can_submit_requests     AS CanSubmitRequests
            FROM dbo.user_groups
            WHERE id = @id;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { id }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<UserGroup>(command);
    }

    public async Task<int> CountAssignedUsersAsync(int groupId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT COUNT(*)
            FROM dbo.users
            WHERE group_id = @groupId;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { groupId }, cancellationToken: ct);
        return await connection.ExecuteScalarAsync<int>(command);
    }

    public async Task<int> CreateAsync(UserGroup group, CancellationToken ct = default)
    {
        // INSERT then SELECT SCOPE_IDENTITY() in one round-trip. CAST to int
        // because SCOPE_IDENTITY returns numeric(38,0) by default.
        const string sql = @"
            INSERT INTO dbo.user_groups
                (name, is_active, can_restart_workflow, can_change_workflow, can_submit_requests)
            VALUES
                (@Name, @IsActive, @CanRestartWorkflow, @CanChangeWorkflow, @CanSubmitRequests);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, group, cancellationToken: ct);
        return await connection.QuerySingleAsync<int>(command);
    }

    public async Task<UpdateUserGroupResult> UpdateAsync(UserGroup group, CancellationToken ct = default)
    {
        // The WHERE clause enforces the deactivation rule atomically:
        //   - we always match by id, AND
        //   - either we're not setting IsActive=false, OR the row is
        //     already inactive (so this isn't a deactivation transition),
        //     OR no users reference this group.
        // Doing it in one statement closes the race window between a
        // count check and the UPDATE.
        const string updateSql = @"
            UPDATE dbo.user_groups
            SET name                  = @Name,
                is_active             = @IsActive,
                can_restart_workflow  = @CanRestartWorkflow,
                can_change_workflow   = @CanChangeWorkflow,
                can_submit_requests   = @CanSubmitRequests
            WHERE id = @Id
              AND (
                    @IsActive = 1
                 OR is_active = 0
                 OR NOT EXISTS (SELECT 1 FROM dbo.users WHERE group_id = @Id)
              );";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var updateCommand = new CommandDefinition(updateSql, group, cancellationToken: ct);
        var rowsAffected = await connection.ExecuteAsync(updateCommand);
        if (rowsAffected > 0)
        {
            return UpdateUserGroupResult.Updated;
        }

        // 0 rows updated. Either the id doesn't exist, or the rule rejected
        // the deactivation. Disambiguate with a single existence probe.
        const string existsSql = "SELECT COUNT(*) FROM dbo.user_groups WHERE id = @Id;";
        var existsCommand = new CommandDefinition(existsSql, new { group.Id }, cancellationToken: ct);
        var rowExists = await connection.ExecuteScalarAsync<int>(existsCommand) > 0;

        return rowExists
            ? UpdateUserGroupResult.RejectedHasUsers
            : UpdateUserGroupResult.NotFound;
    }
}
