using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Workflows;
using VendorSure.Services.Data;
using VendorSure.Services.Workflows;

namespace VendorSure.Infrastructure.Tests.Workflows;

/// <summary>
/// Integration tests for the block-catalog repository.
///
/// Originally a read-only repo (Phase 5 chunks 1-10); gained authoring
/// methods (CreateAsync, UpdateAsync, SetActiveAsync, GetByIdAsync,
/// ListAllAsync, CountWorkflowNodeReferencesAsync) during cleanup to
/// support the admin Blocks page. Tests cover both the original list
/// behavior and the new authoring surface.
///
/// Each test inserts its own catalog rows with prefixed names/
/// descriptions to avoid colliding with any seed data the dev DB may
/// already have, then filters the returned list to just those rows
/// for the assertion.
/// </summary>
public sealed class BlockCatalogRepositoryTests
    : IClassFixture<InfrastructureTestFixture>
{
    private readonly IBlockCatalogRepository _catalog;
    private readonly IDbConnectionFactory _connectionFactory;

    public BlockCatalogRepositoryTests(InfrastructureTestFixture fixture)
    {
        _catalog = fixture.ServiceProvider.GetRequiredService<IBlockCatalogRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task ListActiveAsync_returns_active_rows_with_all_columns_hydrated()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            // Two active rows — one Process (node_type_id=2) with
            // System actor, one Decision (3) with Human actor. The
            // mismatch between node type and actor type is intentional:
            // the two fields are independent (a Process block can be
            // System or Human or AI; same for Decision).
            var processId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_proc_n", description: $"{prefix}_proc",
                className: "VendorSure.Test.ProcBlock", isActive: true, color: "#abcdef",
                actorType: BlockCatalogActorType.System);
            insertedIds.Add(processId);

            var decisionId = await InsertBlockAsync(
                nodeTypeId: 3, name: $"{prefix}_dec_n", description: $"{prefix}_dec",
                className: "VendorSure.Test.DecBlock", isActive: true, color: null,
                path1Decision: "True", path2Decision: "False",
                actorType: BlockCatalogActorType.Human);
            insertedIds.Add(decisionId);

            var all = await _catalog.ListActiveAsync();
            var mine = all.Where(b => b.Description.StartsWith(prefix)).ToList();
            Assert.Equal(2, mine.Count);

            var proc = mine.Single(b => b.Id == processId);
            Assert.Equal(2, proc.NodeTypeId);
            Assert.Equal($"{prefix}_proc_n", proc.Name);
            Assert.Equal($"{prefix}_proc", proc.Description);
            Assert.Equal("VendorSure.Test.ProcBlock", proc.ClassName);
            Assert.True(proc.IsActive);
            Assert.Equal("#abcdef", proc.Color);
            Assert.Equal(BlockCatalogActorType.System, proc.ActorType);
            // Process blocks must have NULL path decisions per
            // CK_block_catalog_decision_labels.
            Assert.Null(proc.Path1Decision);
            Assert.Null(proc.Path2Decision);

            var dec = mine.Single(b => b.Id == decisionId);
            Assert.Equal(3, dec.NodeTypeId);
            Assert.Equal($"{prefix}_dec_n", dec.Name);
            Assert.Equal($"{prefix}_dec", dec.Description);
            Assert.Equal("VendorSure.Test.DecBlock", dec.ClassName);
            Assert.True(dec.IsActive);
            Assert.Null(dec.Color);
            Assert.Equal(BlockCatalogActorType.Human, dec.ActorType);
            // Decision blocks must have both path decisions populated.
            Assert.Equal("True", dec.Path1Decision);
            Assert.Equal("False", dec.Path2Decision);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task ListActiveAsync_excludes_inactive_rows()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var activeId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_active_n", description: $"{prefix}_active",
                className: "VendorSure.Test.Active", isActive: true, color: null);
            insertedIds.Add(activeId);

            var inactiveId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_inactive_n", description: $"{prefix}_inactive",
                className: "VendorSure.Test.Inactive", isActive: false, color: null);
            insertedIds.Add(inactiveId);

            var all = await _catalog.ListActiveAsync();
            var mine = all.Where(b => b.Description.StartsWith(prefix)).ToList();

            // The inactive row must be excluded; the active row must be present.
            Assert.Single(mine);
            Assert.Equal(activeId, mine[0].Id);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task ListActiveAsync_orders_by_node_type_then_name()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            // Insert four rows: 2 Process (zeta, alpha) and 2 Decision (gamma,
            // beta), in a deliberately wrong order so we can verify the SQL
            // ORDER BY clause actually sorts them. Names drive the sort
            // post-Chunk-10-cleanup; descriptions are unrelated to ordering.
            var procZ = await InsertBlockAsync(
                2, $"{prefix}_proc_zeta", "desc z", "X", true, null);
            insertedIds.Add(procZ);

            var decG = await InsertBlockAsync(
                3, $"{prefix}_dec_gamma", "desc g", "X", true, null, "T", "F");
            insertedIds.Add(decG);

            var procA = await InsertBlockAsync(
                2, $"{prefix}_proc_alpha", "desc a", "X", true, null);
            insertedIds.Add(procA);

            var decB = await InsertBlockAsync(
                3, $"{prefix}_dec_beta", "desc b", "X", true, null, "T", "F");
            insertedIds.Add(decB);

            var all = await _catalog.ListActiveAsync();
            var mine = all.Where(b => b.Name.StartsWith(prefix)).ToList();

            // Expected order: all Process (alpha, zeta), then all Decision
            // (beta, gamma). node_type_id ASC, name ASC.
            Assert.Collection(mine,
                b => Assert.Equal(procA, b.Id),
                b => Assert.Equal(procZ, b.Id),
                b => Assert.Equal(decB, b.Id),
                b => Assert.Equal(decG, b.Id));
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task ListActiveAsync_returns_empty_when_no_active_rows_match_filter()
    {
        // Sanity check: even on a clean db with no test data, the call doesn't
        // throw. Result may include real seed data — we just check the type.
        var result = await _catalog.ListActiveAsync();
        Assert.NotNull(result);
    }

    // ==== ListAllAsync ====================================================

    [Fact]
    public async Task ListAllAsync_returns_active_and_inactive()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var activeId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_active", description: "active",
                className: "X", isActive: true, color: null);
            var inactiveId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_inactive", description: "inactive",
                className: "X", isActive: false, color: null);
            insertedIds.Add(activeId);
            insertedIds.Add(inactiveId);

            var all = await _catalog.ListAllAsync();
            var mine = all.Where(b => b.Name.StartsWith(prefix)).ToList();
            Assert.Equal(2, mine.Count);
            Assert.Contains(mine, b => b.Id == activeId && b.IsActive);
            Assert.Contains(mine, b => b.Id == inactiveId && !b.IsActive);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    // ==== GetByIdAsync ====================================================

    [Fact]
    public async Task GetByIdAsync_returns_row_with_all_columns()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var id = await InsertBlockAsync(
                nodeTypeId: 3, name: $"{prefix}_get", description: "for get test",
                className: "VendorSure.Test.Get", isActive: true, color: "#123456",
                path1Decision: "Yes", path2Decision: "No",
                actorType: BlockCatalogActorType.Human);
            insertedIds.Add(id);

            var fetched = await _catalog.GetByIdAsync(id);
            Assert.NotNull(fetched);
            Assert.Equal(id, fetched!.Id);
            Assert.Equal(3, fetched.NodeTypeId);
            Assert.Equal($"{prefix}_get", fetched.Name);
            Assert.Equal("for get test", fetched.Description);
            Assert.Equal("VendorSure.Test.Get", fetched.ClassName);
            Assert.True(fetched.IsActive);
            Assert.Equal("#123456", fetched.Color);
            Assert.Equal("Yes", fetched.Path1Decision);
            Assert.Equal("No", fetched.Path2Decision);
            Assert.Equal(BlockCatalogActorType.Human, fetched.ActorType);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var fetched = await _catalog.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(fetched);
    }

    // ==== CreateAsync =====================================================

    [Fact]
    public async Task CreateAsync_inserts_process_block()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var seed = new BlockCatalog
            {
                NodeTypeId = 2,
                Name = $"{prefix}_proc",
                Description = "Created via CreateAsync",
                ClassName = "VendorSure.Test.CreatedProc",
                IsActive = true,
                Color = null,
                ActorType = BlockCatalogActorType.System,
            };

            var result = await _catalog.CreateAsync(seed);
            Assert.Equal(CreateBlockCatalogOutcome.Created, result.Outcome);
            Assert.NotNull(result.Id);
            insertedIds.Add(result.Id!.Value);

            var fetched = await _catalog.GetByIdAsync(result.Id.Value);
            Assert.NotNull(fetched);
            Assert.Equal($"{prefix}_proc", fetched!.Name);
            Assert.Null(fetched.Path1Decision);
            Assert.Null(fetched.Path2Decision);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task CreateAsync_inserts_decision_block_with_path_labels()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var seed = new BlockCatalog
            {
                NodeTypeId = 3,
                Name = $"{prefix}_dec",
                Description = "Decision via CreateAsync",
                ClassName = "VendorSure.Test.CreatedDec",
                IsActive = true,
                Color = "#abcdef",
                Path1Decision = "Pass",
                Path2Decision = "Fail",
                ActorType = BlockCatalogActorType.AI,
            };

            var result = await _catalog.CreateAsync(seed);
            Assert.Equal(CreateBlockCatalogOutcome.Created, result.Outcome);
            insertedIds.Add(result.Id!.Value);

            var fetched = await _catalog.GetByIdAsync(result.Id.Value);
            Assert.Equal("Pass", fetched!.Path1Decision);
            Assert.Equal("Fail", fetched.Path2Decision);
            Assert.Equal(BlockCatalogActorType.AI, fetched.ActorType);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task CreateAsync_rejects_invalid_node_type()
    {
        var seed = MinimalSeed("_test_bc_invalid_nt", nodeTypeId: 5);
        var result = await _catalog.CreateAsync(seed);
        Assert.Equal(CreateBlockCatalogOutcome.RejectedInvalidNodeType, result.Outcome);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task CreateAsync_rejects_invalid_actor_type()
    {
        var seed = MinimalSeed("_test_bc_invalid_actor", nodeTypeId: 2) with
        {
            ActorType = (BlockCatalogActorType)99,
        };
        var result = await _catalog.CreateAsync(seed);
        Assert.Equal(CreateBlockCatalogOutcome.RejectedInvalidActorType, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_rejects_decision_with_missing_path_labels()
    {
        var seed = MinimalSeed("_test_bc_dec_no_labels", nodeTypeId: 3);
        // Decision but no path labels.
        var result = await _catalog.CreateAsync(seed);
        Assert.Equal(CreateBlockCatalogOutcome.RejectedDecisionLabelsRequired, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_rejects_process_with_path_labels()
    {
        var seed = MinimalSeed("_test_bc_proc_has_labels", nodeTypeId: 2) with
        {
            Path1Decision = "Yes",
            Path2Decision = "No",
        };
        var result = await _catalog.CreateAsync(seed);
        Assert.Equal(CreateBlockCatalogOutcome.RejectedProcessLabelsForbidden, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_rejects_malformed_color()
    {
        var seed = MinimalSeed("_test_bc_bad_color", nodeTypeId: 2) with
        {
            Color = "not-a-hex",
        };
        var result = await _catalog.CreateAsync(seed);
        Assert.Equal(CreateBlockCatalogOutcome.RejectedInvalidColor, result.Outcome);
    }

    // ==== UpdateAsync =====================================================

    [Fact]
    public async Task UpdateAsync_updates_editable_fields()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var id = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_orig", description: "orig",
                className: "VendorSure.Test.Orig", isActive: true, color: null);
            insertedIds.Add(id);

            var edited = new BlockCatalog
            {
                Id = id,
                NodeTypeId = 2,                           // preserved by repo
                Name = $"{prefix}_updated",
                Description = "updated",
                ClassName = "VendorSure.Test.Orig",       // unchanged
                IsActive = true,
                Color = "#fedcba",
                ActorType = BlockCatalogActorType.Human,
            };

            var result = await _catalog.UpdateAsync(edited);
            Assert.Equal(UpdateBlockCatalogOutcome.Updated, result);

            var fetched = await _catalog.GetByIdAsync(id);
            Assert.Equal($"{prefix}_updated", fetched!.Name);
            Assert.Equal("updated", fetched.Description);
            Assert.Equal("#fedcba", fetched.Color);
            Assert.Equal(BlockCatalogActorType.Human, fetched.ActorType);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task UpdateAsync_returns_not_found_for_unknown_id()
    {
        var edited = MinimalSeed("_test_bc_nf", nodeTypeId: 2) with
        {
            Id = int.MaxValue - 1,
            ClassName = "X",
        };
        var result = await _catalog.UpdateAsync(edited);
        Assert.Equal(UpdateBlockCatalogOutcome.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_allows_class_name_change_when_block_unused()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var id = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_n", description: "d",
                className: "VendorSure.Test.Old", isActive: true, color: null);
            insertedIds.Add(id);

            var edited = (await _catalog.GetByIdAsync(id))! with
            {
                ClassName = "VendorSure.Test.New",
            };
            var result = await _catalog.UpdateAsync(edited);
            Assert.Equal(UpdateBlockCatalogOutcome.Updated, result);

            var fetched = await _catalog.GetByIdAsync(id);
            Assert.Equal("VendorSure.Test.New", fetched!.ClassName);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task UpdateAsync_rejects_class_name_change_when_block_in_use()
    {
        // Seed a workflow that uses the block, then try to change its
        // class_name. Repo refuses with RejectedClassNameChangeBlocked.
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var blockIds = new List<int>();
        var typeIds = new List<int>();
        try
        {
            var blockId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_inuse", description: "in use",
                className: "VendorSure.Test.InUse", isActive: true, color: null);
            blockIds.Add(blockId);

            // Build the FK chain: request_type → version → workflow → node.
            var typeId = await InsertRequestTypeAsync(prefix);
            typeIds.Add(typeId);
            var workflowId = await InsertWorkflowWithProcessNodeAsync(typeId, blockId);

            var edited = (await _catalog.GetByIdAsync(blockId))! with
            {
                ClassName = "VendorSure.Test.ChangedClass",
            };

            var result = await _catalog.UpdateAsync(edited);
            Assert.Equal(UpdateBlockCatalogOutcome.RejectedClassNameChangeBlocked, result);

            // Verify the row was untouched.
            var fetched = await _catalog.GetByIdAsync(blockId);
            Assert.Equal("VendorSure.Test.InUse", fetched!.ClassName);
        }
        finally
        {
            await CleanupWorkflowChainAsync(typeIds);
            await CleanupAsync(blockIds);
        }
    }

    [Fact]
    public async Task UpdateAsync_allows_color_change_when_class_name_differs_only_in_whitespace()
    {
        // Regression test for a bug surfaced via the admin UI: when a
        // block_catalog row's class_name has trailing whitespace (from
        // legacy data or a poorly-trimmed seed), the dialog's
        // _className.Trim() on save makes the submitted ClassName
        // differ from the stored one. The repo used to interpret that
        // as a class_name change and refuse the update if the block
        // was in use — even when the user only changed the color.
        //
        // Fix: compare trimmed values in UpdateAsync's in-use check.
        // This test seeds a block with trailing whitespace in its
        // class_name AND a referencing workflow_node (so the in-use
        // check would fire if the comparison failed), then submits
        // a color change with a trimmed class_name. Must succeed.
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var blockIds = new List<int>();
        var typeIds = new List<int>();
        try
        {
            var blockId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_ws", description: "ws-tolerant",
                className: "VendorSure.Test.WhitespaceClass   ", // trailing whitespace
                isActive: true, color: null);
            blockIds.Add(blockId);

            var typeId = await InsertRequestTypeAsync(prefix);
            typeIds.Add(typeId);
            await InsertWorkflowWithProcessNodeAsync(typeId, blockId);

            // User edits color; dialog trims class_name on save. The
            // comparison must not see this as a class_name change.
            var edited = (await _catalog.GetByIdAsync(blockId))! with
            {
                ClassName = "VendorSure.Test.WhitespaceClass", // no trailing ws
                Color = "#abcdef",
            };

            var result = await _catalog.UpdateAsync(edited);
            Assert.Equal(UpdateBlockCatalogOutcome.Updated, result);

            var fetched = await _catalog.GetByIdAsync(blockId);
            Assert.Equal("#abcdef", fetched!.Color);
            // class_name written through with the trimmed value;
            // acceptable since the comparison treated them as equal.
            Assert.Equal("VendorSure.Test.WhitespaceClass", fetched.ClassName);
        }
        finally
        {
            await CleanupWorkflowChainAsync(typeIds);
            await CleanupAsync(blockIds);
        }
    }

    // ==== SetActiveAsync ==================================================

    [Fact]
    public async Task SetActiveAsync_toggles_active_flag()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var id = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_a", description: "a",
                className: "X", isActive: true, color: null);
            insertedIds.Add(id);

            var deactivate = await _catalog.SetActiveAsync(id, false);
            Assert.Equal(SetActiveOutcome.Updated, deactivate);
            Assert.False((await _catalog.GetByIdAsync(id))!.IsActive);

            var reactivate = await _catalog.SetActiveAsync(id, true);
            Assert.Equal(SetActiveOutcome.Updated, reactivate);
            Assert.True((await _catalog.GetByIdAsync(id))!.IsActive);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task SetActiveAsync_returns_not_found_for_unknown_id()
    {
        var result = await _catalog.SetActiveAsync(int.MaxValue - 1, false);
        Assert.Equal(SetActiveOutcome.NotFound, result);
    }

    // ==== CountWorkflowNodeReferencesAsync ================================

    [Fact]
    public async Task CountWorkflowNodeReferencesAsync_returns_zero_when_unused()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var blockId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_unused", description: "u",
                className: "X", isActive: true, color: null);
            insertedIds.Add(blockId);

            var count = await _catalog.CountWorkflowNodeReferencesAsync(blockId);
            Assert.Equal(0, count);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task CountWorkflowNodeReferencesAsync_counts_referencing_nodes()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var blockIds = new List<int>();
        var typeIds = new List<int>();
        try
        {
            var blockId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_counted", description: "c",
                className: "X", isActive: true, color: null);
            blockIds.Add(blockId);

            var typeId = await InsertRequestTypeAsync(prefix);
            typeIds.Add(typeId);
            await InsertWorkflowWithProcessNodeAsync(typeId, blockId);

            var count = await _catalog.CountWorkflowNodeReferencesAsync(blockId);
            Assert.Equal(1, count);
        }
        finally
        {
            await CleanupWorkflowChainAsync(typeIds);
            await CleanupAsync(blockIds);
        }
    }

    // --- helpers ---------------------------------------------------------

    private async Task<int> InsertBlockAsync(
        int nodeTypeId, string name, string description, string className, bool isActive, string? color,
        string? path1Decision = null, string? path2Decision = null,
        BlockCatalogActorType actorType = BlockCatalogActorType.System)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(@"
            INSERT INTO dbo.block_catalog
                (node_type_id, name, description, class_name, is_active, color,
                 path1_decision, path2_decision, actor_type)
            VALUES (@nodeTypeId, @name, @description, @className, @isActive, @color,
                    @path1Decision, @path2Decision, @actorType);
            SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { nodeTypeId, name, description, className, isActive, color,
                  path1Decision, path2Decision, actorType = (int)actorType });
    }

    private async Task CleanupAsync(IEnumerable<int> ids)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        foreach (var id in ids)
        {
            await connection.ExecuteAsync(
                "DELETE FROM dbo.block_catalog WHERE id = @id;", new { id });
        }
    }

    /// <summary>
    /// Convenience seed for Create/Update tests that don't care about
    /// the full BlockCatalog shape — just need a structurally-valid
    /// rejection-test starting point. Builds a System-actor block with
    /// no color and no path labels (caller adjusts via `with` for the
    /// specific rejection being tested).
    /// </summary>
    private static BlockCatalog MinimalSeed(string namePrefix, int nodeTypeId)
    {
        return new BlockCatalog
        {
            NodeTypeId = nodeTypeId,
            Name = namePrefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
            Description = "test seed",
            ClassName = "VendorSure.Test.Seed",
            IsActive = true,
            Color = null,
            ActorType = BlockCatalogActorType.System,
        };
    }

    /// <summary>
    /// Raw-inserts a request_type row. Required upstream of the FK
    /// chain when seeding a workflow node that references a catalog
    /// block. Returns the new row's id; caller is responsible for
    /// cleanup via <see cref="CleanupWorkflowChainAsync"/>.
    /// </summary>
    private async Task<int> InsertRequestTypeAsync(string namePrefix)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        return await c.QuerySingleAsync<int>(@"
            INSERT INTO dbo.request_types (name, description, is_active)
            VALUES (@name, 'test', 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { name = namePrefix + "_rt_" + Guid.NewGuid().ToString("N").Substring(0, 8) });
    }

    /// <summary>
    /// Raw-inserts the FK chain
    ///   request_type_version → workflow_definition → workflow_nodes
    /// terminating in a single Process node that references the given
    /// catalog block. Used by tests that need to verify in-use checks
    /// (CountWorkflowNodeReferencesAsync, UpdateAsync's
    /// RejectedClassNameChangeBlocked path).
    ///
    /// Returns the workflow_definition_id; cleanup goes through the
    /// request type via <see cref="CleanupWorkflowChainAsync"/>.
    /// </summary>
    private async Task<int> InsertWorkflowWithProcessNodeAsync(int requestTypeId, int blockId)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        var sql = @"
            DECLARE @verId int;
            INSERT INTO dbo.request_type_versions
                (request_type_id, version, request_state, created_ts)
            VALUES (@requestTypeId, 1, 'Draft', SYSUTCDATETIME());
            SET @verId = CAST(SCOPE_IDENTITY() AS int);

            DECLARE @wfId int;
            INSERT INTO dbo.workflow_definitions
                (request_type_version_id, name, is_active)
            VALUES (@verId, 'test-wf', 1);
            SET @wfId = CAST(SCOPE_IDENTITY() AS int);

            -- One Process node referencing the test block. Level 0
            -- (orphan-style) is fine for this purpose — we're not
            -- exercising the workflow engine, just the FK reference.
            INSERT INTO dbo.workflow_nodes
                (workflow_definition_id, node_type_id, block_catalog_id,
                 execution_level, path1_node_id, path2_node_id)
            VALUES (@wfId, 2, @blockId, 0, NULL, NULL);

            SELECT @wfId;";
        return await c.QuerySingleAsync<int>(
            new CommandDefinition(sql, new { requestTypeId, blockId }));
    }

    /// <summary>
    /// Cleans up the FK chain seeded by <see cref="InsertWorkflowWithProcessNodeAsync"/>.
    /// Order matters: workflow_nodes → workflow_definitions →
    /// request_type_versions → request_types.
    /// </summary>
    private async Task CleanupWorkflowChainAsync(IEnumerable<int> requestTypeIds)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        foreach (var typeId in requestTypeIds)
        {
            await c.ExecuteAsync(@"
                DELETE n FROM dbo.workflow_nodes n
                INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
                INNER JOIN dbo.request_type_versions v ON v.id = wd.request_type_version_id
                WHERE v.request_type_id = @typeId;

                DELETE wd FROM dbo.workflow_definitions wd
                INNER JOIN dbo.request_type_versions v ON v.id = wd.request_type_version_id
                WHERE v.request_type_id = @typeId;

                DELETE FROM dbo.request_type_versions WHERE request_type_id = @typeId;
                DELETE FROM dbo.request_types WHERE id = @typeId;",
                new { typeId });
        }
    }
}
