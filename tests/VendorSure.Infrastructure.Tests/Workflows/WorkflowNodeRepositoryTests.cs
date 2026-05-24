using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.RequestTypes;
using VendorSure.Domain.Workflows;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;
using VendorSure.Services.Workflows;

namespace VendorSure.Infrastructure.Tests.Workflows;

/// <summary>
/// Integration tests for the workflow-node repository. Each test creates
/// its own RequestType + Draft Version + Workflow + a block_catalog row
/// for any Process/Decision nodes, and tears down in FK-aware order.
/// </summary>
public sealed class WorkflowNodeRepositoryTests
    : IClassFixture<InfrastructureTestFixture>
{
    private readonly IWorkflowNodeRepository _nodes;
    private readonly IWorkflowDefinitionRepository _workflows;
    private readonly IRequestTypeRepository _types;
    private readonly IDbConnectionFactory _connectionFactory;

    public WorkflowNodeRepositoryTests(InfrastructureTestFixture fixture)
    {
        _nodes = fixture.ServiceProvider.GetRequiredService<IWorkflowNodeRepository>();
        _workflows = fixture.ServiceProvider.GetRequiredService<IWorkflowDefinitionRepository>();
        _types = fixture.ServiceProvider.GetRequiredService<IRequestTypeRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    // ==== CreateAsync ====================================================

    [Fact]
    public async Task CreateAsync_creates_Start_node_with_level_0_and_null_paths()
    {
        await using var f = await SetupAsync();

        var result = await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = f.WorkflowId,
            NodeTypeId = WorkflowNodeTypeIds.Start,
        });

        Assert.Equal(CreateNodeOutcome.Created, result.Outcome);
        Assert.NotNull(result.Id);

        var fetched = await _nodes.GetByIdAsync(result.Id!.Value);
        Assert.NotNull(fetched);
        Assert.Equal(WorkflowNodeTypeIds.Start, fetched!.NodeTypeId);
        Assert.Null(fetched.BlockCatalogId);
        Assert.Equal(0, fetched.ExecutionLevel);   // unwired by default
        Assert.Null(fetched.Path1NodeId);
        Assert.Null(fetched.Path2NodeId);
    }

    [Fact]
    public async Task CreateAsync_creates_Process_node_with_block_catalog_id()
    {
        await using var f = await SetupAsync();

        var result = await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = f.WorkflowId,
            NodeTypeId = WorkflowNodeTypeIds.Process,
            BlockCatalogId = f.BlockId,
            Notes = "_test_notes",
        });

        Assert.Equal(CreateNodeOutcome.Created, result.Outcome);
        var fetched = await _nodes.GetByIdAsync(result.Id!.Value);
        Assert.Equal(f.BlockId, fetched!.BlockCatalogId);
        Assert.Equal("_test_notes", fetched.Notes);
    }

    [Fact]
    public async Task CreateAsync_ignores_caller_supplied_execution_level_and_paths()
    {
        // Even if the caller passes ExecutionLevel = 7 and Path1NodeId = something,
        // the repo forces 0 / NULL on insert. Wiring is a separate operation.
        await using var f = await SetupAsync();

        var result = await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = f.WorkflowId,
            NodeTypeId = WorkflowNodeTypeIds.Start,
            ExecutionLevel = 7,
            Path1NodeId = 999,
            Path2NodeId = 999,
        });

        Assert.Equal(CreateNodeOutcome.Created, result.Outcome);
        var fetched = await _nodes.GetByIdAsync(result.Id!.Value);
        Assert.Equal(0, fetched!.ExecutionLevel);
        Assert.Null(fetched.Path1NodeId);
        Assert.Null(fetched.Path2NodeId);
    }

    [Fact]
    public async Task CreateAsync_returns_RejectedWorkflowNotFound_for_unknown_workflow()
    {
        var result = await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = int.MaxValue - 1,
            NodeTypeId = WorkflowNodeTypeIds.Start,
        });
        Assert.Equal(CreateNodeOutcome.RejectedWorkflowNotFound, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_returns_RejectedNotDraft_for_InService_version()
    {
        await using var f = await SetupAsync();
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = f.WorkflowId,
            NodeTypeId = WorkflowNodeTypeIds.Start,
        });
        Assert.Equal(CreateNodeOutcome.RejectedNotDraft, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_returns_RejectedInvalidShape_for_block_on_terminal()
    {
        await using var f = await SetupAsync();

        var result = await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = f.WorkflowId,
            NodeTypeId = WorkflowNodeTypeIds.Approved,
            BlockCatalogId = f.BlockId,   // not allowed for terminals
        });
        Assert.Equal(CreateNodeOutcome.RejectedInvalidShape, result.Outcome);
    }

    [Fact]
    public async Task CreateAsync_returns_RejectedInvalidShape_for_missing_block_on_process()
    {
        await using var f = await SetupAsync();

        var result = await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = f.WorkflowId,
            NodeTypeId = WorkflowNodeTypeIds.Process,
            BlockCatalogId = null,   // required for Process
        });
        Assert.Equal(CreateNodeOutcome.RejectedInvalidShape, result.Outcome);
    }

    // ==== ListByWorkflowIdAsync ===========================================

    [Fact]
    public async Task ListByWorkflowIdAsync_orders_by_level_then_id()
    {
        await using var f = await SetupAsync();

        // Three Start nodes (level 0, three different ids). Listing should
        // return them id-ascending since they all share level 0.
        var a = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var b = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var c = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;

        // Bump one to a different level via raw SQL to test the primary
        // sort.
        await SetLevelAsync(b, level: 3);

        var list = await _nodes.ListByWorkflowIdAsync(f.WorkflowId);
        Assert.Equal(3, list.Count);
        // a (level 0, smallest id), c (level 0), b (level 3)
        Assert.Equal(a, list[0].Id);
        Assert.Equal(c, list[1].Id);
        Assert.Equal(b, list[2].Id);
    }

    [Fact]
    public async Task ListByWorkflowIdAsync_returns_empty_for_workflow_with_no_nodes()
    {
        await using var f = await SetupAsync();
        var list = await _nodes.ListByWorkflowIdAsync(f.WorkflowId);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var node = await _nodes.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(node);
    }

    // ==== UpdateAsync =====================================================

    [Fact]
    public async Task UpdateAsync_edits_property_fields()
    {
        await using var f = await SetupAsync();
        var created = (await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = f.WorkflowId,
            NodeTypeId = WorkflowNodeTypeIds.Process,
            BlockCatalogId = f.BlockId,
            Notes = "orig",
        })).Id!.Value;

        var result = await _nodes.UpdateAsync(new WorkflowNode
        {
            Id = created,
            Notes = "edited",
            PromptText = "Why?",
            StaleThresholdDays = 7,
            StaleMessageText = "_test_stale_msg",
        });
        Assert.Equal(UpdateNodeResult.Updated, result);

        var fetched = await _nodes.GetByIdAsync(created);
        Assert.Equal("edited", fetched!.Notes);
        Assert.Equal("Why?", fetched.PromptText);
        Assert.Equal(7, fetched.StaleThresholdDays);
        Assert.Equal("_test_stale_msg", fetched.StaleMessageText);
    }

    [Fact]
    public async Task UpdateAsync_does_not_touch_node_type_block_or_paths()
    {
        // Even if the caller passes a wildly-different NodeTypeId or
        // BlockCatalogId, the repo doesn't touch those. (The SQL doesn't
        // SET them, so they round-trip unchanged regardless.)
        await using var f = await SetupAsync();
        var created = (await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = f.WorkflowId,
            NodeTypeId = WorkflowNodeTypeIds.Process,
            BlockCatalogId = f.BlockId,
        })).Id!.Value;

        // Pre-bump path1 via raw SQL to make sure we can detect the
        // no-change behavior.
        var sibling = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        await SetPath1RawAsync(created, sibling);

        await _nodes.UpdateAsync(new WorkflowNode
        {
            Id = created,
            // Caller tries to change type and block — must be ignored.
            NodeTypeId = WorkflowNodeTypeIds.Decision,
            BlockCatalogId = null,
            // Caller tries to change paths — must be ignored.
            Path1NodeId = null,
            Path2NodeId = sibling,
        });

        var fetched = await _nodes.GetByIdAsync(created);
        Assert.Equal(WorkflowNodeTypeIds.Process, fetched!.NodeTypeId);
        Assert.Equal(f.BlockId, fetched.BlockCatalogId);
        Assert.Equal(sibling, fetched.Path1NodeId);
        Assert.Null(fetched.Path2NodeId);
    }

    [Fact]
    public async Task UpdateAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _nodes.UpdateAsync(new WorkflowNode { Id = int.MaxValue - 1 });
        Assert.Equal(UpdateNodeResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_returns_RejectedNotDraft_when_parent_is_not_Draft()
    {
        await using var f = await SetupAsync();
        var created = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _nodes.UpdateAsync(new WorkflowNode { Id = created, Notes = "x" });
        Assert.Equal(UpdateNodeResult.RejectedNotDraft, result);
    }

    // ==== SetPath1Async / SetPath2Async ==================================

    [Fact]
    public async Task SetPath1Async_wires_edge_and_renumbers_target()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var process = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;
        await SetLevelAsync(start, 1);    // Start is at level 1 (designer puts it there via SetStartNode)
        // process is currently at level 0 (unwired).

        var result = await _nodes.SetPath1Async(start, process);
        Assert.Equal(SetPathOutcome.Updated, result);

        var startAfter = await _nodes.GetByIdAsync(start);
        var processAfter = await _nodes.GetByIdAsync(process);
        Assert.Equal(process, startAfter!.Path1NodeId);
        Assert.Equal(2, processAfter!.ExecutionLevel);  // 1 + 1
    }

    [Fact]
    public async Task SetPath1Async_renumbers_entire_subtree()
    {
        // Build a chain disconnected from start: A → B → C, with A, B, C
        // all unwired (level 0 since SetPath assigns target's level = source+1
        // but source = unwired (level 0) so child becomes level 1, etc).
        // Then wire Start → A and verify the whole chain shifts up by Start's level.
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var a = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;
        var b = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;
        var c = (await _nodes.CreateAsync(TerminalNode(f.WorkflowId, WorkflowNodeTypeIds.Approved))).Id!.Value;

        // Pre-wire the chain A → B → C with A as source-level 0.
        // SetPath1(A, B) sets B's level to 1; SetPath1(B, C) sets C's to 2.
        await _nodes.SetPath1Async(a, b);
        await _nodes.SetPath1Async(b, c);

        Assert.Equal(0, (await _nodes.GetByIdAsync(a))!.ExecutionLevel);
        Assert.Equal(1, (await _nodes.GetByIdAsync(b))!.ExecutionLevel);
        Assert.Equal(2, (await _nodes.GetByIdAsync(c))!.ExecutionLevel);

        // Now place start at level 5 (artificially) and wire start → A.
        // After: A should be level 6, B level 7, C level 8.
        await SetLevelAsync(start, 5);
        var result = await _nodes.SetPath1Async(start, a);
        Assert.Equal(SetPathOutcome.Updated, result);

        Assert.Equal(6, (await _nodes.GetByIdAsync(a))!.ExecutionLevel);
        Assert.Equal(7, (await _nodes.GetByIdAsync(b))!.ExecutionLevel);
        Assert.Equal(8, (await _nodes.GetByIdAsync(c))!.ExecutionLevel);
    }

    [Fact]
    public async Task SetPath1Async_clears_edge_when_target_is_null()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var process = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;
        await _nodes.SetPath1Async(start, process);

        var result = await _nodes.SetPath1Async(start, null);
        Assert.Equal(SetPathOutcome.Updated, result);

        var fetched = await _nodes.GetByIdAsync(start);
        Assert.Null(fetched!.Path1NodeId);
    }

    [Fact]
    public async Task SetPath1Async_returns_RejectedSelfLoop_when_source_equals_target()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;

        var result = await _nodes.SetPath1Async(start, start);
        Assert.Equal(SetPathOutcome.RejectedSelfLoop, result);
    }

    [Fact]
    public async Task SetPath1Async_returns_NotFound_for_unknown_source()
    {
        var result = await _nodes.SetPath1Async(int.MaxValue - 1, null);
        Assert.Equal(SetPathOutcome.NotFound, result);
    }

    [Fact]
    public async Task SetPath1Async_returns_RejectedTargetNotFound_for_unknown_target()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;

        var result = await _nodes.SetPath1Async(start, int.MaxValue - 1);
        Assert.Equal(SetPathOutcome.RejectedTargetNotFound, result);
    }

    [Fact]
    public async Task SetPath1Async_returns_RejectedTargetNotInWorkflow_for_cross_workflow_target()
    {
        await using var fA = await SetupAsync();
        await using var fB = await SetupAsync();

        var sourceInA = (await _nodes.CreateAsync(StartNode(fA.WorkflowId))).Id!.Value;
        var targetInB = (await _nodes.CreateAsync(ProcessNode(fB.WorkflowId, fB.BlockId))).Id!.Value;

        var result = await _nodes.SetPath1Async(sourceInA, targetInB);
        Assert.Equal(SetPathOutcome.RejectedTargetNotInWorkflow, result);
    }

    [Fact]
    public async Task SetPath1Async_returns_RejectedTargetAlreadyHasParent_for_no_merging_violation()
    {
        // Two source nodes both trying to point at the same target.
        await using var f = await SetupAsync();
        var sourceA = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var sourceB = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var target = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;

        await _nodes.SetPath1Async(sourceA, target);
        var result = await _nodes.SetPath1Async(sourceB, target);
        Assert.Equal(SetPathOutcome.RejectedTargetAlreadyHasParent, result);
    }

    [Fact]
    public async Task SetPath2Async_returns_RejectedShape_for_non_decision_source()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var process = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;

        var result = await _nodes.SetPath2Async(start, process);
        Assert.Equal(SetPathOutcome.RejectedShape, result);
    }

    [Fact]
    public async Task SetPath2Async_works_on_decision_node()
    {
        await using var f = await SetupAsync();
        var decision = (await _nodes.CreateAsync(new WorkflowNode
        {
            WorkflowDefinitionId = f.WorkflowId,
            NodeTypeId = WorkflowNodeTypeIds.Decision,
            BlockCatalogId = f.BlockId,
        })).Id!.Value;
        var yesTarget = (await _nodes.CreateAsync(TerminalNode(f.WorkflowId, WorkflowNodeTypeIds.Approved))).Id!.Value;
        var noTarget = (await _nodes.CreateAsync(TerminalNode(f.WorkflowId, WorkflowNodeTypeIds.Rejected))).Id!.Value;

        Assert.Equal(SetPathOutcome.Updated, await _nodes.SetPath1Async(decision, yesTarget));
        Assert.Equal(SetPathOutcome.Updated, await _nodes.SetPath2Async(decision, noTarget));

        var fetched = await _nodes.GetByIdAsync(decision);
        Assert.Equal(yesTarget, fetched!.Path1NodeId);
        Assert.Equal(noTarget, fetched.Path2NodeId);
    }

    [Fact]
    public async Task SetPath1Async_returns_RejectedNotDraft_when_parent_is_not_Draft()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var process = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _nodes.SetPath1Async(start, process);
        Assert.Equal(SetPathOutcome.RejectedNotDraft, result);
    }

    // ==== DeleteAsync =====================================================

    [Fact]
    public async Task DeleteAsync_removes_node_and_nulls_upstream_path_fks()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var middle = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;
        var terminal = (await _nodes.CreateAsync(TerminalNode(f.WorkflowId, WorkflowNodeTypeIds.Approved))).Id!.Value;

        await _nodes.SetPath1Async(start, middle);
        await _nodes.SetPath1Async(middle, terminal);

        // Delete the middle node. Upstream (start) should have path1 nulled.
        // Downstream (terminal) keeps its level (stale-but-fine per the
        // dumb-canvas posture); it just becomes an orphan.
        var result = await _nodes.DeleteAsync(middle);
        Assert.Equal(DeleteNodeResult.Deleted, result);

        Assert.Null(await _nodes.GetByIdAsync(middle));
        var startAfter = await _nodes.GetByIdAsync(start);
        Assert.Null(startAfter!.Path1NodeId);

        // Terminal still exists, level unchanged.
        var terminalAfter = await _nodes.GetByIdAsync(terminal);
        Assert.NotNull(terminalAfter);
        Assert.Equal(2, terminalAfter!.ExecutionLevel);
    }

    [Fact]
    public async Task DeleteAsync_nulls_start_node_id_on_workflow_when_deleting_the_start()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        await _nodes.SetStartNodeAsync(f.WorkflowId, start);

        // workflow.start_node_id is now `start`.
        var workflowBefore = await _workflows.GetByIdAsync(f.WorkflowId);
        Assert.Equal(start, workflowBefore!.StartNodeId);

        var result = await _nodes.DeleteAsync(start);
        Assert.Equal(DeleteNodeResult.Deleted, result);

        var workflowAfter = await _workflows.GetByIdAsync(f.WorkflowId);
        Assert.Null(workflowAfter!.StartNodeId);
    }

    [Fact]
    public async Task DeleteAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _nodes.DeleteAsync(int.MaxValue - 1);
        Assert.Equal(DeleteNodeResult.NotFound, result);
    }

    [Fact]
    public async Task DeleteAsync_returns_RejectedNotDraft_when_parent_is_not_Draft()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Superseded);

        var result = await _nodes.DeleteAsync(start);
        Assert.Equal(DeleteNodeResult.RejectedNotDraft, result);

        // Restore Draft and confirm the node row still exists.
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Draft);
        Assert.NotNull(await _nodes.GetByIdAsync(start));
    }

    // ==== SetStartNodeAsync ===============================================

    [Fact]
    public async Task SetStartNodeAsync_sets_workflow_start_and_renumbers_to_level_1()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        // Start is currently level 0 (unwired).

        var result = await _nodes.SetStartNodeAsync(f.WorkflowId, start);
        Assert.Equal(SetStartNodeOutcome.Updated, result);

        var workflowAfter = await _workflows.GetByIdAsync(f.WorkflowId);
        Assert.Equal(start, workflowAfter!.StartNodeId);

        var startAfter = await _nodes.GetByIdAsync(start);
        Assert.Equal(1, startAfter!.ExecutionLevel);
    }

    [Fact]
    public async Task SetStartNodeAsync_renumbers_subtree_from_start()
    {
        // Build Start → Process → Approved with the chain wired but
        // SetStartNodeAsync not yet called. After SetStart, all three
        // should be at levels 1, 2, 3.
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        var process = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;
        var approved = (await _nodes.CreateAsync(TerminalNode(f.WorkflowId, WorkflowNodeTypeIds.Approved))).Id!.Value;

        // Wire chain. Start is at level 0, so process ends up at level 1,
        // and approved at level 2.
        await _nodes.SetPath1Async(start, process);
        await _nodes.SetPath1Async(process, approved);

        Assert.Equal(0, (await _nodes.GetByIdAsync(start))!.ExecutionLevel);
        Assert.Equal(1, (await _nodes.GetByIdAsync(process))!.ExecutionLevel);
        Assert.Equal(2, (await _nodes.GetByIdAsync(approved))!.ExecutionLevel);

        // Designate Start. Everything shifts up by 1.
        var result = await _nodes.SetStartNodeAsync(f.WorkflowId, start);
        Assert.Equal(SetStartNodeOutcome.Updated, result);

        Assert.Equal(1, (await _nodes.GetByIdAsync(start))!.ExecutionLevel);
        Assert.Equal(2, (await _nodes.GetByIdAsync(process))!.ExecutionLevel);
        Assert.Equal(3, (await _nodes.GetByIdAsync(approved))!.ExecutionLevel);
    }

    [Fact]
    public async Task SetStartNodeAsync_clears_start_when_node_is_null()
    {
        await using var f = await SetupAsync();
        var start = (await _nodes.CreateAsync(StartNode(f.WorkflowId))).Id!.Value;
        await _nodes.SetStartNodeAsync(f.WorkflowId, start);

        var result = await _nodes.SetStartNodeAsync(f.WorkflowId, null);
        Assert.Equal(SetStartNodeOutcome.Updated, result);

        var workflow = await _workflows.GetByIdAsync(f.WorkflowId);
        Assert.Null(workflow!.StartNodeId);
    }

    [Fact]
    public async Task SetStartNodeAsync_returns_RejectedWorkflowNotFound_for_unknown_workflow()
    {
        var result = await _nodes.SetStartNodeAsync(int.MaxValue - 1, null);
        Assert.Equal(SetStartNodeOutcome.RejectedWorkflowNotFound, result);
    }

    [Fact]
    public async Task SetStartNodeAsync_returns_RejectedNotDraft_when_parent_is_not_Draft()
    {
        await using var f = await SetupAsync();
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _nodes.SetStartNodeAsync(f.WorkflowId, null);
        Assert.Equal(SetStartNodeOutcome.RejectedNotDraft, result);
    }

    [Fact]
    public async Task SetStartNodeAsync_returns_RejectedNodeNotFound_for_unknown_node()
    {
        await using var f = await SetupAsync();
        var result = await _nodes.SetStartNodeAsync(f.WorkflowId, int.MaxValue - 1);
        Assert.Equal(SetStartNodeOutcome.RejectedNodeNotFound, result);
    }

    [Fact]
    public async Task SetStartNodeAsync_returns_RejectedNodeNotInWorkflow_for_cross_workflow_node()
    {
        await using var fA = await SetupAsync();
        await using var fB = await SetupAsync();
        var nodeInB = (await _nodes.CreateAsync(StartNode(fB.WorkflowId))).Id!.Value;

        var result = await _nodes.SetStartNodeAsync(fA.WorkflowId, nodeInB);
        Assert.Equal(SetStartNodeOutcome.RejectedNodeNotInWorkflow, result);
    }

    [Fact]
    public async Task SetStartNodeAsync_returns_RejectedNotStartNode_for_non_start_node()
    {
        await using var f = await SetupAsync();
        var process = (await _nodes.CreateAsync(ProcessNode(f.WorkflowId, f.BlockId))).Id!.Value;

        var result = await _nodes.SetStartNodeAsync(f.WorkflowId, process);
        Assert.Equal(SetStartNodeOutcome.RejectedNotStartNode, result);
    }

    // ---- fixture / helpers ---------------------------------------------

    private sealed class Fixture : IAsyncDisposable
    {
        public required int TypeId { get; init; }
        public required int VersionId { get; init; }
        public required int WorkflowId { get; init; }
        public required int BlockId { get; init; }
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

        var wfId = (await _workflows.CreateAsync(
            versionId, $"_test_wf_{Guid.NewGuid():N}", null)).Id!.Value;
        var blockId = await InsertTestBlockAsync();

        return new Fixture
        {
            TypeId = typeId,
            VersionId = versionId,
            WorkflowId = wfId,
            BlockId = blockId,
            Cleanup = async () =>
            {
                await CleanupWorkflowsForTypeAsync(typeId);
                await DeleteVersionsForTypeAsync(typeId);
                await DeleteTypeAsync(typeId);
                await DeleteTestBlockAsync(blockId);
            },
        };
    }

    private static WorkflowNode StartNode(int workflowId) => new()
    {
        WorkflowDefinitionId = workflowId,
        NodeTypeId = WorkflowNodeTypeIds.Start,
    };

    private static WorkflowNode ProcessNode(int workflowId, int blockId) => new()
    {
        WorkflowDefinitionId = workflowId,
        NodeTypeId = WorkflowNodeTypeIds.Process,
        BlockCatalogId = blockId,
    };

    private static WorkflowNode TerminalNode(int workflowId, int terminalType) => new()
    {
        WorkflowDefinitionId = workflowId,
        NodeTypeId = terminalType,
    };

    private async Task SetLevelAsync(int nodeId, int level)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE dbo.workflow_nodes SET execution_level = @level WHERE id = @nodeId;",
            new { nodeId, level });
    }

    private async Task SetPath1RawAsync(int sourceId, int? targetId)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE dbo.workflow_nodes SET path1_node_id = @targetId WHERE id = @sourceId;",
            new { sourceId, targetId });
    }

    private async Task ForceVersionStateAsync(int versionId, string stateCode)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        await c.ExecuteAsync(
            "UPDATE dbo.request_type_versions SET request_state = @stateCode WHERE id = @versionId;",
            new { versionId, stateCode });
    }

    private async Task<int> InsertTestBlockAsync()
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        return await c.QuerySingleAsync<int>(@"
            INSERT INTO dbo.block_catalog (node_type_id, description, class_name, is_active)
            VALUES (2, @desc, 'VendorSure.Test.NoOpBlock', 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { desc = "_test_block_" + Guid.NewGuid().ToString("N") });
    }

    private async Task DeleteTestBlockAsync(int blockId)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM dbo.block_catalog WHERE id = @blockId;", new { blockId });
    }

    private async Task CleanupWorkflowsForTypeAsync(int typeId)
    {
        // Mirror the WorkflowDefinitionRepository cleanup pattern: nulls
        // first to clear self-referential FKs, then nodes, then workflows.
        // Bypasses the Draft gate so cleanup works after tests force
        // versions into non-Draft states.
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        await c.ExecuteAsync(@"
            UPDATE wd
            SET wd.start_node_id = NULL
            FROM dbo.workflow_definitions wd
            INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
            WHERE ver.request_type_id = @typeId;

            UPDATE n
            SET n.path1_node_id = NULL, n.path2_node_id = NULL
            FROM dbo.workflow_nodes n
            INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
            INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
            WHERE ver.request_type_id = @typeId;

            DELETE n
            FROM dbo.workflow_nodes n
            INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
            INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
            WHERE ver.request_type_id = @typeId;

            DELETE wd
            FROM dbo.workflow_definitions wd
            INNER JOIN dbo.request_type_versions ver ON ver.id = wd.request_type_version_id
            WHERE ver.request_type_id = @typeId;",
            new { typeId });
    }

    private async Task DeleteVersionsForTypeAsync(int typeId)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        await c.ExecuteAsync(
            "DELETE FROM dbo.request_type_versions WHERE request_type_id = @typeId;",
            new { typeId });
    }

    private async Task DeleteTypeAsync(int id)
    {
        using var c = await _connectionFactory.CreateOpenConnectionAsync();
        await c.ExecuteAsync("DELETE FROM dbo.request_types WHERE id = @id;", new { id });
    }
}
