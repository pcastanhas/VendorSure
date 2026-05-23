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

    public async Task<bool> UpdateAsync(UserGroup group, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE dbo.user_groups
            SET name                  = @Name,
                is_active             = @IsActive,
                can_restart_workflow  = @CanRestartWorkflow,
                can_change_workflow   = @CanChangeWorkflow,
                can_submit_requests   = @CanSubmitRequests
            WHERE id = @Id;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, group, cancellationToken: ct);
        var rowsAffected = await connection.ExecuteAsync(command);
        return rowsAffected > 0;
    }
}
