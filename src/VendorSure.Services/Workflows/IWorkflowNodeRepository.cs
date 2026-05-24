using VendorSure.Domain.Workflows;

namespace VendorSure.Services.Workflows;

/// <summary>Outcome of <see cref="IWorkflowNodeRepository.CreateAsync"/>.</summary>
public enum CreateNodeOutcome
{
    Created,

    /// <summary>The target workflow doesn't exist.</summary>
    RejectedWorkflowNotFound,

    /// <summary>The workflow's parent Request Type version isn't Draft.</summary>
    RejectedNotDraft,

    /// <summary>
    /// The seed combines a node type and a block_catalog_id that can't
    /// coexist per the schema's CK_workflow_nodes_block_by_type CHECK
    /// (Process/Decision require a block; Start/Terminals forbid one).
    /// Pre-validated in C# so the caller gets a clean result enum rather
    /// than a SqlException with error 547.
    /// </summary>
    RejectedInvalidShape,
}

public sealed record CreateNodeResult(CreateNodeOutcome Outcome, int? Id);

/// <summary>Outcome of <see cref="IWorkflowNodeRepository.UpdateAsync"/>.</summary>
public enum UpdateNodeResult
{
    Updated,
    NotFound,
    RejectedNotDraft,
}

/// <summary>Outcome of <see cref="IWorkflowNodeRepository.DeleteAsync"/>.</summary>
public enum DeleteNodeResult
{
    Deleted,
    NotFound,
    RejectedNotDraft,
}

/// <summary>Outcome of the <c>SetPath1Async</c> / <c>SetPath2Async</c> wiring methods.</summary>
public enum SetPathOutcome
{
    Updated,

    /// <summary>The source node doesn't exist.</summary>
    NotFound,

    /// <summary>The parent Request Type version isn't Draft.</summary>
    RejectedNotDraft,

    /// <summary>The source node's type doesn't allow this path slot (e.g. path2 on a Process node).</summary>
    RejectedShape,

    /// <summary>The target node doesn't exist (only when targetNodeId is non-null).</summary>
    RejectedTargetNotFound,

    /// <summary>The target node belongs to a different workflow.</summary>
    RejectedTargetNotInWorkflow,

    /// <summary>
    /// The target already has an incoming edge from another node. Branch
    /// merging is deferred per the Phase 5 design (Q4a) — each node has at
    /// most one parent.
    /// </summary>
    RejectedTargetAlreadyHasParent,

    /// <summary>
    /// Source and target are the same node. The schema doesn't forbid it
    /// but the repository does; a self-loop has no semantic in the workflow
    /// engine model.
    /// </summary>
    RejectedSelfLoop,
}

/// <summary>Outcome of <see cref="IWorkflowNodeRepository.SetStartNodeAsync"/>.</summary>
public enum SetStartNodeOutcome
{
    Updated,
    RejectedWorkflowNotFound,
    RejectedNotDraft,

    /// <summary>The node id passed in doesn't exist (only when non-null).</summary>
    RejectedNodeNotFound,

    /// <summary>The node belongs to a different workflow.</summary>
    RejectedNodeNotInWorkflow,

    /// <summary>The node isn't a Start node (node_type_id != 1).</summary>
    RejectedNotStartNode,
}

/// <summary>
/// CRUD on <c>workflow_nodes</c> plus the wiring operations the designer
/// needs (set path1/path2, set start node, delete with upstream
/// disconnection). All mutations are gated on the parent Request Type
/// version being in Draft.
/// </summary>
/// <remarks>
/// <para>
/// <b>execution_level model:</b> a node's level is its topological depth
/// from the workflow's Start node (Start = 1). A freshly-created node has
/// level 0 — "unwired." When an edge is wired into the node via
/// <see cref="SetPath1Async"/> or <see cref="SetPath2Async"/>, the repo
/// walks the target's subtree assigning new levels (target = parent.level + 1,
/// target's children = target.level + 1, etc.). The walk uses a recursive
/// CTE in one SQL statement.
/// </para>
/// <para>
/// Deletion does NOT renumber: when a node is deleted, its downstream
/// subtree retains its current levels (stale but harmless). The next
/// wiring operation that touches the subtree will reset them. The
/// upstream parents' path FKs that pointed at the deleted node are
/// nulled out so the orphaned subtree is reachable only by id, not by
/// graph traversal.
/// </para>
/// <para>
/// <b>No branch merging:</b> a node may have at most one incoming edge.
/// <see cref="SetPath1Async"/> and <see cref="SetPath2Async"/> reject
/// targets that already have a parent. The schema doesn't enforce this
/// invariant directly; the repository does.
/// </para>
/// </remarks>
public interface IWorkflowNodeRepository
{
    /// <summary>
    /// Returns all nodes for a workflow ordered by execution_level ASC,
    /// then id ASC for stability when multiple nodes share a level (which
    /// happens for siblings of a Decision and for unwired orphans).
    /// </summary>
    Task<IReadOnlyList<WorkflowNode>> ListByWorkflowIdAsync(
        int workflowDefinitionId, CancellationToken ct = default);

    Task<WorkflowNode?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new node into a workflow with execution_level = 0
    /// (unwired). The caller passes node_type_id, block_catalog_id, and
    /// optional editable fields. Edges (<c>path1_node_id</c>, <c>path2_node_id</c>)
    /// are always null at creation; wire them via <see cref="SetPath1Async"/>
    /// / <see cref="SetPath2Async"/>.
    /// </summary>
    /// <remarks>
    /// The seed's <see cref="WorkflowNode.ExecutionLevel"/>, <see cref="WorkflowNode.Path1NodeId"/>,
    /// and <see cref="WorkflowNode.Path2NodeId"/> are ignored — these are
    /// always 0 / null / null on a new row.
    /// </remarks>
    Task<CreateNodeResult> CreateAsync(WorkflowNode seed, CancellationToken ct = default);

    /// <summary>
    /// Updates the editable property fields on a node: prompt_text,
    /// path1_prompt_text, path2_prompt_text, approver_group_id,
    /// stale_threshold_days, stale_message_text, notes.
    /// Does NOT touch node_type, block_catalog_id, workflow,
    /// execution_level, or path FKs.
    /// </summary>
    Task<UpdateNodeResult> UpdateAsync(WorkflowNode edited, CancellationToken ct = default);

    /// <summary>
    /// Sets (or clears, if <paramref name="targetNodeId"/> is null) the
    /// source node's <c>path1_node_id</c>. When setting a non-null target,
    /// the target's execution_level is recomputed as
    /// <c>source.ExecutionLevel + 1</c> and the target's subtree is
    /// renumbered to maintain depth. All in one transaction.
    /// </summary>
    Task<SetPathOutcome> SetPath1Async(int sourceId, int? targetNodeId, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="SetPath1Async"/> but for <c>path2_node_id</c>.
    /// Only Decision (node_type_id = 3) nodes have a path2 slot; other
    /// types return <see cref="SetPathOutcome.RejectedShape"/>.
    /// </summary>
    Task<SetPathOutcome> SetPath2Async(int sourceId, int? targetNodeId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a node. First nulls out any upstream path FK that points at
    /// it (within the same workflow) and any <c>workflow_definitions.start_node_id</c>
    /// that points at it. Does NOT renumber the downstream subtree —
    /// orphans keep their levels until re-wired (see remarks on the
    /// interface).
    /// </summary>
    Task<DeleteNodeResult> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>workflow_definitions.start_node_id</c> for the given
    /// workflow. When <paramref name="nodeId"/> is non-null the node must
    /// be a Start node in this workflow; its execution_level is set to 1
    /// and its subtree is renumbered. When <paramref name="nodeId"/> is
    /// null the start_node_id is cleared with no renumbering.
    /// </summary>
    Task<SetStartNodeOutcome> SetStartNodeAsync(
        int workflowDefinitionId, int? nodeId, CancellationToken ct = default);
}
