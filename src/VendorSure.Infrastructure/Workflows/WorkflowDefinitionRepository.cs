using Dapper;
using VendorSure.Domain.RequestTypes;
using VendorSure.Domain.Workflows;
using VendorSure.Services.Data;
using VendorSure.Services.Workflows;

namespace VendorSure.Infrastructure.Workflows;

internal sealed class WorkflowDefinitionRepository : IWorkflowDefinitionRepository
{
    private const string SelectColumns = @"
        id                          AS Id,
        request_type_version_id     AS RequestTypeVersionId,
        name                        AS Name,
        notes                       AS Notes,
        start_node_id               AS StartNodeId";

    private readonly IDbConnectionFactory _connectionFactory;

    public WorkflowDefinitionRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> ListByVersionIdAsync(
        int requestTypeVersionId, CancellationToken ct = default)
    {
        var sql = $@"
            SELECT {SelectColumns}
            FROM dbo.workflow_definitions
            WHERE request_type_version_id = @requestTypeVersionId
            ORDER BY name ASC;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { requestTypeVersionId }, cancellationToken: ct);
        var rows = await connection.QueryAsync<WorkflowDefinition>(command);
        return rows.ToList();
    }

    public async Task<WorkflowDefinition?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.workflow_definitions WHERE id = @id;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { id }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<WorkflowDefinition>(command);
    }

    public async Task<CreateWorkflowResult> CreateAsync(
        int requestTypeVersionId,
        string name,
        string? notes,
        CancellationToken ct = default)
    {
        // Two-statement transaction: insert the workflow row, then insert
        // its Start node, then point start_node_id at the new Start.
        // Every workflow has a Start by invariant (Phase 5 / Chunk 7 design
        // shift): the schema's start_node_id is nullable only because of
        // the create-then-set ordering — semantically Start is mandatory.
        //
        // The workflow INSERT is conditional on the same three rules as
        // before (version exists AND is Draft, name not taken). The Start
        // INSERT only runs if the workflow INSERT produced a new id.
        //
        // Atomicity matters: if the Start INSERT failed for any reason
        // we'd otherwise leave a workflow row with start_node_id = NULL,
        // breaking the invariant. The transaction ensures all-or-nothing.
        const string insertWorkflowSql = @"
            INSERT INTO dbo.workflow_definitions
                (request_type_version_id, name, notes)
            SELECT @versionId, @name, @notes
            WHERE EXISTS (
                    SELECT 1 FROM dbo.request_type_versions
                    WHERE id = @versionId AND request_state = @draftCode)
              AND NOT EXISTS (
                    SELECT 1 FROM dbo.workflow_definitions
                    WHERE request_type_version_id = @versionId AND name = @name);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        // Start node: node_type_id=1, no block, no children yet,
        // execution_level=1 (Start is THE root of its workflow).
        const string insertStartSql = @"
            INSERT INTO dbo.workflow_nodes
                (workflow_definition_id, node_type_id, block_catalog_id, execution_level)
            VALUES (@workflowId, 1, NULL, 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        const string setStartFkSql = @"
            UPDATE dbo.workflow_definitions
            SET start_node_id = @startNodeId
            WHERE id = @workflowId;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            var newId = await connection.QuerySingleOrDefaultAsync<int?>(
                new CommandDefinition(
                    insertWorkflowSql,
                    new
                    {
                        versionId = requestTypeVersionId,
                        name,
                        notes,
                        draftCode = RequestStateCodes.Draft,
                    },
                    transaction,
                    cancellationToken: ct));

            if (newId is null)
            {
                // No row inserted. Roll back (no-op since nothing wrote),
                // then run the probes outside the transaction to figure
                // out which rule rejected.
                transaction.Rollback();

                var versionExists = await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        "SELECT COUNT(*) FROM dbo.request_type_versions WHERE id = @versionId;",
                        new { versionId = requestTypeVersionId },
                        cancellationToken: ct)) > 0;
                if (!versionExists)
                {
                    return new CreateWorkflowResult(CreateWorkflowOutcome.RejectedVersionNotFound, null);
                }

                var versionIsDraft = await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        @"SELECT COUNT(*) FROM dbo.request_type_versions
                          WHERE id = @versionId AND request_state = @draftCode;",
                        new { versionId = requestTypeVersionId, draftCode = RequestStateCodes.Draft },
                        cancellationToken: ct)) > 0;
                if (!versionIsDraft)
                {
                    return new CreateWorkflowResult(CreateWorkflowOutcome.RejectedNotDraft, null);
                }

                return new CreateWorkflowResult(CreateWorkflowOutcome.RejectedNameConflict, null);
            }

            var startNodeId = await connection.QuerySingleAsync<int>(
                new CommandDefinition(
                    insertStartSql,
                    new { workflowId = newId.Value },
                    transaction,
                    cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition(
                setStartFkSql,
                new { workflowId = newId.Value, startNodeId },
                transaction,
                cancellationToken: ct));

            transaction.Commit();
            return new CreateWorkflowResult(CreateWorkflowOutcome.Created, newId);
        }
        catch
        {
            // Any exception inside the transaction (e.g. constraint
            // violation on the Start insert) rolls everything back so we
            // don't leave a workflow row without a Start.
            try { transaction.Rollback(); } catch { /* already rolled */ }
            throw;
        }
    }

    public async Task<UpdateWorkflowResult> UpdateAsync(
        int id, string name, string? notes, CancellationToken ct = default)
    {
        // Update gated on:
        //   - parent version being Draft (UPDATE ... FROM ... INNER JOIN)
        //   - no OTHER workflow on the same version having this name
        //     (the 'id <> @id' lets a no-op rename succeed)
        const string updateSql = @"
            UPDATE wd
            SET wd.name  = @name,
                wd.notes = @notes
            FROM dbo.workflow_definitions wd
            INNER JOIN dbo.request_type_versions ver
                ON ver.id = wd.request_type_version_id
            WHERE wd.id = @id
              AND ver.request_state = @draftCode
              AND NOT EXISTS (
                    SELECT 1 FROM dbo.workflow_definitions other
                    WHERE other.request_type_version_id = wd.request_type_version_id
                      AND other.name = @name
                      AND other.id <> @id);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var rowsAffected = await connection.ExecuteAsync(
            new CommandDefinition(
                updateSql,
                new { id, name, notes, draftCode = RequestStateCodes.Draft },
                cancellationToken: ct));

        if (rowsAffected > 0)
        {
            return UpdateWorkflowResult.Updated;
        }

        // 0 rows. Three possible reasons in specificity order:
        //   1. Row doesn't exist → NotFound
        //   2. Row exists but parent version isn't Draft → RejectedNotDraft
        //   3. Otherwise → name conflict (some OTHER row took the name)
        const string probeSql = @"
            SELECT ver.request_state
            FROM dbo.workflow_definitions wd
            INNER JOIN dbo.request_type_versions ver
                ON ver.id = wd.request_type_version_id
            WHERE wd.id = @id;";

        var parentState = await connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(probeSql, new { id }, cancellationToken: ct));

        if (parentState is null)
        {
            return UpdateWorkflowResult.NotFound;
        }
        if (parentState != RequestStateCodes.Draft)
        {
            return UpdateWorkflowResult.RejectedNotDraft;
        }
        return UpdateWorkflowResult.RejectedNameConflict;
    }

    public async Task<DeleteWorkflowResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        // Transactional delete with FK-aware cleanup. Order matters:
        //   1. Lock the workflow row (UPDLOCK) and read its parent state.
        //      Bail with NotFound/RejectedNotDraft if not exactly Draft.
        //   2. Null out workflow_definitions.start_node_id so step 3 can
        //      delete the node it was pointing at without an FK error.
        //   3. Null out path1_node_id and path2_node_id on this workflow's
        //      nodes — workflow_nodes.path* are self-referential FKs and
        //      would block step 4 otherwise.
        //   4. DELETE the workflow's nodes.
        //   5. DELETE the workflow definition.
        //
        // Steps 2-5 don't need their own Draft gate; the UPDLOCK in step 1
        // holds the parent workflow_definitions row for the rest of the
        // transaction. No concurrent caller can flip its parent's state
        // (which would be a Phase 4 transition) and have us delete from
        // under them — that's a separate row, not gated, but the
        // transition rules in Phase 4 only operate on versions whose
        // current InService is being demoted, not on Drafts being moved
        // out from under their workflows. The risk is essentially nil
        // for v1; revisit if Phase 6+ adds a "delete this Draft version"
        // operation.
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            // 1. Lock and probe.
            var parentState = await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(
                    @"SELECT ver.request_state
                      FROM dbo.workflow_definitions wd WITH (UPDLOCK, ROWLOCK)
                      INNER JOIN dbo.request_type_versions ver
                          ON ver.id = wd.request_type_version_id
                      WHERE wd.id = @id;",
                    new { id },
                    transaction,
                    cancellationToken: ct));

            if (parentState is null)
            {
                transaction.Rollback();
                return DeleteWorkflowResult.NotFound;
            }
            if (parentState != RequestStateCodes.Draft)
            {
                transaction.Rollback();
                return DeleteWorkflowResult.RejectedNotDraft;
            }

            // 2. Null out start_node_id so step 3 doesn't violate the FK.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.workflow_definitions SET start_node_id = NULL WHERE id = @id;",
                new { id }, transaction, cancellationToken: ct));

            // 3. Null out node-to-node path FKs for this workflow's nodes.
            //    Both path1 and path2 in one statement.
            await connection.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.workflow_nodes
                  SET path1_node_id = NULL, path2_node_id = NULL
                  WHERE workflow_definition_id = @id;",
                new { id }, transaction, cancellationToken: ct));

            // 4. Delete the nodes.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.workflow_nodes WHERE workflow_definition_id = @id;",
                new { id }, transaction, cancellationToken: ct));

            // 5. Delete the workflow definition.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.workflow_definitions WHERE id = @id;",
                new { id }, transaction, cancellationToken: ct));

            transaction.Commit();
            return DeleteWorkflowResult.Deleted;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}
