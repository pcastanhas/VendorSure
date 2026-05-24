using Dapper;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.RequestTypes;

internal sealed class RequestTypeVersionRepository : IRequestTypeVersionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeVersionRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<RequestTypeVersion?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(SelectByIdSql, new { id }, cancellationToken: ct);
        var row = await connection.QuerySingleOrDefaultAsync<VersionRow>(command);
        return row is null ? null : ToEntity(row);
    }

    public async Task<IReadOnlyList<RequestTypeVersion>> GetByRequestTypeIdAsync(
        int requestTypeId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(SelectByTypeIdSql, new { requestTypeId }, cancellationToken: ct);
        var rows = await connection.QueryAsync<VersionRow>(command);
        return rows.Select(ToEntity).ToList();
    }

    public async Task<CreateDraftResult> CreateDraftAsync(int requestTypeId, CancellationToken ct = default)
    {
        // The new version number is (current MAX + 1) or 1 if the type has
        // no prior versions. ISNULL(MAX, 0) + 1 gives both.
        // INSERT … SELECT pattern lets the WHERE EXISTS guard against an
        // unknown request_type_id without a separate round-trip.
        const string sql = @"
            INSERT INTO dbo.request_type_versions
                (request_type_id, version, request_state)
            SELECT
                @requestTypeId,
                ISNULL((SELECT MAX(version) FROM dbo.request_type_versions
                        WHERE request_type_id = @requestTypeId), 0) + 1,
                @stateCode
            WHERE EXISTS (SELECT 1 FROM dbo.request_types WHERE id = @requestTypeId);

            SELECT id   AS Id,
                   version AS Version
            FROM dbo.request_type_versions
            WHERE id = CAST(SCOPE_IDENTITY() AS int);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(
            sql,
            new { requestTypeId, stateCode = RequestStateCodes.Draft },
            cancellationToken: ct);

        // Returns 0 rows when the WHERE EXISTS guard fails (no such request_type).
        // Returns 1 row when the insert succeeded.
        var row = await connection.QuerySingleOrDefaultAsync<NewVersionRow>(command);
        return row is null
            ? new CreateDraftResult(CreateDraftOutcome.RejectedRequestTypeNotFound, null, null)
            : new CreateDraftResult(CreateDraftOutcome.Created, row.Id, row.Version);
    }

    public async Task<UpdateRequestTypeVersionResult> UpdateAsync(
        RequestTypeVersion version, CancellationToken ct = default)
    {
        // Immutability rule: only Draft versions can be edited. Enforced in
        // the WHERE clause so it's atomic with the update; no race window
        // between a state-check read and the UPDATE.
        //
        // Editable fields (per the interface doc): Name and
        // WorkflowSelectionPrompt. RequestState, timestamps, RequestTypeId,
        // and Version are not touched by this method.
        const string updateSql = @"
            UPDATE dbo.request_type_versions
            SET name                       = @Name,
                workflow_selection_prompt  = @WorkflowSelectionPrompt
            WHERE id = @Id
              AND request_state = @DraftCode;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var updateCommand = new CommandDefinition(
            updateSql,
            new
            {
                version.Id,
                version.Name,
                version.WorkflowSelectionPrompt,
                DraftCode = RequestStateCodes.Draft,
            },
            cancellationToken: ct);

        var rowsAffected = await connection.ExecuteAsync(updateCommand);
        if (rowsAffected > 0)
        {
            return UpdateRequestTypeVersionResult.Updated;
        }

        // 0 rows. Either no such id, or the row exists but isn't Draft.
        const string rowExistsSql =
            "SELECT COUNT(*) FROM dbo.request_type_versions WHERE id = @Id;";
        var rowExistsCommand = new CommandDefinition(rowExistsSql, new { version.Id }, cancellationToken: ct);
        var rowExists = await connection.ExecuteScalarAsync<int>(rowExistsCommand) > 0;

        return rowExists
            ? UpdateRequestTypeVersionResult.RejectedNotDraft
            : UpdateRequestTypeVersionResult.NotFound;
    }

    public async Task<TransitionToInServiceResult> TransitionToInServiceAsync(
        int versionId, CancellationToken ct = default)
    {
        // Two-statement transactional transition:
        //   1) Demote prior In Service of the same type to Superseded.
        //   2) Promote the target Draft to In Service.
        //
        // The initial SELECT with UPDLOCK serialises concurrent callers
        // attempting to transition the same Draft: the second caller
        // blocks until the first commits, then re-reads and sees
        // request_state != 'D' on the row, so its @TypeId comes back NULL
        // and it bails. This rules out the race where two callers each
        // demote the other's not-yet-promoted row.
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        // One timestamp value used for both UPDATEs so the new InService's
        // placed_in_service_ts equals the prior InService's superseded_ts
        // exactly. Captured here rather than via SYSUTCDATETIME() in each
        // statement to make the audit story crisp.
        var transitionAt = DateTime.UtcNow;

        try
        {
            // 1. UPDLOCK on the Draft row — locks it for this transaction so
            //    no other transition can race past us between read and update.
            var typeId = await connection.QuerySingleOrDefaultAsync<int?>(
                new CommandDefinition(
                    @"SELECT request_type_id
                      FROM dbo.request_type_versions WITH (UPDLOCK, ROWLOCK)
                      WHERE id = @versionId
                        AND request_state = @draftCode;",
                    new { versionId, draftCode = RequestStateCodes.Draft },
                    transaction,
                    cancellationToken: ct));

            if (typeId is null)
            {
                // Bail before any UPDATEs run. Probe within the same
                // (read-only) transaction so we don't introduce a second
                // network round-trip pattern that could race with our own
                // rollback. The row either doesn't exist (NotFound) or
                // exists but isn't Draft (NotDraft).
                var rowExists = await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        "SELECT COUNT(*) FROM dbo.request_type_versions WHERE id = @versionId;",
                        new { versionId },
                        transaction,
                        cancellationToken: ct)) > 0;

                transaction.Rollback();

                return rowExists
                    ? TransitionToInServiceResult.RejectedNotDraft
                    : TransitionToInServiceResult.NotFound;
            }

            // 2. Demote the prior In Service of this type (if any). The
            //    schema doesn't enforce at-most-one-InService-per-type so
            //    in pathological data we might demote several; that's
            //    still the right thing.
            await connection.ExecuteAsync(
                new CommandDefinition(
                    @"UPDATE dbo.request_type_versions
                      SET request_state = @supersededCode,
                          superseded_ts = @transitionAt
                      WHERE request_type_id = @typeId
                        AND request_state = @inServiceCode;",
                    new
                    {
                        typeId = typeId.Value,
                        supersededCode = RequestStateCodes.Superseded,
                        inServiceCode = RequestStateCodes.InService,
                        transitionAt,
                    },
                    transaction,
                    cancellationToken: ct));

            // 3. Promote the target Draft to In Service. The UPDLOCK above
            //    means the WHERE on @versionId still matches a Draft row.
            await connection.ExecuteAsync(
                new CommandDefinition(
                    @"UPDATE dbo.request_type_versions
                      SET request_state = @inServiceCode,
                          placed_in_service_ts = @transitionAt
                      WHERE id = @versionId;",
                    new
                    {
                        versionId,
                        inServiceCode = RequestStateCodes.InService,
                        transitionAt,
                    },
                    transaction,
                    cancellationToken: ct));

            transaction.Commit();
            return TransitionToInServiceResult.Succeeded;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // ---- shared SELECTs and mapping ------------------------------------

    private const string SelectColumns = @"
        id                          AS Id,
        request_type_id             AS RequestTypeId,
        version                     AS Version,
        name                        AS Name,
        request_state               AS RequestStateCode,
        workflow_selection_prompt   AS WorkflowSelectionPrompt,
        created_ts                  AS CreatedTs,
        placed_in_service_ts        AS PlacedInServiceTs,
        superseded_ts               AS SupersededTs";

    private static readonly string SelectByIdSql =
        $"SELECT {SelectColumns} FROM dbo.request_type_versions WHERE id = @id;";

    private static readonly string SelectByTypeIdSql =
        $@"SELECT {SelectColumns}
           FROM dbo.request_type_versions
           WHERE request_type_id = @requestTypeId
           ORDER BY version ASC;";

    // Flat shape Dapper materialises into. We map the request_state char
    // to the RequestState enum in ToEntity; doing it here keeps the
    // domain entity free of the schema's char representation.
    private sealed class VersionRow
    {
        public int Id { get; init; }
        public int RequestTypeId { get; init; }
        public int Version { get; init; }
        public string? Name { get; init; }
        public string RequestStateCode { get; init; } = string.Empty;
        public string? WorkflowSelectionPrompt { get; init; }
        public DateTime CreatedTs { get; init; }
        public DateTime? PlacedInServiceTs { get; init; }
        public DateTime? SupersededTs { get; init; }
    }

    /// <summary>
    /// Tiny holder for the (id, version) projection returned by
    /// <see cref="CreateDraftAsync"/>'s INSERT … SELECT. A nullable
    /// value-tuple would be cleaner but Dapper's positional materialisation
    /// of tuples is brittle; a named class is unambiguous.
    /// </summary>
    private sealed class NewVersionRow
    {
        public int Id { get; init; }
        public int Version { get; init; }
    }

    private static RequestTypeVersion ToEntity(VersionRow row) => new()
    {
        Id = row.Id,
        RequestTypeId = row.RequestTypeId,
        Version = row.Version,
        Name = row.Name,
        RequestState = RequestStateExtensions.FromCode(row.RequestStateCode),
        WorkflowSelectionPrompt = row.WorkflowSelectionPrompt,
        CreatedTs = row.CreatedTs,
        PlacedInServiceTs = row.PlacedInServiceTs,
        SupersededTs = row.SupersededTs,
    };
}
