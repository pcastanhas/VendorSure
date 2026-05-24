using System.Data;
using Dapper;
using VendorSure.Domain.RequestTypes;
using VendorSure.Domain.Workflows;
using VendorSure.Services.Data;
using VendorSure.Services.Workflows;

namespace VendorSure.Infrastructure.Workflows;

internal sealed class WorkflowNodeRepository : IWorkflowNodeRepository
{
    private const string SelectColumns = @"
        id                          AS Id,
        workflow_definition_id      AS WorkflowDefinitionId,
        node_type_id                AS NodeTypeId,
        block_catalog_id            AS BlockCatalogId,
        execution_level             AS ExecutionLevel,
        approver_group_id           AS ApproverGroupId,
        stale_threshold_days        AS StaleThresholdDays,
        stale_message_text          AS StaleMessageText,
        notes                       AS Notes,
        path1_node_id               AS Path1NodeId,
        path2_node_id               AS Path2NodeId,
        prompt_text                 AS PromptText,
        path1_prompt_text           AS Path1PromptText,
        path2_prompt_text           AS Path2PromptText";

    private readonly IDbConnectionFactory _connectionFactory;

    public WorkflowNodeRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<WorkflowNode>> ListByWorkflowIdAsync(
        int workflowDefinitionId, CancellationToken ct = default)
    {
        var sql = $@"
            SELECT {SelectColumns}
            FROM dbo.workflow_nodes
            WHERE workflow_definition_id = @workflowDefinitionId
            ORDER BY execution_level ASC, id ASC;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { workflowDefinitionId }, cancellationToken: ct);
        var rows = await connection.QueryAsync<WorkflowNode>(command);
        return rows.ToList();
    }

    public async Task<WorkflowNode?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.workflow_nodes WHERE id = @id;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { id }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<WorkflowNode>(command);
    }

    public async Task<CreateNodeResult> CreateAsync(WorkflowNode seed, CancellationToken ct = default)
    {
        // Pre-validate node_type vs block_catalog pairing so the caller
        // gets RejectedInvalidShape rather than an SqlException from the
        // schema CHECK. The schema is still the safety net for everyone
        // who isn't us.
        var requiresBlock = WorkflowNodeTypeIds.RequiresBlock(seed.NodeTypeId);
        if (requiresBlock && seed.BlockCatalogId is null)
        {
            return new CreateNodeResult(CreateNodeOutcome.RejectedInvalidShape, null);
        }
        if (!requiresBlock && seed.BlockCatalogId is not null)
        {
            return new CreateNodeResult(CreateNodeOutcome.RejectedInvalidShape, null);
        }

        // Conditional INSERT: workflow exists AND its parent version is Draft.
        // execution_level forced to 0 (unwired). path FKs forced to NULL —
        // wiring is a separate operation.
        const string sql = @"
            INSERT INTO dbo.workflow_nodes
                (workflow_definition_id, node_type_id, block_catalog_id,
                 execution_level,
                 approver_group_id, stale_threshold_days, stale_message_text, notes,
                 path1_node_id, path2_node_id,
                 prompt_text, path1_prompt_text, path2_prompt_text)
            SELECT
                @WorkflowDefinitionId, @NodeTypeId, @BlockCatalogId,
                0,
                @ApproverGroupId, @StaleThresholdDays, @StaleMessageText, @Notes,
                NULL, NULL,
                @PromptText, @Path1PromptText, @Path2PromptText
            WHERE EXISTS (
                SELECT 1
                FROM dbo.workflow_definitions wd
                INNER JOIN dbo.request_type_versions ver
                    ON ver.id = wd.request_type_version_id
                WHERE wd.id = @WorkflowDefinitionId
                  AND ver.request_state = @DraftCode);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var newId = await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                sql,
                new
                {
                    seed.WorkflowDefinitionId,
                    seed.NodeTypeId,
                    seed.BlockCatalogId,
                    seed.ApproverGroupId,
                    seed.StaleThresholdDays,
                    seed.StaleMessageText,
                    seed.Notes,
                    seed.PromptText,
                    seed.Path1PromptText,
                    seed.Path2PromptText,
                    DraftCode = RequestStateCodes.Draft,
                },
                cancellationToken: ct));

        if (newId is not null)
        {
            return new CreateNodeResult(CreateNodeOutcome.Created, newId);
        }

        // Probe: workflow exists at all?
        var workflowExists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.workflow_definitions WHERE id = @id;",
                new { id = seed.WorkflowDefinitionId },
                cancellationToken: ct)) > 0;

        return new CreateNodeResult(
            workflowExists ? CreateNodeOutcome.RejectedNotDraft
                           : CreateNodeOutcome.RejectedWorkflowNotFound,
            null);
    }

    public async Task<InsertChildResult> InsertChildAsync(
        InsertChildRequest request, CancellationToken ct = default)
    {
        // Caller-bug validation: parentSlot must be 1 or 2. The decision-
        // child-slot constraint (must be set when new node is Decision
        // AND parent's slot is non-empty) is checked after we know
        // whether the slot is empty.
        if (request.ParentSlot is not (1 or 2))
        {
            throw new ArgumentException(
                $"ParentSlot must be 1 or 2; got {request.ParentSlot}.",
                nameof(request));
        }
        if (request.DecisionChildSlot is { } dcs && dcs is not (1 or 2))
        {
            throw new ArgumentException(
                $"DecisionChildSlot must be null, 1, or 2; got {dcs}.",
                nameof(request));
        }

        // Shape pre-validation — same logic as CreateAsync, surfacing the
        // same RejectedInvalidShape outcome rather than letting the schema
        // CHECK throw an SqlException.
        var requiresBlock = WorkflowNodeTypeIds.RequiresBlock(request.NodeTypeId);
        if (requiresBlock && request.BlockCatalogId is null)
        {
            return new InsertChildResult(InsertChildOutcome.RejectedInvalidShape, null);
        }
        if (!requiresBlock && request.BlockCatalogId is not null)
        {
            return new InsertChildResult(InsertChildOutcome.RejectedInvalidShape, null);
        }

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            // Lock + read the parent's shape and the current value of the
            // chosen slot, plus the Draft state of the version. One round-
            // trip. UPDLOCK serialises concurrent inserts on the same
            // parent slot — without it, two concurrent users could both
            // see the slot as "empty" and race to fill it.
            var parent = await connection.QuerySingleOrDefaultAsync<ParentProbe>(
                new CommandDefinition(
                    @"SELECT n.node_type_id           AS NodeTypeId,
                             n.workflow_definition_id AS WorkflowDefinitionId,
                             n.execution_level        AS ExecutionLevel,
                             n.path1_node_id          AS Path1NodeId,
                             n.path2_node_id          AS Path2NodeId,
                             ver.request_state        AS RequestStateCode
                      FROM dbo.workflow_nodes n WITH (UPDLOCK, ROWLOCK)
                      INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
                      INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
                      WHERE n.id = @parentId;",
                    new { parentId = request.ParentNodeId },
                    transaction,
                    cancellationToken: ct));

            if (parent is null)
            {
                transaction.Rollback();
                return new InsertChildResult(InsertChildOutcome.RejectedParentNotFound, null);
            }

            if (parent.RequestStateCode != RequestStateCodes.Draft)
            {
                transaction.Rollback();
                return new InsertChildResult(InsertChildOutcome.RejectedNotDraft, null);
            }

            // Terminals can't have children. Defensive — UI shouldn't show
            // a + button on a terminal in the first place.
            if (parent.NodeTypeId is WorkflowNodeTypeIds.Approved
                                  or WorkflowNodeTypeIds.Rejected
                                  or WorkflowNodeTypeIds.Cancelled)
            {
                transaction.Rollback();
                return new InsertChildResult(InsertChildOutcome.RejectedParentIsTerminal, null);
            }

            // Slot 2 on a non-Decision parent is a caller bug: only
            // Decision parents have a path2. Start and Process only have
            // path1 (slot 1).
            if (request.ParentSlot == 2 && parent.NodeTypeId != WorkflowNodeTypeIds.Decision)
            {
                throw new ArgumentException(
                    $"ParentSlot=2 is only valid for Decision parents; parent {request.ParentNodeId} is type {parent.NodeTypeId}.",
                    nameof(request));
            }

            // Capture the currently-occupied slot value (if any) — this
            // is the "displaced child" in the insert-between case.
            var displacedChildId = request.ParentSlot == 1
                ? parent.Path1NodeId
                : parent.Path2NodeId;

            // Insert-between with a Decision new-node: caller must specify
            // which Decision slot inherits the displaced child. Without
            // that we'd be guessing.
            if (displacedChildId is not null
                && request.NodeTypeId == WorkflowNodeTypeIds.Decision
                && request.DecisionChildSlot is null)
            {
                throw new ArgumentException(
                    "DecisionChildSlot is required when inserting a Decision between a parent and an existing child.",
                    nameof(request));
            }

            // Terminals can't be inserted-between because they have no
            // out-edges — there's no slot to put the displaced child in.
            // UI must filter terminals out of the picker for non-empty
            // slots. Defensive enum here (could also throw; treating as
            // an invalid-shape outcome keeps it consistent with other
            // structural rejections).
            if (displacedChildId is not null
                && (request.NodeTypeId is WorkflowNodeTypeIds.Approved
                                       or WorkflowNodeTypeIds.Rejected
                                       or WorkflowNodeTypeIds.Cancelled))
            {
                transaction.Rollback();
                return new InsertChildResult(InsertChildOutcome.RejectedInvalidShape, null);
            }

            // Insert the new node at parent.level + 1. Path FKs depend on
            // whether we're appending or splicing:
            //   - Append:  new.path1 = NULL, new.path2 = NULL.
            //   - Splice (new is Start/Process): new.path1 = displaced.
            //   - Splice (new is Decision): new.pathN = displaced (N from
            //     DecisionChildSlot); the other slot stays NULL.
            int? newPath1 = null;
            int? newPath2 = null;
            if (displacedChildId is int dcId)
            {
                if (request.NodeTypeId == WorkflowNodeTypeIds.Decision)
                {
                    // Caller specified which side inherits the displaced
                    // child via DecisionChildSlot (we already validated
                    // it's non-null and in {1, 2} above).
                    if (request.DecisionChildSlot == 1)
                    {
                        newPath1 = dcId;
                    }
                    else
                    {
                        newPath2 = dcId;
                    }
                }
                else
                {
                    // Start/Process new node has only path1. The displaced
                    // child goes there.
                    newPath1 = dcId;
                }
            }

            var newNodeLevel = parent.ExecutionLevel + 1;

            const string insertSql = @"
                INSERT INTO dbo.workflow_nodes
                    (workflow_definition_id, node_type_id, block_catalog_id,
                     execution_level, path1_node_id, path2_node_id)
                VALUES (@WorkflowDefinitionId, @NodeTypeId, @BlockCatalogId,
                        @ExecutionLevel, @Path1NodeId, @Path2NodeId);
                SELECT CAST(SCOPE_IDENTITY() AS int);";

            var newNodeId = await connection.QuerySingleAsync<int>(
                new CommandDefinition(
                    insertSql,
                    new
                    {
                        WorkflowDefinitionId = parent.WorkflowDefinitionId,
                        request.NodeTypeId,
                        request.BlockCatalogId,
                        ExecutionLevel = newNodeLevel,
                        Path1NodeId = newPath1,
                        Path2NodeId = newPath2,
                    },
                    transaction,
                    cancellationToken: ct));

            // Wire the parent's slot to point at the new node. This
            // replaces the previous value (the displaced child reference,
            // if any), which now lives only on the new node's path1/pathN.
            var wireParentSql = request.ParentSlot == 1
                ? "UPDATE dbo.workflow_nodes SET path1_node_id = @newNodeId WHERE id = @parentId;"
                : "UPDATE dbo.workflow_nodes SET path2_node_id = @newNodeId WHERE id = @parentId;";
            await connection.ExecuteAsync(new CommandDefinition(
                wireParentSql,
                new { newNodeId, parentId = request.ParentNodeId },
                transaction,
                cancellationToken: ct));

            // Renumber starting from the new node at its just-computed
            // level. The recursive CTE in RenumberSubtreeAsync walks via
            // path1/path2:
            //   - Append case: only the new node (no descendants yet).
            //   - Splice case: new -> displaced -> displaced's
            //     descendants. The displaced child ends up at L+1
            //     (one deeper than before, since it's now one level
            //     deeper from the root), and its descendants follow.
            await RenumberSubtreeAsync(connection, transaction, newNodeId, newNodeLevel, ct);

            transaction.Commit();
            return new InsertChildResult(InsertChildOutcome.Inserted, newNodeId);
        }
        catch
        {
            // ArgumentException or any other failure rolls back atomically.
            try { transaction.Rollback(); } catch { /* already rolled */ }
            throw;
        }
    }

    private sealed class ParentProbe
    {
        public int NodeTypeId { get; init; }
        public int WorkflowDefinitionId { get; init; }
        public int ExecutionLevel { get; init; }
        public int? Path1NodeId { get; init; }
        public int? Path2NodeId { get; init; }
        public string RequestStateCode { get; init; } = string.Empty;
    }

    public async Task<UpdateNodeResult> UpdateAsync(WorkflowNode edited, CancellationToken ct = default)
    {
        // Edits only the property fields. node_type, block_catalog_id,
        // workflow, execution_level, and path FKs are NOT touched.
        const string updateSql = @"
            UPDATE n
            SET n.approver_group_id    = @ApproverGroupId,
                n.stale_threshold_days = @StaleThresholdDays,
                n.stale_message_text   = @StaleMessageText,
                n.notes                = @Notes,
                n.prompt_text          = @PromptText,
                n.path1_prompt_text    = @Path1PromptText,
                n.path2_prompt_text    = @Path2PromptText
            FROM dbo.workflow_nodes n
            INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
            INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
            WHERE n.id = @Id
              AND ver.request_state = @DraftCode;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var rowsAffected = await connection.ExecuteAsync(
            new CommandDefinition(
                updateSql,
                new
                {
                    edited.Id,
                    edited.ApproverGroupId,
                    edited.StaleThresholdDays,
                    edited.StaleMessageText,
                    edited.Notes,
                    edited.PromptText,
                    edited.Path1PromptText,
                    edited.Path2PromptText,
                    DraftCode = RequestStateCodes.Draft,
                },
                cancellationToken: ct));

        if (rowsAffected > 0)
        {
            return UpdateNodeResult.Updated;
        }

        // Disambiguate: row exists or not?
        var rowExists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.workflow_nodes WHERE id = @Id;",
                new { edited.Id },
                cancellationToken: ct)) > 0;

        return rowExists ? UpdateNodeResult.RejectedNotDraft : UpdateNodeResult.NotFound;
    }

    public Task<SetPathOutcome> SetPath1Async(int sourceId, int? targetNodeId, CancellationToken ct = default)
        => SetPathAsync(sourceId, targetNodeId, isPath1: true, ct);

    public Task<SetPathOutcome> SetPath2Async(int sourceId, int? targetNodeId, CancellationToken ct = default)
        => SetPathAsync(sourceId, targetNodeId, isPath1: false, ct);

    private async Task<SetPathOutcome> SetPathAsync(
        int sourceId, int? targetNodeId, bool isPath1, CancellationToken ct)
    {
        // Reject self-loops up front — cheap, no SQL needed.
        if (targetNodeId is int tid && tid == sourceId)
        {
            return SetPathOutcome.RejectedSelfLoop;
        }

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            // Lock + read the source row's shape (node type, workflow id,
            // its level, and the Draft state of the parent version) in one
            // shot. UPDLOCK serialises concurrent SetPath calls on the same
            // source node.
            var source = await connection.QuerySingleOrDefaultAsync<SourceProbe>(
                new CommandDefinition(
                    @"SELECT n.node_type_id          AS NodeTypeId,
                             n.workflow_definition_id AS WorkflowDefinitionId,
                             n.execution_level       AS ExecutionLevel,
                             ver.request_state       AS RequestStateCode
                      FROM dbo.workflow_nodes n WITH (UPDLOCK, ROWLOCK)
                      INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
                      INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
                      WHERE n.id = @sourceId;",
                    new { sourceId },
                    transaction,
                    cancellationToken: ct));

            if (source is null)
            {
                transaction.Rollback();
                return SetPathOutcome.NotFound;
            }
            if (source.RequestStateCode != RequestStateCodes.Draft)
            {
                transaction.Rollback();
                return SetPathOutcome.RejectedNotDraft;
            }

            // Shape check: which path slots does this node type allow?
            //   - Start (1), Process (2): only path1
            //   - Decision (3): both
            //   - Approved/Rejected/Cancelled (4/5/6): neither
            var shapeAllows = (source.NodeTypeId, isPath1) switch
            {
                (WorkflowNodeTypeIds.Start, true) => true,
                (WorkflowNodeTypeIds.Process, true) => true,
                (WorkflowNodeTypeIds.Decision, _) => true,  // either path
                _ => false,
            };
            if (!shapeAllows)
            {
                transaction.Rollback();
                return SetPathOutcome.RejectedShape;
            }

            // If clearing the edge (targetNodeId = null), the path FK becomes
            // NULL and there's no renumbering to do — the (now-orphaned)
            // downstream subtree keeps its current levels.
            if (targetNodeId is null)
            {
                var clearSql = isPath1
                    ? "UPDATE dbo.workflow_nodes SET path1_node_id = NULL WHERE id = @sourceId;"
                    : "UPDATE dbo.workflow_nodes SET path2_node_id = NULL WHERE id = @sourceId;";
                await connection.ExecuteAsync(
                    new CommandDefinition(clearSql, new { sourceId }, transaction, cancellationToken: ct));
                transaction.Commit();
                return SetPathOutcome.Updated;
            }

            // Setting a non-null target. Probe the target.
            var target = await connection.QuerySingleOrDefaultAsync<TargetProbe>(
                new CommandDefinition(
                    @"SELECT workflow_definition_id AS WorkflowDefinitionId
                      FROM dbo.workflow_nodes WITH (UPDLOCK, ROWLOCK)
                      WHERE id = @targetId;",
                    new { targetId = targetNodeId.Value },
                    transaction,
                    cancellationToken: ct));

            if (target is null)
            {
                transaction.Rollback();
                return SetPathOutcome.RejectedTargetNotFound;
            }
            if (target.WorkflowDefinitionId != source.WorkflowDefinitionId)
            {
                transaction.Rollback();
                return SetPathOutcome.RejectedTargetNotInWorkflow;
            }

            // No-merging rule: the target must not already have an incoming
            // edge from anywhere in this workflow. Includes the source
            // itself (if we're re-pointing the same edge to the same target,
            // we'd otherwise spuriously reject). We allow the no-op case:
            // if source's path1/path2 is already this target, we treat it
            // as success.
            //
            // Two checks:
            //   1. Is this source's relevant path slot already pointing at
            //      this target? Then no-op.
            //   2. Otherwise, does ANY other source have a path FK pointing
            //      at this target? Then RejectedTargetAlreadyHasParent.
            var alreadyParentInfo = await connection.QuerySingleOrDefaultAsync<ParentCheck>(
                new CommandDefinition(
                    @"SELECT
                        SUM(CASE WHEN id = @sourceId
                                  AND ((@isPath1 = 1 AND path1_node_id = @targetId)
                                    OR (@isPath1 = 0 AND path2_node_id = @targetId))
                             THEN 1 ELSE 0 END) AS SourceAlreadyPoints,
                        SUM(CASE WHEN (id <> @sourceId
                                  AND (path1_node_id = @targetId OR path2_node_id = @targetId))
                                  OR (id = @sourceId
                                  AND ((@isPath1 = 1 AND path2_node_id = @targetId)
                                    OR (@isPath1 = 0 AND path1_node_id = @targetId)))
                             THEN 1 ELSE 0 END) AS OtherParentCount
                      FROM dbo.workflow_nodes
                      WHERE workflow_definition_id = @workflowId;",
                    new
                    {
                        sourceId,
                        targetId = targetNodeId.Value,
                        isPath1 = isPath1 ? 1 : 0,
                        workflowId = source.WorkflowDefinitionId,
                    },
                    transaction,
                    cancellationToken: ct));

            if (alreadyParentInfo is not null && alreadyParentInfo.SourceAlreadyPoints > 0)
            {
                // No-op: source's relevant slot already points at target.
                // We still renumber the target's subtree to be defensive
                // about any drift in the data — cheap insurance.
                await RenumberSubtreeAsync(
                    connection, transaction, targetNodeId.Value, source.ExecutionLevel + 1, ct);
                transaction.Commit();
                return SetPathOutcome.Updated;
            }
            if (alreadyParentInfo is not null && alreadyParentInfo.OtherParentCount > 0)
            {
                transaction.Rollback();
                return SetPathOutcome.RejectedTargetAlreadyHasParent;
            }

            // All checks passed. Set the FK and renumber the target's subtree.
            var setSql = isPath1
                ? "UPDATE dbo.workflow_nodes SET path1_node_id = @targetId WHERE id = @sourceId;"
                : "UPDATE dbo.workflow_nodes SET path2_node_id = @targetId WHERE id = @sourceId;";
            await connection.ExecuteAsync(new CommandDefinition(
                setSql,
                new { sourceId, targetId = targetNodeId.Value },
                transaction,
                cancellationToken: ct));

            await RenumberSubtreeAsync(
                connection, transaction, targetNodeId.Value, source.ExecutionLevel + 1, ct);

            transaction.Commit();
            return SetPathOutcome.Updated;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<DeleteNodeResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        // Three-statement transactional delete:
        //   1. Null the workflow's start_node_id if it points at us.
        //   2. Null any path1/path2 in the same workflow pointing at us.
        //   3. Delete this node.
        // Draft-gated up front via UPDLOCK + state read on the node row.
        //
        // No renumbering — the (now-orphaned) downstream subtree retains
        // its current execution_level values. They'll be reset when the
        // subtree is re-wired into the graph from a new parent.
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            var probe = await connection.QuerySingleOrDefaultAsync<NodeStateProbe>(
                new CommandDefinition(
                    @"SELECT n.workflow_definition_id AS WorkflowDefinitionId,
                             ver.request_state         AS RequestStateCode
                      FROM dbo.workflow_nodes n WITH (UPDLOCK, ROWLOCK)
                      INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
                      INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
                      WHERE n.id = @id;",
                    new { id },
                    transaction,
                    cancellationToken: ct));

            if (probe is null)
            {
                transaction.Rollback();
                return DeleteNodeResult.NotFound;
            }
            if (probe.RequestStateCode != RequestStateCodes.Draft)
            {
                transaction.Rollback();
                return DeleteNodeResult.RejectedNotDraft;
            }

            // 1. Null workflow_definitions.start_node_id if it points at us.
            await connection.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.workflow_definitions
                  SET start_node_id = NULL
                  WHERE id = @workflowId AND start_node_id = @id;",
                new { workflowId = probe.WorkflowDefinitionId, id },
                transaction,
                cancellationToken: ct));

            // 2. Null path FKs of upstream parents in the same workflow.
            await connection.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.workflow_nodes
                  SET path1_node_id = CASE WHEN path1_node_id = @id THEN NULL ELSE path1_node_id END,
                      path2_node_id = CASE WHEN path2_node_id = @id THEN NULL ELSE path2_node_id END
                  WHERE workflow_definition_id = @workflowId
                    AND (path1_node_id = @id OR path2_node_id = @id);",
                new { id, workflowId = probe.WorkflowDefinitionId },
                transaction,
                cancellationToken: ct));

            // 3. Delete the node.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.workflow_nodes WHERE id = @id;",
                new { id }, transaction, cancellationToken: ct));

            transaction.Commit();
            return DeleteNodeResult.Deleted;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<SetStartNodeOutcome> SetStartNodeAsync(
        int workflowDefinitionId, int? nodeId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            // Lock the workflow row + read parent state.
            var probe = await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(
                    @"SELECT ver.request_state
                      FROM dbo.workflow_definitions wd WITH (UPDLOCK, ROWLOCK)
                      INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
                      WHERE wd.id = @workflowDefinitionId;",
                    new { workflowDefinitionId },
                    transaction,
                    cancellationToken: ct));

            if (probe is null)
            {
                transaction.Rollback();
                return SetStartNodeOutcome.RejectedWorkflowNotFound;
            }
            if (probe != RequestStateCodes.Draft)
            {
                transaction.Rollback();
                return SetStartNodeOutcome.RejectedNotDraft;
            }

            // Clearing? Just null the FK and we're done.
            if (nodeId is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.workflow_definitions SET start_node_id = NULL WHERE id = @workflowDefinitionId;",
                    new { workflowDefinitionId }, transaction, cancellationToken: ct));
                transaction.Commit();
                return SetStartNodeOutcome.Updated;
            }

            // Validate the node: exists, in this workflow, is Start type.
            var nodeShape = await connection.QuerySingleOrDefaultAsync<NodeShapeProbe>(
                new CommandDefinition(
                    @"SELECT workflow_definition_id AS WorkflowDefinitionId,
                             node_type_id            AS NodeTypeId
                      FROM dbo.workflow_nodes
                      WHERE id = @nodeId;",
                    new { nodeId = nodeId.Value },
                    transaction,
                    cancellationToken: ct));

            if (nodeShape is null)
            {
                transaction.Rollback();
                return SetStartNodeOutcome.RejectedNodeNotFound;
            }
            if (nodeShape.WorkflowDefinitionId != workflowDefinitionId)
            {
                transaction.Rollback();
                return SetStartNodeOutcome.RejectedNodeNotInWorkflow;
            }
            if (nodeShape.NodeTypeId != WorkflowNodeTypeIds.Start)
            {
                transaction.Rollback();
                return SetStartNodeOutcome.RejectedNotStartNode;
            }

            // Set the FK and renumber from the start node at level 1.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.workflow_definitions SET start_node_id = @nodeId WHERE id = @workflowDefinitionId;",
                new { workflowDefinitionId, nodeId = nodeId.Value },
                transaction, cancellationToken: ct));

            await RenumberSubtreeAsync(connection, transaction, nodeId.Value, 1, ct);

            transaction.Commit();
            return SetStartNodeOutcome.Updated;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<DeleteSubtreeResult> DeleteSubtreeAsync(int nodeId, CancellationToken ct = default)
    {
        // Atomic subtree delete. Used by the "delete entire branch" action
        // on Decisions and as the "delete this and N descendants" option
        // on Process node deletion.
        //
        // Order of operations inside the transaction:
        //   1. Probe the node: exists, version is Draft, not Start.
        //   2. Walk descendants via recursive CTE; collect IDs.
        //   3. Null the upstream parent's path FK that points at the
        //      subtree root (must happen before the delete to avoid the
        //      FK_workflow_nodes_path1 / path2 constraint violations on
        //      cascade).
        //   4. Null workflow.start_node_id if it points at any subtree
        //      node (defensive — Start delete is rejected above, so this
        //      should only fire if a descendant is somehow the start; in
        //      a healthy workflow it's a no-op).
        //   5. Null all path1/path2 within the subtree so the DELETE
        //      doesn't see intra-subtree FK references.
        //   6. DELETE all subtree rows in one statement.
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            var probe = await connection.QuerySingleOrDefaultAsync<NodeShapeStateProbe>(
                new CommandDefinition(
                    @"SELECT n.workflow_definition_id AS WorkflowDefinitionId,
                             n.node_type_id           AS NodeTypeId,
                             ver.request_state        AS RequestStateCode
                      FROM dbo.workflow_nodes n WITH (UPDLOCK, ROWLOCK)
                      INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
                      INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
                      WHERE n.id = @nodeId;",
                    new { nodeId },
                    transaction,
                    cancellationToken: ct));

            if (probe is null)
            {
                transaction.Rollback();
                return new DeleteSubtreeResult(DeleteSubtreeOutcome.RejectedNodeNotFound, 0);
            }
            if (probe.RequestStateCode != RequestStateCodes.Draft)
            {
                transaction.Rollback();
                return new DeleteSubtreeResult(DeleteSubtreeOutcome.RejectedNotDraft, 0);
            }
            if (probe.NodeTypeId == WorkflowNodeTypeIds.Start)
            {
                transaction.Rollback();
                return new DeleteSubtreeResult(DeleteSubtreeOutcome.RejectedIsStart, 0);
            }

            // Count descendants for the result (caller wants to surface
            // "deleted N nodes" — same count the UI computed before the
            // confirm dialog, so the user sees the expected number).
            // -1 to exclude the root itself; it's not a descendant.
            var totalRows = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                @";WITH subtree AS (
                       SELECT id, path1_node_id, path2_node_id
                       FROM dbo.workflow_nodes WHERE id = @nodeId
                       UNION ALL
                       SELECT child.id, child.path1_node_id, child.path2_node_id
                       FROM dbo.workflow_nodes child
                       INNER JOIN subtree parent
                           ON child.id = parent.path1_node_id
                           OR child.id = parent.path2_node_id
                   )
                   SELECT COUNT(*) FROM subtree;",
                new { nodeId }, transaction, cancellationToken: ct));
            var descendantsDeleted = Math.Max(0, totalRows - 1);

            // 3. Null upstream parent's path FK pointing at this node.
            await connection.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.workflow_nodes
                  SET path1_node_id = CASE WHEN path1_node_id = @nodeId THEN NULL ELSE path1_node_id END,
                      path2_node_id = CASE WHEN path2_node_id = @nodeId THEN NULL ELSE path2_node_id END
                  WHERE workflow_definition_id = @workflowId
                    AND (path1_node_id = @nodeId OR path2_node_id = @nodeId);",
                new { nodeId, workflowId = probe.WorkflowDefinitionId },
                transaction, cancellationToken: ct));

            // 4-6. Null intra-subtree path FKs, then delete subtree rows.
            // Both statements use the same recursive CTE definition.
            // Doing it as two batched statements (one UPDATE, one DELETE)
            // keeps each individually simple and lets SQL Server optimize
            // the CTE recursion separately for each.
            const string subtreeCte = @"
                ;WITH subtree AS (
                    SELECT id, path1_node_id, path2_node_id
                    FROM dbo.workflow_nodes WHERE id = @nodeId
                    UNION ALL
                    SELECT n.id, n.path1_node_id, n.path2_node_id
                    FROM dbo.workflow_nodes n
                    INNER JOIN subtree s
                        ON n.id = s.path1_node_id OR n.id = s.path2_node_id
                )";

            // 4. Defensive: if start_node_id happens to point inside the
            // subtree (e.g. a descendant is the start — shouldn't happen
            // because we reject Start delete above, but the schema
            // permits arbitrary nodes being designated as start), null
            // it before the cascade.
            await connection.ExecuteAsync(new CommandDefinition(
                subtreeCte + @"
                  UPDATE wd
                  SET start_node_id = NULL
                  FROM dbo.workflow_definitions wd
                  WHERE wd.id = @workflowId
                    AND wd.start_node_id IN (SELECT id FROM subtree);",
                new { nodeId, workflowId = probe.WorkflowDefinitionId },
                transaction, cancellationToken: ct));

            // 5. Null intra-subtree FKs so the DELETE doesn't violate
            // FK_workflow_nodes_path1 / path2 when removing rows.
            await connection.ExecuteAsync(new CommandDefinition(
                subtreeCte + @"
                  UPDATE n
                  SET path1_node_id = NULL, path2_node_id = NULL
                  FROM dbo.workflow_nodes n
                  INNER JOIN subtree s ON s.id = n.id;",
                new { nodeId }, transaction, cancellationToken: ct));

            // 6. Delete the subtree.
            await connection.ExecuteAsync(new CommandDefinition(
                subtreeCte + @"
                  DELETE n FROM dbo.workflow_nodes n
                  INNER JOIN subtree s ON s.id = n.id;",
                new { nodeId }, transaction, cancellationToken: ct));

            transaction.Commit();
            return new DeleteSubtreeResult(DeleteSubtreeOutcome.Deleted, descendantsDeleted);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<DeleteAndSpliceOutcome> DeleteAndSpliceAsync(int nodeId, CancellationToken ct = default)
    {
        // Atomic splice-delete. Only valid for nodes with one out-edge
        // (Start blocked, Process valid; Decision throws because two
        // subtrees have no clean merge; terminals throw because they
        // have no surviving child).
        //
        // Order:
        //   1. Probe node + draft state + type.
        //   2. Read node.path1_node_id = surviving child (may be null).
        //   3. Find upstream parent + which slot of theirs points at us.
        //   4. Replace parent's slot with the surviving child id.
        //   5. Delete the node.
        //   6. Renumber the surviving subtree starting at the
        //      now-removed node's execution_level (shifts up by 1).
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            var probe = await connection.QuerySingleOrDefaultAsync<SpliceProbe>(
                new CommandDefinition(
                    @"SELECT n.workflow_definition_id AS WorkflowDefinitionId,
                             n.node_type_id           AS NodeTypeId,
                             n.execution_level        AS ExecutionLevel,
                             n.path1_node_id          AS Path1NodeId,
                             n.path2_node_id          AS Path2NodeId,
                             ver.request_state        AS RequestStateCode
                      FROM dbo.workflow_nodes n WITH (UPDLOCK, ROWLOCK)
                      INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
                      INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
                      WHERE n.id = @nodeId;",
                    new { nodeId }, transaction, cancellationToken: ct));

            if (probe is null)
            {
                transaction.Rollback();
                return DeleteAndSpliceOutcome.RejectedNodeNotFound;
            }
            if (probe.RequestStateCode != RequestStateCodes.Draft)
            {
                transaction.Rollback();
                return DeleteAndSpliceOutcome.RejectedNotDraft;
            }
            if (probe.NodeTypeId == WorkflowNodeTypeIds.Start)
            {
                transaction.Rollback();
                return DeleteAndSpliceOutcome.RejectedIsStart;
            }
            // Caller bug: splice is not defined for these types.
            if (probe.NodeTypeId == WorkflowNodeTypeIds.Decision)
            {
                transaction.Rollback();
                throw new ArgumentException(
                    $"DeleteAndSpliceAsync is not valid for Decision nodes (two subtrees, no clean splice). Node id={nodeId}.",
                    nameof(nodeId));
            }
            if (probe.NodeTypeId is WorkflowNodeTypeIds.Approved
                                or WorkflowNodeTypeIds.Rejected
                                or WorkflowNodeTypeIds.Cancelled)
            {
                transaction.Rollback();
                throw new ArgumentException(
                    $"DeleteAndSpliceAsync is not valid for terminal nodes (no child to splice). Node id={nodeId}.",
                    nameof(nodeId));
            }

            var survivingChild = probe.Path1NodeId;

            // Find the upstream parent + slot. There should be at most
            // one (no-merging invariant).
            var parentSlot = await connection.QuerySingleOrDefaultAsync<ParentSlotProbe>(
                new CommandDefinition(
                    @"SELECT TOP 1
                             id AS ParentId,
                             CASE WHEN path1_node_id = @nodeId THEN 1
                                  WHEN path2_node_id = @nodeId THEN 2
                                  ELSE 0 END AS Slot
                      FROM dbo.workflow_nodes
                      WHERE workflow_definition_id = @workflowId
                        AND (path1_node_id = @nodeId OR path2_node_id = @nodeId);",
                    new { nodeId, workflowId = probe.WorkflowDefinitionId },
                    transaction, cancellationToken: ct));

            if (parentSlot is null)
            {
                // Orphan. Defensive — shouldn't happen for Process in
                // normal workflows, but if it does, splice has no parent
                // to wire the surviving child into. Treat as "not found"
                // since structurally the node is unreachable.
                transaction.Rollback();
                return DeleteAndSpliceOutcome.RejectedNodeNotFound;
            }

            // 4. Replace parent's slot with survivingChild (may be null).
            var wireSql = parentSlot.Slot == 1
                ? "UPDATE dbo.workflow_nodes SET path1_node_id = @survivingChild WHERE id = @parentId;"
                : "UPDATE dbo.workflow_nodes SET path2_node_id = @survivingChild WHERE id = @parentId;";
            await connection.ExecuteAsync(new CommandDefinition(
                wireSql,
                new { parentSlot.ParentId, survivingChild },
                transaction, cancellationToken: ct));

            // 5. Delete the node. Its path1 is still pointing at the
            // surviving child, but that's fine — we delete the parent
            // (this node), not the child.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.workflow_nodes WHERE id = @nodeId;",
                new { nodeId }, transaction, cancellationToken: ct));

            // 6. Renumber the surviving subtree to shift up by 1. Skip
            // if there was no surviving child.
            if (survivingChild is int childId)
            {
                await RenumberSubtreeAsync(
                    connection, transaction, childId, probe.ExecutionLevel, ct);
            }

            transaction.Commit();
            return DeleteAndSpliceOutcome.Deleted;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Renumber the subtree rooted at <paramref name="rootNodeId"/>:
    /// root gets level <paramref name="rootLevel"/>, its path1/path2
    /// children get level rootLevel + 1, and so on transitively.
    /// </summary>
    /// <remarks>
    /// Uses a recursive CTE to walk the tree in one SQL statement.
    /// SQL Server's default MAXRECURSION is 100; workflow graphs deeper
    /// than that aren't a realistic concern for v1 (a recursive CTE that
    /// hits the limit throws an error rather than silently truncating,
    /// which is the right failure mode if we somehow hit it).
    ///
    /// Cycles would cause the CTE to loop until MAXRECURSION; the
    /// no-merging rule we enforce at SetPath time prevents cycles in
    /// practice, but if one slipped in (e.g. via raw SQL outside the
    /// repo) the renumber would error out — which is fine, the data is
    /// broken and someone needs to look at it.
    /// </remarks>
    private static async Task RenumberSubtreeAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        int rootNodeId,
        int rootLevel,
        CancellationToken ct)
    {
        const string sql = @"
            ;WITH descendants AS (
                SELECT id, CAST(@rootLevel AS int) AS new_level,
                       path1_node_id, path2_node_id
                FROM dbo.workflow_nodes
                WHERE id = @rootNodeId

                UNION ALL

                SELECT child.id, parent.new_level + 1,
                       child.path1_node_id, child.path2_node_id
                FROM dbo.workflow_nodes child
                INNER JOIN descendants parent
                    ON child.id = parent.path1_node_id
                    OR child.id = parent.path2_node_id
            )
            UPDATE n
            SET n.execution_level = d.new_level
            FROM dbo.workflow_nodes n
            INNER JOIN descendants d ON d.id = n.id;";

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { rootNodeId, rootLevel },
            transaction,
            cancellationToken: ct));
    }

    // ---- private projection holders for shape probes -------------------

    private sealed class SourceProbe
    {
        public int NodeTypeId { get; init; }
        public int WorkflowDefinitionId { get; init; }
        public int ExecutionLevel { get; init; }
        public string RequestStateCode { get; init; } = string.Empty;
    }

    private sealed class TargetProbe
    {
        public int WorkflowDefinitionId { get; init; }
    }

    private sealed class ParentCheck
    {
        public int SourceAlreadyPoints { get; init; }
        public int OtherParentCount { get; init; }
    }

    private sealed class NodeStateProbe
    {
        public int WorkflowDefinitionId { get; init; }
        public string RequestStateCode { get; init; } = string.Empty;
    }

    private sealed class NodeShapeProbe
    {
        public int WorkflowDefinitionId { get; init; }
        public int NodeTypeId { get; init; }
    }

    private sealed class NodeShapeStateProbe
    {
        public int WorkflowDefinitionId { get; init; }
        public int NodeTypeId { get; init; }
        public string RequestStateCode { get; init; } = string.Empty;
    }

    private sealed class SpliceProbe
    {
        public int WorkflowDefinitionId { get; init; }
        public int NodeTypeId { get; init; }
        public int ExecutionLevel { get; init; }
        public int? Path1NodeId { get; init; }
        public int? Path2NodeId { get; init; }
        public string RequestStateCode { get; init; } = string.Empty;
    }

    private sealed class ParentSlotProbe
    {
        public int ParentId { get; init; }
        public int Slot { get; init; }
    }
}
