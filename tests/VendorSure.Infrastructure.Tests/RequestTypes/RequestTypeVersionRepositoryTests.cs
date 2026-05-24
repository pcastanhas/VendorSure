using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.RequestTypes;
using VendorSure.Domain.Workflows;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;
using VendorSure.Services.Workflows;

namespace VendorSure.Infrastructure.Tests.RequestTypes;

/// <summary>
/// Integration tests for the request-type-version repository. Cleanup
/// order matters: nodes before workflows before versions before types
/// (FK chain).
/// </summary>
public sealed class RequestTypeVersionRepositoryTests : IClassFixture<InfrastructureTestFixture>
{
    private readonly IRequestTypeRepository _types;
    private readonly IRequestTypeVersionRepository _versions;
    private readonly IWorkflowDefinitionRepository _workflows;
    private readonly IWorkflowNodeRepository _nodes;
    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeVersionRepositoryTests(InfrastructureTestFixture fixture)
    {
        _types = fixture.ServiceProvider.GetRequiredService<IRequestTypeRepository>();
        _versions = fixture.ServiceProvider.GetRequiredService<IRequestTypeVersionRepository>();
        _workflows = fixture.ServiceProvider.GetRequiredService<IWorkflowDefinitionRepository>();
        _nodes = fixture.ServiceProvider.GetRequiredService<IWorkflowNodeRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task CreateDraftAsync_first_call_creates_version_1()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var result = await _versions.CreateDraftAsync(typeId);
            Assert.Equal(CreateDraftOutcome.Created, result.Outcome);
            Assert.NotNull(result.Id);
            Assert.Equal(1, result.Version);

            var fetched = await _versions.GetByIdAsync(result.Id!.Value);
            Assert.NotNull(fetched);
            Assert.Equal(typeId, fetched!.RequestTypeId);
            Assert.Equal(1, fetched.Version);
            Assert.Equal(RequestState.Draft, fetched.RequestState);
            Assert.NotEqual(default, fetched.CreatedTs);
            Assert.Null(fetched.PlacedInServiceTs);
            Assert.Null(fetched.SupersededTs);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task CreateDraftAsync_subsequent_calls_increment_version()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var first = await _versions.CreateDraftAsync(typeId);
            var second = await _versions.CreateDraftAsync(typeId);
            var third = await _versions.CreateDraftAsync(typeId);

            Assert.Equal(1, first.Version);
            Assert.Equal(2, second.Version);
            Assert.Equal(3, third.Version);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task CreateDraftAsync_returns_RejectedRequestTypeNotFound_for_unknown_type()
    {
        var result = await _versions.CreateDraftAsync(int.MaxValue - 1);
        Assert.Equal(CreateDraftOutcome.RejectedRequestTypeNotFound, result.Outcome);
        Assert.Null(result.Id);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var result = await _versions.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByRequestTypeIdAsync_returns_versions_in_ascending_order()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            await _versions.CreateDraftAsync(typeId);
            await _versions.CreateDraftAsync(typeId);
            await _versions.CreateDraftAsync(typeId);

            var list = await _versions.GetByRequestTypeIdAsync(typeId);
            Assert.Equal(3, list.Count);
            Assert.Equal(new[] { 1, 2, 3 }, list.Select(v => v.Version).ToArray());
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task UpdateAsync_round_trips_editable_fields_on_a_Draft()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var created = await _versions.CreateDraftAsync(typeId);
            var fresh = (await _versions.GetByIdAsync(created.Id!.Value))!;

            var edited = new RequestTypeVersion
            {
                Id = fresh.Id,
                RequestTypeId = fresh.RequestTypeId,        // not edited by UpdateAsync
                Version = fresh.Version,                    // not edited
                Name = "v1 — initial draft",
                RequestState = fresh.RequestState,          // not edited
                WorkflowSelectionPrompt = "Pick the simplest workflow.",
                CreatedTs = fresh.CreatedTs,                // not edited
            };

            var result = await _versions.UpdateAsync(edited);
            Assert.Equal(UpdateRequestTypeVersionResult.Updated, result);

            var fetched = await _versions.GetByIdAsync(fresh.Id);
            Assert.Equal("v1 — initial draft", fetched!.Name);
            Assert.Equal("Pick the simplest workflow.", fetched.WorkflowSelectionPrompt);
            // Version, RequestState, RequestTypeId, CreatedTs unchanged.
            Assert.Equal(fresh.Version, fetched.Version);
            Assert.Equal(fresh.RequestState, fetched.RequestState);
            Assert.Equal(fresh.RequestTypeId, fetched.RequestTypeId);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task UpdateAsync_returns_NotFound_for_unknown_id()
    {
        var ghost = new RequestTypeVersion
        {
            Id = int.MaxValue - 1,
            Name = "_test_ghost",
            WorkflowSelectionPrompt = null,
        };
        var result = await _versions.UpdateAsync(ghost);
        Assert.Equal(UpdateRequestTypeVersionResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_rejects_edits_on_an_InService_version()
    {
        // Immutability rule: even though Chunk 9 owns the official Draft→
        // InService transition path, this chunk should refuse updates on
        // any non-Draft row. Set the state directly via raw SQL to exercise
        // the rule, since the repository intentionally exposes no state-
        // changing API yet.
        var typeId = await CreateTestTypeAsync();
        try
        {
            var created = await _versions.CreateDraftAsync(typeId);
            await ForceVersionStateAsync(created.Id!.Value, RequestStateCodes.InService);

            var edited = new RequestTypeVersion
            {
                Id = created.Id!.Value,
                Name = "I should be refused",
                WorkflowSelectionPrompt = null,
            };
            var result = await _versions.UpdateAsync(edited);
            Assert.Equal(UpdateRequestTypeVersionResult.RejectedNotDraft, result);

            // Row should NOT have been updated.
            var fetched = await _versions.GetByIdAsync(created.Id.Value);
            Assert.NotEqual("I should be refused", fetched!.Name);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task UpdateAsync_rejects_edits_on_a_Superseded_version()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var created = await _versions.CreateDraftAsync(typeId);
            await ForceVersionStateAsync(created.Id!.Value, RequestStateCodes.Superseded);

            var edited = new RequestTypeVersion
            {
                Id = created.Id!.Value,
                Name = "I should also be refused",
            };
            var result = await _versions.UpdateAsync(edited);
            Assert.Equal(UpdateRequestTypeVersionResult.RejectedNotDraft, result);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task RequestState_round_trips_through_the_enum_mapper()
    {
        // Sanity: when we force a row into each of the three states via SQL,
        // the repository reads it back as the correct enum value. Cheap
        // coverage of RequestStateExtensions.FromCode beyond the unit-level
        // 'D'/'I'/'S' cases.
        var typeId = await CreateTestTypeAsync();
        try
        {
            var created = await _versions.CreateDraftAsync(typeId);
            var versionId = created.Id!.Value;

            await ForceVersionStateAsync(versionId, RequestStateCodes.Draft);
            Assert.Equal(RequestState.Draft, (await _versions.GetByIdAsync(versionId))!.RequestState);

            await ForceVersionStateAsync(versionId, RequestStateCodes.InService);
            Assert.Equal(RequestState.InService, (await _versions.GetByIdAsync(versionId))!.RequestState);

            await ForceVersionStateAsync(versionId, RequestStateCodes.Superseded);
            Assert.Equal(RequestState.Superseded, (await _versions.GetByIdAsync(versionId))!.RequestState);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_promotes_draft_with_no_prior_in_service()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var v1 = await _versions.CreateDraftAsync(typeId);
            var versionId = v1.Id!.Value;

            var result = await _versions.TransitionToInServiceAsync(versionId);
            Assert.Equal(TransitionToInServiceResult.Succeeded, result);

            var fetched = await _versions.GetByIdAsync(versionId);
            Assert.NotNull(fetched);
            Assert.Equal(RequestState.InService, fetched!.RequestState);
            Assert.NotNull(fetched.PlacedInServiceTs);
            Assert.Null(fetched.SupersededTs);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_supersedes_prior_in_service_atomically()
    {
        // The headline test. v1 is forced In Service, then v2 is created
        // and promoted via TransitionToInServiceAsync. After the call:
        //   - v2 is In Service with placed_in_service_ts set
        //   - v1 is Superseded with superseded_ts set
        //   - the timestamps are equal (single value captured in C# and
        //     passed to both UPDATEs).
        var typeId = await CreateTestTypeAsync();
        try
        {
            var v1 = await _versions.CreateDraftAsync(typeId);
            var v1Id = v1.Id!.Value;
            await ForceVersionStateAsync(v1Id, RequestStateCodes.InService);

            var v2 = await _versions.CreateDraftAsync(typeId);
            var v2Id = v2.Id!.Value;

            var result = await _versions.TransitionToInServiceAsync(v2Id);
            Assert.Equal(TransitionToInServiceResult.Succeeded, result);

            var v2Fetched = await _versions.GetByIdAsync(v2Id);
            var v1Fetched = await _versions.GetByIdAsync(v1Id);

            Assert.Equal(RequestState.InService, v2Fetched!.RequestState);
            Assert.NotNull(v2Fetched.PlacedInServiceTs);
            Assert.Null(v2Fetched.SupersededTs);

            Assert.Equal(RequestState.Superseded, v1Fetched!.RequestState);
            Assert.NotNull(v1Fetched.SupersededTs);

            // The two timestamps should be identical (same DateTime value
            // captured once and used for both UPDATEs). Allow a tiny
            // millisecond fudge for any rounding at the SQL Server side
            // — datetime2 precision is fine but Dapper round-tripping
            // through DateTime can lose sub-tick precision.
            var delta = (v2Fetched.PlacedInServiceTs!.Value - v1Fetched.SupersededTs!.Value).Duration();
            Assert.True(delta < TimeSpan.FromMilliseconds(1),
                $"Expected matching transition timestamps; got delta={delta}.");
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _versions.TransitionToInServiceAsync(int.MaxValue - 1);
        Assert.Equal(TransitionToInServiceResult.NotFound, result);
    }

    [Fact]
    public async Task TransitionToInServiceAsync_returns_RejectedNotDraft_for_InService_target()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var v1 = await _versions.CreateDraftAsync(typeId);
            await ForceVersionStateAsync(v1.Id!.Value, RequestStateCodes.InService);

            var result = await _versions.TransitionToInServiceAsync(v1.Id.Value);
            Assert.Equal(TransitionToInServiceResult.RejectedNotDraft, result);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_returns_RejectedNotDraft_for_Superseded_target()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var v1 = await _versions.CreateDraftAsync(typeId);
            await ForceVersionStateAsync(v1.Id!.Value, RequestStateCodes.Superseded);

            var result = await _versions.TransitionToInServiceAsync(v1.Id.Value);
            Assert.Equal(TransitionToInServiceResult.RejectedNotDraft, result);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_does_not_touch_other_types()
    {
        // Two unrelated types each with an In Service version. Promoting
        // v2 of type A must NOT supersede the In Service version of type
        // B. (The demote SQL filters on request_type_id; this test
        // guards against a regression where the filter is dropped.)
        var typeAId = await CreateTestTypeAsync();
        var typeBId = await CreateTestTypeAsync();
        try
        {
            // Type A: v1 InService, v2 Draft (target).
            var a1 = await _versions.CreateDraftAsync(typeAId);
            await ForceVersionStateAsync(a1.Id!.Value, RequestStateCodes.InService);
            var a2 = await _versions.CreateDraftAsync(typeAId);

            // Type B: v1 InService, untouched.
            var b1 = await _versions.CreateDraftAsync(typeBId);
            await ForceVersionStateAsync(b1.Id!.Value, RequestStateCodes.InService);

            var result = await _versions.TransitionToInServiceAsync(a2.Id!.Value);
            Assert.Equal(TransitionToInServiceResult.Succeeded, result);

            // Type B's InService should be untouched.
            var b1After = await _versions.GetByIdAsync(b1.Id.Value);
            Assert.Equal(RequestState.InService, b1After!.RequestState);
            Assert.Null(b1After.SupersededTs);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeAId);
            await DeleteVersionsForTypeAsync(typeBId);
            await DeleteTypeAsync(typeAId);
            await DeleteTypeAsync(typeBId);
        }
    }

    // ==== Promotion-time validation (Phase 5 / Chunk 10) ===================

    [Fact]
    public async Task TransitionToInServiceAsync_promotes_when_all_workflows_are_complete()
    {
        // Sanity: a fully-wired workflow (Start -> Process -> Approved) on
        // the Draft promotes successfully. The validator returns zero
        // issues and the transition proceeds.
        var typeId = await CreateTestTypeAsync();
        var blockId = await InsertTestBlockAsync();
        try
        {
            var draft = await _versions.CreateDraftAsync(typeId);
            var versionId = draft.Id!.Value;
            var wfId = (await _workflows.CreateAsync(
                versionId, "wf-complete", null)).Id!.Value;
            // Workflow auto-Start is at level 1; build Start -> Process -> Approved.
            var workflow = await _workflows.GetByIdAsync(wfId);
            var startId = workflow!.StartNodeId!.Value;
            var processResult = await _nodes.InsertChildAsync(new InsertChildRequest(
                startId, 1, WorkflowNodeTypeIds.Process, blockId));
            await _nodes.InsertChildAsync(new InsertChildRequest(
                processResult.Id!.Value, 1, WorkflowNodeTypeIds.Approved, null));

            var result = await _versions.TransitionToInServiceAsync(versionId);
            Assert.Equal(TransitionToInServiceOutcome.Succeeded, result.Outcome);
            Assert.Empty(result.Issues);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
            await DeleteTestBlockAsync(blockId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_rejects_when_Decision_missing_path2()
    {
        // Start -> Decision (path1 = Approved, path2 = null). Decision is
        // incomplete: path2 must be set before promotion.
        var typeId = await CreateTestTypeAsync();
        var blockId = await InsertTestBlockAsync();
        try
        {
            var draft = await _versions.CreateDraftAsync(typeId);
            var versionId = draft.Id!.Value;
            var wfId = (await _workflows.CreateAsync(
                versionId, "wf-incomplete-decision", null)).Id!.Value;
            var workflow = await _workflows.GetByIdAsync(wfId);
            var startId = workflow!.StartNodeId!.Value;
            var decisionResult = await _nodes.InsertChildAsync(new InsertChildRequest(
                startId, 1, WorkflowNodeTypeIds.Decision, blockId));
            await _nodes.InsertChildAsync(new InsertChildRequest(
                decisionResult.Id!.Value, 1, WorkflowNodeTypeIds.Approved, null));
            // No path2 — that's what we're testing.

            var result = await _versions.TransitionToInServiceAsync(versionId);
            Assert.Equal(TransitionToInServiceOutcome.RejectedIncompleteWorkflow, result.Outcome);
            Assert.Single(result.Issues);
            var issue = result.Issues[0];
            Assert.Equal(WorkflowIssueKind.DecisionMissingPath2, issue.Kind);
            Assert.Equal(decisionResult.Id, issue.NodeId);
            Assert.Equal("wf-incomplete-decision", issue.WorkflowName);

            // Version is still Draft — transition rolled back.
            var fetched = await _versions.GetByIdAsync(versionId);
            Assert.Equal(RequestState.Draft, fetched!.RequestState);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
            await DeleteTestBlockAsync(blockId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_rejects_when_Process_missing_path1()
    {
        // Start -> Process (no child). Process is incomplete.
        var typeId = await CreateTestTypeAsync();
        var blockId = await InsertTestBlockAsync();
        try
        {
            var draft = await _versions.CreateDraftAsync(typeId);
            var versionId = draft.Id!.Value;
            var wfId = (await _workflows.CreateAsync(
                versionId, "wf-incomplete-process", null)).Id!.Value;
            var workflow = await _workflows.GetByIdAsync(wfId);
            var startId = workflow!.StartNodeId!.Value;
            var processResult = await _nodes.InsertChildAsync(new InsertChildRequest(
                startId, 1, WorkflowNodeTypeIds.Process, blockId));

            var result = await _versions.TransitionToInServiceAsync(versionId);
            Assert.Equal(TransitionToInServiceOutcome.RejectedIncompleteWorkflow, result.Outcome);
            Assert.Single(result.Issues);
            Assert.Equal(WorkflowIssueKind.MissingPath1, result.Issues[0].Kind);
            Assert.Equal(processResult.Id, result.Issues[0].NodeId);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
            await DeleteTestBlockAsync(blockId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_rejects_when_Start_has_no_child()
    {
        // Brand-new workflow: Start exists but path1 is null. The
        // auto-Start from Chunk 7 lands path1=null until the user wires
        // something. Promotion should refuse.
        var typeId = await CreateTestTypeAsync();
        try
        {
            var draft = await _versions.CreateDraftAsync(typeId);
            var versionId = draft.Id!.Value;
            await _workflows.CreateAsync(versionId, "wf-empty", null);

            var result = await _versions.TransitionToInServiceAsync(versionId);
            Assert.Equal(TransitionToInServiceOutcome.RejectedIncompleteWorkflow, result.Outcome);
            Assert.Single(result.Issues);
            Assert.Equal(WorkflowIssueKind.MissingPath1, result.Issues[0].Kind);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_rejects_when_orphan_node_present()
    {
        // Build a complete Start -> Approved chain, then raw-insert an
        // orphan node (no parent points at it). Validation should flag it.
        var typeId = await CreateTestTypeAsync();
        try
        {
            var draft = await _versions.CreateDraftAsync(typeId);
            var versionId = draft.Id!.Value;
            var wfId = (await _workflows.CreateAsync(
                versionId, "wf-with-orphan", null)).Id!.Value;
            var workflow = await _workflows.GetByIdAsync(wfId);
            var startId = workflow!.StartNodeId!.Value;
            await _nodes.InsertChildAsync(new InsertChildRequest(
                startId, 1, WorkflowNodeTypeIds.Approved, null));

            // Raw insert an orphan terminal (simulating a stale/regressed row).
            int orphanId = await InsertRawOrphanAsync(wfId, WorkflowNodeTypeIds.Rejected);

            var result = await _versions.TransitionToInServiceAsync(versionId);
            Assert.Equal(TransitionToInServiceOutcome.RejectedIncompleteWorkflow, result.Outcome);
            Assert.Single(result.Issues);
            Assert.Equal(WorkflowIssueKind.OrphanNode, result.Issues[0].Kind);
            Assert.Equal(orphanId, result.Issues[0].NodeId);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_reports_issues_across_multiple_workflows()
    {
        // Two workflows on the same version, each with a different issue.
        // The validator should return both, ordered by name.
        var typeId = await CreateTestTypeAsync();
        var blockId = await InsertTestBlockAsync();
        try
        {
            var draft = await _versions.CreateDraftAsync(typeId);
            var versionId = draft.Id!.Value;

            // Workflow A: Process with no child.
            var wfA = (await _workflows.CreateAsync(versionId, "A-incomplete", null)).Id!.Value;
            var wfAStart = (await _workflows.GetByIdAsync(wfA))!.StartNodeId!.Value;
            await _nodes.InsertChildAsync(new InsertChildRequest(
                wfAStart, 1, WorkflowNodeTypeIds.Process, blockId));

            // Workflow B: Decision missing both branches.
            var wfB = (await _workflows.CreateAsync(versionId, "B-incomplete", null)).Id!.Value;
            var wfBStart = (await _workflows.GetByIdAsync(wfB))!.StartNodeId!.Value;
            await _nodes.InsertChildAsync(new InsertChildRequest(
                wfBStart, 1, WorkflowNodeTypeIds.Decision, blockId));

            var result = await _versions.TransitionToInServiceAsync(versionId);
            Assert.Equal(TransitionToInServiceOutcome.RejectedIncompleteWorkflow, result.Outcome);

            // Workflow A: one Process missing path1. Workflow B: one
            // Decision missing path1 and one Decision missing path2.
            // Total = 3 issues.
            Assert.Equal(3, result.Issues.Count);
            Assert.Contains(result.Issues, i =>
                i.WorkflowName == "A-incomplete" && i.Kind == WorkflowIssueKind.MissingPath1);
            Assert.Contains(result.Issues, i =>
                i.WorkflowName == "B-incomplete" && i.Kind == WorkflowIssueKind.DecisionMissingPath1);
            Assert.Contains(result.Issues, i =>
                i.WorkflowName == "B-incomplete" && i.Kind == WorkflowIssueKind.DecisionMissingPath2);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
            await DeleteTestBlockAsync(blockId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_succeeds_for_Draft_with_no_workflows()
    {
        // A version with no workflows at all has nothing to validate.
        // Promotion succeeds. This matches the pre-Chunk-10 behavior of
        // the existing happy-path tests, which never created workflows.
        var typeId = await CreateTestTypeAsync();
        try
        {
            var draft = await _versions.CreateDraftAsync(typeId);
            var result = await _versions.TransitionToInServiceAsync(draft.Id!.Value);
            Assert.Equal(TransitionToInServiceOutcome.Succeeded, result.Outcome);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    // ---- helpers --------------------------------------------------------

    private async Task<int> CreateTestTypeAsync()
    {
        var result = await _types.CreateAsync(new RequestType
        {
            Name = $"_test_rt_{Guid.NewGuid():N}",
            IsExplanationRequired = false,
            IsActive = true,
        });
        return result.Id!.Value;
    }

    private async Task ForceVersionStateAsync(int versionId, string stateCode)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE dbo.request_type_versions SET request_state = @stateCode WHERE id = @versionId;",
            new { versionId, stateCode });
    }

    private async Task DeleteVersionsForTypeAsync(int typeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        // FK chain: workflow_nodes → workflow_definitions → request_type_versions.
        // No ON DELETE CASCADE on the chain, so delete in the right order.
        // First null all path FKs inside the workflows (so node deletes
        // don't violate FK_workflow_nodes_path1/path2), then null
        // start_node_id (FK to workflow_nodes), then delete nodes, then
        // workflows, then versions.
        await connection.ExecuteAsync(@"
            DECLARE @ids TABLE (id int PRIMARY KEY);
            INSERT INTO @ids (id)
            SELECT n.id FROM dbo.workflow_nodes n
            INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
            INNER JOIN dbo.request_type_versions v ON v.id = wd.request_type_version_id
            WHERE v.request_type_id = @typeId;

            UPDATE dbo.workflow_definitions
            SET start_node_id = NULL
            WHERE request_type_version_id IN (
                SELECT id FROM dbo.request_type_versions WHERE request_type_id = @typeId
            );

            UPDATE n SET path1_node_id = NULL, path2_node_id = NULL
            FROM dbo.workflow_nodes n INNER JOIN @ids i ON i.id = n.id;

            DELETE n FROM dbo.workflow_nodes n INNER JOIN @ids i ON i.id = n.id;

            DELETE FROM dbo.workflow_definitions
            WHERE request_type_version_id IN (
                SELECT id FROM dbo.request_type_versions WHERE request_type_id = @typeId
            );

            DELETE FROM dbo.request_type_versions WHERE request_type_id = @typeId;",
            new { typeId });
    }

    private async Task DeleteTypeAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_types WHERE id = @id;",
            new { id });
    }

    private async Task<int> InsertTestBlockAsync()
    {
        // Seed a Process-typed block_catalog row so InsertChildAsync can
        // refer to it. Tests that need a Decision block can call this
        // and override the node_type_id, but for promotion-time
        // validation a Process block is enough — Decisions only need it
        // to satisfy CK_workflow_nodes_block_by_type at insert time.
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        return await c.QuerySingleAsync<int>(@"
            INSERT INTO dbo.block_catalog (node_type_id, name, description, class_name, is_active, actor_type)
            VALUES (2, @name, @desc, 'VendorSure.Test.NoOpBlock', 1, 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);",
            new
            {
                name = "_test_blk_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                desc = "_test_block_" + Guid.NewGuid().ToString("N"),
            });
    }

    private async Task DeleteTestBlockAsync(int blockId)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM dbo.block_catalog WHERE id = @blockId;", new { blockId });
    }

    /// <summary>
    /// Raw-inserts an orphan terminal node into the workflow — no
    /// parent references it. Used by the orphan-detection test to
    /// simulate stale/regressed data that the repo APIs wouldn't
    /// normally produce.
    /// </summary>
    private async Task<int> InsertRawOrphanAsync(int workflowId, int terminalTypeId)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        return await c.QuerySingleAsync<int>(@"
            INSERT INTO dbo.workflow_nodes
                (workflow_definition_id, node_type_id, execution_level,
                 path1_node_id, path2_node_id, block_catalog_id)
            VALUES (@workflowId, @nodeTypeId, 99, NULL, NULL, NULL);
            SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { workflowId, nodeTypeId = terminalTypeId });
    }
}
