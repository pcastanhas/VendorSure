using Dapper;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.RequestTypes;

internal sealed class RequestTypeValidationRepository : IRequestTypeValidationRepository
{
    private const string SelectColumns = @"
        id                          AS Id,
        request_type_version_id     AS RequestTypeVersionId,
        description                 AS Description,
        ai_prompt                   AS AiPrompt,
        execution_order             AS ExecutionOrder";

    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeValidationRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<RequestTypeValidation>> ListByVersionIdAsync(
        int requestTypeVersionId, CancellationToken ct = default)
    {
        var sql = $@"
            SELECT {SelectColumns}
            FROM dbo.request_type_validations
            WHERE request_type_version_id = @requestTypeVersionId
            ORDER BY execution_order ASC;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { requestTypeVersionId }, cancellationToken: ct);
        var rows = await connection.QueryAsync<RequestTypeValidation>(command);
        return rows.ToList();
    }

    public async Task<RequestTypeValidation?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.request_type_validations WHERE id = @id;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { id }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<RequestTypeValidation>(command);
    }

    public async Task<CreateValidationResult> CreateAsync(
        int requestTypeVersionId,
        string description,
        string aiPrompt,
        CancellationToken ct = default)
    {
        // The execution_order is computed atomically as ISNULL(MAX, 0) + 1
        // inside the same INSERT, paired with a WHERE EXISTS … draft guard
        // on the parent version. Single statement, no race window.
        //
        // The SELECT after returns the new row's id + execution_order from
        // the just-inserted row; returns 0 rows when the guard fails.
        const string sql = @"
            INSERT INTO dbo.request_type_validations
                (request_type_version_id, description, ai_prompt, execution_order)
            SELECT
                @versionId,
                @description,
                @aiPrompt,
                ISNULL((SELECT MAX(execution_order) FROM dbo.request_type_validations
                        WHERE request_type_version_id = @versionId), 0) + 1
            WHERE EXISTS (
                SELECT 1 FROM dbo.request_type_versions
                WHERE id = @versionId AND request_state = @draftCode);

            SELECT id              AS Id,
                   execution_order AS ExecutionOrder
            FROM dbo.request_type_validations
            WHERE id = CAST(SCOPE_IDENTITY() AS int);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(
            sql,
            new
            {
                versionId = requestTypeVersionId,
                description,
                aiPrompt,
                draftCode = RequestStateCodes.Draft,
            },
            cancellationToken: ct);

        var row = await connection.QuerySingleOrDefaultAsync<NewValidationRow>(command);
        if (row is not null)
        {
            return new CreateValidationResult(CreateValidationOutcome.Created, row.Id, row.ExecutionOrder);
        }

        // Guard failed. Two possible reasons: no such version, or version
        // exists but isn't Draft. Single probe with both conditions:
        var versionExists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.request_type_versions WHERE id = @versionId;",
                new { versionId = requestTypeVersionId },
                cancellationToken: ct)) > 0;

        return new CreateValidationResult(
            versionExists ? CreateValidationOutcome.RejectedNotDraft
                          : CreateValidationOutcome.RejectedVersionNotFound,
            null, null);
    }

    public async Task<UpdateValidationResult> UpdateAsync(
        int id,
        string description,
        string aiPrompt,
        CancellationToken ct = default)
    {
        // Update gated on parent version being Draft, via UPDATE … FROM JOIN.
        // execution_order and request_type_version_id are NOT touched here.
        const string updateSql = @"
            UPDATE val
            SET val.description = @description,
                val.ai_prompt   = @aiPrompt
            FROM dbo.request_type_validations val
            INNER JOIN dbo.request_type_versions ver
                ON ver.id = val.request_type_version_id
            WHERE val.id = @id
              AND ver.request_state = @draftCode;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var rowsAffected = await connection.ExecuteAsync(
            new CommandDefinition(
                updateSql,
                new { id, description, aiPrompt, draftCode = RequestStateCodes.Draft },
                cancellationToken: ct));

        if (rowsAffected > 0)
        {
            return UpdateValidationResult.Updated;
        }

        // 0 rows. Disambiguate.
        var rowExists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.request_type_validations WHERE id = @id;",
                new { id },
                cancellationToken: ct)) > 0;

        return rowExists
            ? UpdateValidationResult.RejectedNotDraft
            : UpdateValidationResult.NotFound;
    }

    public async Task<DeleteValidationResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        // Two-statement transactional delete: junction rows for this
        // validation first (no cascade in the schema), then the validation
        // itself. Both gated on the parent version being Draft. If the
        // gate fails, neither statement removes anything.
        //
        // We use an explicit transaction for safety: between the two
        // statements, a connection failure could leave orphan junction
        // rows. Inside one transaction, both commit together or both roll
        // back.
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            const string deleteJunctionsSql = @"
                DELETE jd
                FROM dbo.request_type_validation_documents jd
                INNER JOIN dbo.request_type_validations val
                    ON val.id = jd.request_type_validation_id
                INNER JOIN dbo.request_type_versions ver
                    ON ver.id = val.request_type_version_id
                WHERE val.id = @id
                  AND ver.request_state = @draftCode;";

            await connection.ExecuteAsync(new CommandDefinition(
                deleteJunctionsSql,
                new { id, draftCode = RequestStateCodes.Draft },
                transaction,
                cancellationToken: ct));

            const string deleteValidationSql = @"
                DELETE val
                FROM dbo.request_type_validations val
                INNER JOIN dbo.request_type_versions ver
                    ON ver.id = val.request_type_version_id
                WHERE val.id = @id
                  AND ver.request_state = @draftCode;";

            var rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
                deleteValidationSql,
                new { id, draftCode = RequestStateCodes.Draft },
                transaction,
                cancellationToken: ct));

            if (rowsAffected > 0)
            {
                transaction.Commit();
                return DeleteValidationResult.Deleted;
            }

            // The validation delete didn't fire. Either no such row or
            // parent isn't Draft. Probe (still inside the transaction;
            // rolled back below regardless).
            var rowExists = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.request_type_validations WHERE id = @id;",
                    new { id },
                    transaction,
                    cancellationToken: ct)) > 0;

            transaction.Rollback();
            return rowExists
                ? DeleteValidationResult.RejectedNotDraft
                : DeleteValidationResult.NotFound;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // Holder for the (Id, ExecutionOrder) projection on CreateAsync.
    private sealed class NewValidationRow
    {
        public int Id { get; init; }
        public int ExecutionOrder { get; init; }
    }
}
