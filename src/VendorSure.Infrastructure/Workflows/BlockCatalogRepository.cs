using System.Text.RegularExpressions;
using Dapper;
using VendorSure.Domain.Workflows;
using VendorSure.Services.Data;
using VendorSure.Services.Workflows;

namespace VendorSure.Infrastructure.Workflows;

internal sealed class BlockCatalogRepository : IBlockCatalogRepository
{
    private const string SelectColumns = @"
        id              AS Id,
        node_type_id    AS NodeTypeId,
        name            AS Name,
        description     AS Description,
        class_name      AS ClassName,
        is_active       AS IsActive,
        color           AS Color,
        path1_decision  AS Path1Decision,
        path2_decision  AS Path2Decision,
        actor_type      AS ActorType";

    private const int NodeTypeProcess = 2;
    private const int NodeTypeDecision = 3;

    // Matches CK_block_catalog_color in data-model.sql.
    private static readonly Regex ColorPattern =
        new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    private readonly IDbConnectionFactory _connectionFactory;

    public BlockCatalogRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<BlockCatalog>> ListActiveAsync(
        CancellationToken ct = default)
    {
        // Order matches how the palette renders: group by node type so
        // Process blocks cluster together and Decision blocks cluster
        // together, then alphabetic by name within each group so the
        // palette is stable across sessions and reads naturally in the
        // picker dialog.
        const string sql = @"
            SELECT " + SelectColumns + @"
            FROM dbo.block_catalog
            WHERE is_active = 1
            ORDER BY node_type_id ASC, name ASC;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<BlockCatalog>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<BlockCatalog>> ListAllAsync(
        CancellationToken ct = default)
    {
        // Same ordering as ListActiveAsync but without the is_active
        // filter. Admin page renders inactive rows muted; the order
        // still groups by type for readability.
        const string sql = @"
            SELECT " + SelectColumns + @"
            FROM dbo.block_catalog
            ORDER BY node_type_id ASC, name ASC;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<BlockCatalog>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<BlockCatalog?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT " + SelectColumns + @"
            FROM dbo.block_catalog
            WHERE id = @id;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<BlockCatalog>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<CreateBlockCatalogResult> CreateAsync(
        BlockCatalog seed, CancellationToken ct = default)
    {
        // Validate in C# before reaching the DB so the caller gets a
        // clean outcome enum rather than a SqlException for any
        // CHECK-constraint violation.
        var validationOutcome = ValidateShape(seed);
        if (validationOutcome.HasValue)
        {
            return new CreateBlockCatalogResult(validationOutcome.Value, null);
        }

        const string sql = @"
            INSERT INTO dbo.block_catalog
                (node_type_id, name, description, class_name, is_active,
                 color, path1_decision, path2_decision, actor_type)
            VALUES
                (@NodeTypeId, @Name, @Description, @ClassName, @IsActive,
                 @Color, @Path1Decision, @Path2Decision, @ActorType);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var newId = await connection.QuerySingleAsync<int>(new CommandDefinition(
            sql,
            new
            {
                seed.NodeTypeId,
                seed.Name,
                seed.Description,
                seed.ClassName,
                seed.IsActive,
                seed.Color,
                seed.Path1Decision,
                seed.Path2Decision,
                ActorType = (int)seed.ActorType,
            },
            cancellationToken: ct));

        return new CreateBlockCatalogResult(CreateBlockCatalogOutcome.Created, newId);
    }

    public async Task<UpdateBlockCatalogOutcome> UpdateAsync(
        BlockCatalog edited, CancellationToken ct = default)
    {
        // Same shape validation as Create, mapped to the Update enum
        // values. node_type_id rejection isn't reachable here because
        // we don't touch it (the existing row's value wins).
        var createOutcome = ValidateShape(edited);
        if (createOutcome.HasValue)
        {
            // Translate Create-side rejections to Update-side
            // equivalents. The InvalidNodeType case can't apply because
            // we don't update node_type_id; skip that branch.
            switch (createOutcome.Value)
            {
                case CreateBlockCatalogOutcome.RejectedInvalidActorType:
                    return UpdateBlockCatalogOutcome.RejectedInvalidActorType;
                case CreateBlockCatalogOutcome.RejectedDecisionLabelsRequired:
                    return UpdateBlockCatalogOutcome.RejectedDecisionLabelsRequired;
                case CreateBlockCatalogOutcome.RejectedProcessLabelsForbidden:
                    return UpdateBlockCatalogOutcome.RejectedProcessLabelsForbidden;
                case CreateBlockCatalogOutcome.RejectedInvalidColor:
                    return UpdateBlockCatalogOutcome.RejectedInvalidColor;
                // RejectedInvalidNodeType not possible: we don't update node_type_id.
            }
        }

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var transaction = connection.BeginTransaction();

        try
        {
            // Probe the current row with UPDLOCK so a concurrent caller
            // can't slip a class_name change past the in-use check.
            var current = await connection.QuerySingleOrDefaultAsync<BlockCatalog>(
                new CommandDefinition(
                    @"SELECT " + SelectColumns + @"
                      FROM dbo.block_catalog WITH (UPDLOCK, ROWLOCK)
                      WHERE id = @Id;",
                    new { edited.Id },
                    transaction, cancellationToken: ct));

            if (current is null)
            {
                transaction.Rollback();
                return UpdateBlockCatalogOutcome.NotFound;
            }

            // If class_name is changing, refuse when any workflow_nodes
            // row references this block. The admin must deactivate and
            // create a new row with the new class_name instead.
            //
            // Compare trimmed values: trailing whitespace in either the
            // stored row or the submitted edit is semantically meaningless
            // for a .NET type name, so a difference only in whitespace
            // shouldn't trigger the in-use check. Without this, an admin
            // editing color (or anything else) on a block whose stored
            // class_name has trailing whitespace gets a misleading
            // "can't change class name" error because the dialog's
            // Trim() on save makes the submitted value differ from
            // the stored one.
            var currentClass = (current.ClassName ?? string.Empty).Trim();
            var editedClass = (edited.ClassName ?? string.Empty).Trim();
            if (!string.Equals(currentClass, editedClass, StringComparison.Ordinal))
            {
                var refCount = await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        @"SELECT COUNT(*) FROM dbo.workflow_nodes
                          WHERE block_catalog_id = @Id;",
                        new { edited.Id },
                        transaction, cancellationToken: ct));

                if (refCount > 0)
                {
                    transaction.Rollback();
                    return UpdateBlockCatalogOutcome.RejectedClassNameChangeBlocked;
                }
            }

            // node_type_id is preserved from the existing row. Caller
            // can't change it (would break referencing workflow_nodes).
            await connection.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.block_catalog
                  SET name           = @Name,
                      description    = @Description,
                      class_name     = @ClassName,
                      is_active      = @IsActive,
                      color          = @Color,
                      path1_decision = @Path1Decision,
                      path2_decision = @Path2Decision,
                      actor_type     = @ActorType
                  WHERE id = @Id;",
                new
                {
                    edited.Id,
                    edited.Name,
                    edited.Description,
                    edited.ClassName,
                    edited.IsActive,
                    edited.Color,
                    edited.Path1Decision,
                    edited.Path2Decision,
                    ActorType = (int)edited.ActorType,
                },
                transaction, cancellationToken: ct));

            transaction.Commit();
            return UpdateBlockCatalogOutcome.Updated;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<SetActiveOutcome> SetActiveAsync(
        int id, bool isActive, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.block_catalog
              SET is_active = @isActive
              WHERE id = @id;",
            new { id, isActive },
            cancellationToken: ct));

        return rows > 0 ? SetActiveOutcome.Updated : SetActiveOutcome.NotFound;
    }

    public async Task<int> CountWorkflowNodeReferencesAsync(
        int blockId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            @"SELECT COUNT(*) FROM dbo.workflow_nodes
              WHERE block_catalog_id = @blockId;",
            new { blockId },
            cancellationToken: ct));
    }

    /// <summary>
    /// Shared pre-validation for Create and Update. Returns the rejection
    /// outcome (cast to Create's enum since the values overlap), or null
    /// when the shape is valid. UpdateAsync translates the result to its
    /// own enum.
    /// </summary>
    private static CreateBlockCatalogOutcome? ValidateShape(BlockCatalog seed)
    {
        if (seed.NodeTypeId != NodeTypeProcess && seed.NodeTypeId != NodeTypeDecision)
        {
            return CreateBlockCatalogOutcome.RejectedInvalidNodeType;
        }

        if (seed.ActorType != BlockCatalogActorType.System
            && seed.ActorType != BlockCatalogActorType.Human
            && seed.ActorType != BlockCatalogActorType.AI)
        {
            return CreateBlockCatalogOutcome.RejectedInvalidActorType;
        }

        if (seed.NodeTypeId == NodeTypeDecision)
        {
            if (string.IsNullOrEmpty(seed.Path1Decision)
                || string.IsNullOrEmpty(seed.Path2Decision))
            {
                return CreateBlockCatalogOutcome.RejectedDecisionLabelsRequired;
            }
        }
        else if (seed.NodeTypeId == NodeTypeProcess)
        {
            if (!string.IsNullOrEmpty(seed.Path1Decision)
                || !string.IsNullOrEmpty(seed.Path2Decision))
            {
                return CreateBlockCatalogOutcome.RejectedProcessLabelsForbidden;
            }
        }

        if (seed.Color is not null && !ColorPattern.IsMatch(seed.Color))
        {
            return CreateBlockCatalogOutcome.RejectedInvalidColor;
        }

        return null;
    }
}
