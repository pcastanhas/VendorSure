using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;
using VendorSure.Services.Workflows;

namespace VendorSure.Infrastructure.Tests.Workflows;

/// <summary>
/// Integration tests for the workflow-definition repository.
/// Each test creates its own RequestType + Draft Version via the existing
/// repositories, exercises the workflow surface, and tears down in FK
/// order (workflows → versions → type).
/// </summary>
public sealed class WorkflowDefinitionRepositoryTests
    : IClassFixture<InfrastructureTestFixture>
{
    private readonly IWorkflowDefinitionRepository _workflows;
    private readonly IRequestTypeRepository _types;
    private readonly IRequestTypeVersionRepository _versions;
    private readonly IDbConnectionFactory _connectionFactory;

    public WorkflowDefinitionRepositoryTests(InfrastructureTestFixture fixture)
    {
        _workflows = fixture.ServiceProvider.GetRequiredService<IWorkflowDefinitionRepository>();
        _types = fixture.ServiceProvider.GetRequiredService<IRequestTypeRepository>();
        _versions = fixture.ServiceProvider.GetRequiredService<IRequestTypeVersionRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task CreateAsync_returns_new_id_and_persists_fields()
    {
        await using var f = await SetupAsync();

        var result = await _workflows.CreateAsync(f.VersionId, "_test_wf_main", "first workflow");
        Assert.Equal(CreateWorkflowOutcome.Created, result.Outcome);
        Assert.NotNull(result.Id);

        var fetched = await _workflows.GetByIdAsync(result.Id!.Value);
        Assert.NotNull(fetched);
        Assert.Equal(f.VersionId, fetched!.RequestTypeVersionId);
        Assert.Equal("_test_wf_main", fetched.Name);
        Assert.Equal("first workflow", fetched.Notes);
        Assert.Null(fetched.StartNodeId);  // freshly-created workflow has no start node
    }

    [Fact]
    public async Task CreateAsync_persists_null_notes()
    {
        await using var f = await SetupAsync();

        var result = await _workflows.CreateAsync(f.VersionId, "_test_wf_nullnotes", notes: null);
        Assert.Equal(CreateWorkflowOutcome.Created, result.Outcome);

        var fetched = await _workflows.GetByIdAsync(result.Id!.Value);
        Assert.Null(fetched!.Notes);
    }

    [Fact]
    public async Task CreateAsync_returns_RejectedVersionNotFound_for_unknown_version()
    {
        var result = await _workflows.CreateAsync(int.MaxValue - 1, "_test_wf_orphan", null);
        Assert.Equal(CreateWorkflowOutcome.RejectedVersionNotFound, result.Outcome);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task CreateAsync_returns_RejectedNotDraft_for_InService_version()
    {
        await using var f = await SetupAsync();
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _workflows.CreateAsync(f.VersionId, "_test_wf_late", null);
        Assert.Equal(CreateWorkflowOutcome.RejectedNotDraft, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_returns_RejectedNameConflict_for_duplicate_within_version()
    {
        await using var f = await SetupAsync();
        var first = await _workflows.CreateAsync(f.VersionId, "_test_wf_dup", null);
        Assert.Equal(CreateWorkflowOutcome.Created, first.Outcome);

        var second = await _workflows.CreateAsync(f.VersionId, "_test_wf_dup", "different notes");
        Assert.Equal(CreateWorkflowOutcome.RejectedNameConflict, second.Outcome);
        Assert.Null(second.Id);
    }

    [Fact]
    public async Task CreateAsync_allows_same_name_on_different_versions()
    {
        // Two unrelated types each with a Draft. Same workflow name on each
        // should succeed — the UNIQUE is per (version, name).
        await using var fA = await SetupAsync();
        await using var fB = await SetupAsync();

        var a = await _workflows.CreateAsync(fA.VersionId, "_test_wf_shared", null);
        var b = await _workflows.CreateAsync(fB.VersionId, "_test_wf_shared", null);

        Assert.Equal(CreateWorkflowOutcome.Created, a.Outcome);
        Assert.Equal(CreateWorkflowOutcome.Created, b.Outcome);
    }

    [Fact]
    public async Task ListByVersionIdAsync_returns_workflows_ordered_by_name()
    {
        await using var f = await SetupAsync();

        await _workflows.CreateAsync(f.VersionId, "_test_wf_ccc", null);
        await _workflows.CreateAsync(f.VersionId, "_test_wf_aaa", null);
        await _workflows.CreateAsync(f.VersionId, "_test_wf_bbb", null);

        var list = await _workflows.ListByVersionIdAsync(f.VersionId);
        Assert.Equal(3, list.Count);
        var names = list.Select(w => w.Name).ToArray();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToArray(), names);
    }

    [Fact]
    public async Task ListByVersionIdAsync_returns_empty_for_version_with_no_workflows()
    {
        await using var f = await SetupAsync();
        var list = await _workflows.ListByVersionIdAsync(f.VersionId);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var fetched = await _workflows.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task UpdateAsync_edits_name_and_notes()
    {
        await using var f = await SetupAsync();
        var created = await _workflows.CreateAsync(f.VersionId, "_test_wf_orig", "orig notes");

        var result = await _workflows.UpdateAsync(created.Id!.Value, "_test_wf_edited", "edited notes");
        Assert.Equal(UpdateWorkflowResult.Updated, result);

        var fetched = await _workflows.GetByIdAsync(created.Id.Value);
        Assert.Equal("_test_wf_edited", fetched!.Name);
        Assert.Equal("edited notes", fetched.Notes);
    }

    [Fact]
    public async Task UpdateAsync_allows_keeping_same_name_no_op()
    {
        // A no-op rename should succeed — the conflict check is for OTHER
        // rows with the same name, so updating a row to its own name is
        // legal (matches the established pattern from Phase 2/4).
        await using var f = await SetupAsync();
        var created = await _workflows.CreateAsync(f.VersionId, "_test_wf_same", "n1");

        var result = await _workflows.UpdateAsync(created.Id!.Value, "_test_wf_same", "n2");
        Assert.Equal(UpdateWorkflowResult.Updated, result);

        var fetched = await _workflows.GetByIdAsync(created.Id.Value);
        Assert.Equal("n2", fetched!.Notes);
    }

    [Fact]
    public async Task UpdateAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _workflows.UpdateAsync(int.MaxValue - 1, "x", null);
        Assert.Equal(UpdateWorkflowResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_returns_RejectedNotDraft_when_parent_is_not_Draft()
    {
        await using var f = await SetupAsync();
        var created = await _workflows.CreateAsync(f.VersionId, "_test_wf_frozen", null);
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _workflows.UpdateAsync(created.Id!.Value, "should not change", null);
        Assert.Equal(UpdateWorkflowResult.RejectedNotDraft, result);

        // Restore Draft to read back; row must be unchanged.
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Draft);
        var fetched = await _workflows.GetByIdAsync(created.Id.Value);
        Assert.Equal("_test_wf_frozen", fetched!.Name);
    }

    [Fact]
    public async Task UpdateAsync_returns_RejectedNameConflict_when_renaming_to_another_workflows_name()
    {
        await using var f = await SetupAsync();
        await _workflows.CreateAsync(f.VersionId, "_test_wf_taken", null);
        var second = await _workflows.CreateAsync(f.VersionId, "_test_wf_renaming", null);

        var result = await _workflows.UpdateAsync(second.Id!.Value, "_test_wf_taken", null);
        Assert.Equal(UpdateWorkflowResult.RejectedNameConflict, result);
    }

    [Fact]
    public async Task DeleteAsync_removes_workflow_with_no_nodes()
    {
        await using var f = await SetupAsync();
        var created = await _workflows.CreateAsync(f.VersionId, "_test_wf_todelete", null);

        var result = await _workflows.DeleteAsync(created.Id!.Value);
        Assert.Equal(DeleteWorkflowResult.Deleted, result);

        Assert.Null(await _workflows.GetByIdAsync(created.Id.Value));
    }

    [Fact]
    public async Task DeleteAsync_cascades_to_nodes_including_self_referential_paths()
    {
        // Build a small graph by raw SQL (Chunk 3 hasn't shipped the node
        // repository yet, but the schema is ready). Three nodes: Start →
        // Process → Approved, where Start.path1 → Process and
        // Process.path1 → Approved. Also set workflow_definitions.start_node_id.
        // Delete should null out the path/start FKs, delete the nodes,
        // delete the workflow row, all transactionally.
        await using var f = await SetupAsync();
        var created = await _workflows.CreateAsync(f.VersionId, "_test_wf_cascade", null);
        var workflowId = created.Id!.Value;

        // We need a block_catalog row for the Process node. Insert one for
        // this test and clean it up afterward. Decision (node_type_id=3) or
        // Process (2) both work; pick Process. The CHECK constraint
        // CK_block_catalog_node_type allows 2 or 3.
        var blockId = await InsertTestBlockAsync();
        var approvedTypeId = 4;  // Approved terminal
        var startTypeId = 1;
        var processTypeId = 2;

        int startNodeId, processNodeId, approvedNodeId;
        try
        {
            // Insert nodes in dependency order: terminal first (no out-edges
            // so safe to insert), then process pointing at it, then start
            // pointing at process.
            approvedNodeId = await InsertTestNodeAsync(
                workflowId, approvedTypeId, blockCatalogId: null,
                path1: null, path2: null);

            processNodeId = await InsertTestNodeAsync(
                workflowId, processTypeId, blockCatalogId: blockId,
                path1: approvedNodeId, path2: null);

            startNodeId = await InsertTestNodeAsync(
                workflowId, startTypeId, blockCatalogId: null,
                path1: processNodeId, path2: null);

            // Point the workflow's start_node_id at the Start node.
            await SetStartNodeAsync(workflowId, startNodeId);

            // Now delete. All FKs (start_node_id, both path1s) should be
            // nulled, then nodes deleted, then workflow deleted.
            var result = await _workflows.DeleteAsync(workflowId);
            Assert.Equal(DeleteWorkflowResult.Deleted, result);

            Assert.Null(await _workflows.GetByIdAsync(workflowId));
            Assert.Equal(0, await NodeCountForWorkflowAsync(workflowId));
        }
        finally
        {
            await DeleteTestBlockAsync(blockId);
        }
    }

    [Fact]
    public async Task DeleteAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _workflows.DeleteAsync(int.MaxValue - 1);
        Assert.Equal(DeleteWorkflowResult.NotFound, result);
    }

    [Fact]
    public async Task DeleteAsync_returns_RejectedNotDraft_when_parent_is_not_Draft()
    {
        await using var f = await SetupAsync();
        var created = await _workflows.CreateAsync(f.VersionId, "_test_wf_locked", null);
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Superseded);

        var result = await _workflows.DeleteAsync(created.Id!.Value);
        Assert.Equal(DeleteWorkflowResult.RejectedNotDraft, result);

        // Restore Draft and confirm the workflow row still exists.
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Draft);
        Assert.NotNull(await _workflows.GetByIdAsync(created.Id.Value));
    }

    // ---- fixture / helpers ----------------------------------------------

    private sealed class Fixture : IAsyncDisposable
    {
        public required int TypeId { get; init; }
        public required int VersionId { get; init; }
        public required Func<Task> Cleanup { get; init; }
        private bool _disposed;
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await Cleanup();
        }
    }

    private async Task<Fixture> SetupAsync()
    {
        var typeResult = await _types.CreateWithFirstDraftAsync(new RequestType
        {
            Name = $"_test_rt_{Guid.NewGuid():N}",
            IsExplanationRequired = false,
            IsActive = true,
        });
        var typeId = typeResult.RequestTypeId!.Value;
        var versionId = typeResult.VersionId!.Value;

        return new Fixture
        {
            TypeId = typeId,
            VersionId = versionId,
            Cleanup = async () =>
            {
                // FK order: nulls + nodes + workflow defs first, then versions,
                // then type. The version may be in InService/Superseded by
                // the time we get here (some tests force-flip it), so use
                // raw SQL that bypasses the Draft gate.
                await CleanupWorkflowsForTypeAsync(typeId);
                await DeleteVersionsForTypeAsync(typeId);
                await DeleteTypeAsync(typeId);
            },
        };
    }

    private async Task CleanupWorkflowsForTypeAsync(int typeId)
    {
        // Mirror the repo's delete sequence but scoped to a whole type, and
        // without the Draft gate (cleanup must work even when tests have
        // forced a version into a non-Draft state).
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(@"
            -- 1. Null start_node_id on all workflows of this type.
            UPDATE wd
            SET wd.start_node_id = NULL
            FROM dbo.workflow_definitions wd
            INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
            WHERE ver.request_type_id = @typeId;

            -- 2. Null path1/path2 on all nodes of those workflows.
            UPDATE n
            SET n.path1_node_id = NULL, n.path2_node_id = NULL
            FROM dbo.workflow_nodes n
            INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
            INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
            WHERE ver.request_type_id = @typeId;

            -- 3. Delete nodes.
            DELETE n
            FROM dbo.workflow_nodes n
            INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
            INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
            WHERE ver.request_type_id = @typeId;

            -- 4. Delete workflow definitions.
            DELETE wd
            FROM dbo.workflow_definitions wd
            INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
            WHERE ver.request_type_id = @typeId;",
            new { typeId });
    }

    private async Task DeleteVersionsForTypeAsync(int typeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_type_versions WHERE request_type_id = @typeId;",
            new { typeId });
    }

    private async Task DeleteTypeAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_types WHERE id = @id;", new { id });
    }

    private async Task ForceVersionStateAsync(int versionId, string stateCode)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE dbo.request_type_versions SET request_state = @stateCode WHERE id = @versionId;",
            new { versionId, stateCode });
    }

    private async Task<int> InsertTestBlockAsync()
    {
        // Process node_type_id = 2.
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(@"
            INSERT INTO dbo.block_catalog (node_type_id, description, class_name, is_active)
            VALUES (2, @desc, @cn, 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);",
            new
            {
                desc = "_test_block_" + Guid.NewGuid().ToString("N"),
                cn = "VendorSure.Test.NoOpBlock",
            });
    }

    private async Task DeleteTestBlockAsync(int blockId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.block_catalog WHERE id = @blockId;", new { blockId });
    }

    private async Task<int> InsertTestNodeAsync(
        int workflowId, int nodeTypeId, int? blockCatalogId, int? path1, int? path2)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(@"
            INSERT INTO dbo.workflow_nodes
                (workflow_definition_id, node_type_id, block_catalog_id,
                 path1_node_id, path2_node_id)
            VALUES (@workflowId, @nodeTypeId, @blockCatalogId, @path1, @path2);
            SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { workflowId, nodeTypeId, blockCatalogId, path1, path2 });
    }

    private async Task SetStartNodeAsync(int workflowId, int startNodeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE dbo.workflow_definitions SET start_node_id = @startNodeId WHERE id = @workflowId;",
            new { workflowId, startNodeId });
    }

    private async Task<int> NodeCountForWorkflowAsync(int workflowId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.workflow_nodes WHERE workflow_definition_id = @workflowId;",
            new { workflowId });
    }
}
