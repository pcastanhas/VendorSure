using System.Data;
using Dapper;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.RequestTypes;

internal sealed class RequestTypeRepository : IRequestTypeRepository
{
    private const string SelectColumns = @"
        id                          AS Id,
        name                        AS Name,
        is_explanation_required     AS IsExplanationRequired,
        is_active                   AS IsActive";

    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<RequestType>> GetAllAsync(CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.request_types ORDER BY name;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync<RequestType>(command);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<RequestTypeListItem>> ListWithVersionInfoAsync(
        CancellationToken ct = default)
    {
        // One query, conditional aggregation: per type, find the version
        // numbers of any rows in 'I' / 'D' state and the count in 'S'.
        // MAX() over a CASE returning the version-or-null collapses to
        // 'the highest matching version, or NULL if none' which is exactly
        // what the projection wants.
        //
        // LEFT JOIN so types with zero versions still appear (e.g. a brand-
        // new type before its initial Draft was created, though
        // CreateWithFirstDraftAsync makes that an edge case rather than
        // the norm).
        const string sql = @"
            SELECT
                t.id                        AS Id,
                t.name                      AS Name,
                t.is_explanation_required   AS IsExplanationRequired,
                t.is_active                 AS IsActive,
                MAX(CASE WHEN v.request_state = 'I' THEN v.version END) AS InServiceVersion,
                MAX(CASE WHEN v.request_state = 'D' THEN v.version END) AS DraftVersion,
                SUM(CASE WHEN v.request_state = 'S' THEN 1 ELSE 0 END)  AS SupersededCount
            FROM dbo.request_types t
            LEFT JOIN dbo.request_type_versions v
                ON v.request_type_id = t.id
            GROUP BY t.id, t.name, t.is_explanation_required, t.is_active
            ORDER BY t.name;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync<ListItemRow>(command);

        return rows.Select(r => new RequestTypeListItem(
            new RequestType
            {
                Id = r.Id,
                Name = r.Name,
                IsExplanationRequired = r.IsExplanationRequired,
                IsActive = r.IsActive,
            },
            r.InServiceVersion,
            r.DraftVersion,
            r.SupersededCount)).ToList();
    }

    // Flat shape Dapper materialises into for ListWithVersionInfoAsync.
    // Private — one-off projection, not a domain concept.
    private sealed class ListItemRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsExplanationRequired { get; init; }
        public bool IsActive { get; init; }
        public int? InServiceVersion { get; init; }
        public int? DraftVersion { get; init; }
        public int SupersededCount { get; init; }
    }

    public async Task<RequestType?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.request_types WHERE id = @id;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { id }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<RequestType>(command);
    }

    public async Task<CreateRequestTypeResult> CreateAsync(RequestType type, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var newId = await InsertRequestTypeAsync(connection, type, transaction: null, ct);

        return newId is not null
            ? new CreateRequestTypeResult(CreateRequestTypeOutcome.Created, newId)
            : new CreateRequestTypeResult(CreateRequestTypeOutcome.RejectedNameConflict, null);
    }

    public async Task<CreateRequestTypeWithDraftResult> CreateWithFirstDraftAsync(
        RequestType type, CancellationToken ct = default)
    {
        // Two writes in one transaction: insert the type, then its first
        // Draft. Either both land or neither does. Conflict on name during
        // the type insert short-circuits — no version row is inserted.
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            var newTypeId = await InsertRequestTypeAsync(connection, type, transaction, ct);
            if (newTypeId is null)
            {
                transaction.Rollback();
                return new CreateRequestTypeWithDraftResult(
                    CreateRequestTypeOutcome.RejectedNameConflict, null, null);
            }

            // The Draft is version 1 by definition (no prior versions exist
            // for a brand-new type), no need to MAX() the table.
            const string insertVersionSql = @"
                INSERT INTO dbo.request_type_versions
                    (request_type_id, version, request_state)
                VALUES (@RequestTypeId, 1, @StateCode);
                SELECT CAST(SCOPE_IDENTITY() AS int);";
            var versionCommand = new CommandDefinition(
                insertVersionSql,
                new { RequestTypeId = newTypeId.Value, StateCode = RequestStateCodes.Draft },
                transaction,
                cancellationToken: ct);
            var newVersionId = await connection.QuerySingleAsync<int>(versionCommand);

            transaction.Commit();
            return new CreateRequestTypeWithDraftResult(
                CreateRequestTypeOutcome.Created, newTypeId, newVersionId);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<UpdateRequestTypeResult> UpdateAsync(RequestType type, CancellationToken ct = default)
    {
        // Conflict check is 'no OTHER row has this name' so a no-op rename
        // is legal.
        const string updateSql = @"
            UPDATE dbo.request_types
            SET name                    = @Name,
                is_explanation_required = @IsExplanationRequired,
                is_active               = @IsActive
            WHERE id = @Id
              AND NOT EXISTS (SELECT 1 FROM dbo.request_types WHERE name = @Name AND id <> @Id);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var updateCommand = new CommandDefinition(updateSql, type, cancellationToken: ct);
        var rowsAffected = await connection.ExecuteAsync(updateCommand);
        if (rowsAffected > 0)
        {
            return UpdateRequestTypeResult.Updated;
        }

        // 0 rows. Probe to disambiguate.
        const string rowExistsSql =
            "SELECT COUNT(*) FROM dbo.request_types WHERE id = @Id;";
        var rowExistsCommand = new CommandDefinition(rowExistsSql, new { type.Id }, cancellationToken: ct);
        var rowExists = await connection.ExecuteScalarAsync<int>(rowExistsCommand) > 0;

        return rowExists
            ? UpdateRequestTypeResult.RejectedNameConflict
            : UpdateRequestTypeResult.NotFound;
    }

    /// <summary>
    /// Shared INSERT path used by both <see cref="CreateAsync"/> and
    /// <see cref="CreateWithFirstDraftAsync"/>. Returns the new id, or
    /// <c>null</c> if the name conflicts.
    /// </summary>
    private static async Task<int?> InsertRequestTypeAsync(
        IDbConnection connection,
        RequestType type,
        IDbTransaction? transaction,
        CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO dbo.request_types (name, is_explanation_required, is_active)
            SELECT @Name, @IsExplanationRequired, @IsActive
            WHERE NOT EXISTS (SELECT 1 FROM dbo.request_types WHERE name = @Name);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        var command = new CommandDefinition(sql, type, transaction, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<int?>(command);
    }
}
