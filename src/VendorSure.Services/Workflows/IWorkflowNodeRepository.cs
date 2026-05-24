using VendorSure.Domain.Workflows;

namespace VendorSure.Services.Workflows;

/// <summary>Outcome of <see cref="IWorkflowNodeRepository.InsertChildAsync"/>.</summary>
public enum InsertChildOutcome
{
    Inserted,

    /// <summary>The parent node doesn't exist.</summary>
    RejectedParentNotFound,

    /// <summary>The parent's workflow version isn't Draft.</summary>
    RejectedNotDraft,

    /// <summary>
    /// The parent is a terminal node (Approved/Rejected/Cancelled) and
    /// can't have children. The UI shouldn't have rendered a + button
    /// here; this outcome is the defensive case.
    /// </summary>
    RejectedParentIsTerminal,

    /// <summary>
    /// The new node's <c>node_type_id</c> / <c>block_catalog_id</c>
    /// pairing violates the schema's CK_workflow_nodes_block_by_type
    /// CHECK. Same meaning as <see cref="CreateNodeOutcome.RejectedInvalidShape"/>.
    /// </summary>
    RejectedInvalidShape,
}

public sealed record InsertChildResult(InsertChildOutcome Outcome, int? Id);

/// <summary>
/// Input record for <see cref="IWorkflowNodeRepository.InsertChildAsync"/>.
/// </summary>
/// <param name="ParentNodeId">Node the + button was clicked on.</param>
/// <param name="ParentSlot">Which parent slot the new node attaches to: 1 = path1, 2 = path2. Must be 1 for Start/Process parents; either for Decision parents.</param>
/// <param name="NodeTypeId">Type of the new node (1=Start, 2=Process, 3=Decision, 4=Approved, 5=Rejected, 6=Cancelled).</param>
/// <param name="BlockCatalogId">Required for Process/Decision, must be null for Start/terminals.</param>
/// <param name="DecisionChildSlot">
/// Only meaningful when the new node is a Decision being inserted between
/// a parent and an existing child. Specifies which slot of the new
/// Decision (1 = path1, 2 = path2) inherits the displaced child. The
/// other slot is left null and gets filled later by the user. Pass null
/// when the parent's slot was empty (append case) or when the new node
/// isn't a Decision.
/// </param>
public sealed record InsertChildRequest(
    int ParentNodeId,
    int ParentSlot,
    int NodeTypeId,
    int? BlockCatalogId,
    int? DecisionChildSlot = null);

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

/// <summary>Outcome of <see cref="IWorkflowNodeRepository.DeleteSubtreeAsync"/>.</summary>
public enum DeleteSubtreeOutcome
{
    Deleted,

    /// <summary>The node doesn't exist.</summary>
    RejectedNodeNotFound,

    /// <summary>The node's workflow version isn't Draft.</summary>
    RejectedNotDraft,

    /// <summary>The node is a Start. Start nodes can never be deleted — every workflow needs its Start.</summary>
    RejectedIsStart,
}

public sealed record DeleteSubtreeResult(DeleteSubtreeOutcome Outcome, int DescendantsDeleted);

/// <summary>Outcome of <see cref="IWorkflowNodeRepository.DeleteAndSpliceAsync"/>.</summary>
public enum DeleteAndSpliceOutcome
{
    Deleted,

    /// <summary>The node doesn't exist.</summary>
    RejectedNodeNotFound,

    /// <summary>The node's workflow version isn't Draft.</summary>
    RejectedNotDraft,

    /// <summary>The node is a Start. Start nodes can never be deleted.</summary>
    RejectedIsStart,
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
    /// High-level "add a child node" operation introduced for the
    /// +-button graph-construction model. Atomically inserts the new
    /// node, wires it into the parent's slot, optionally splices an
    /// existing child below it, and renumbers any displaced subtree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two scenarios, both handled in one call:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <b>Append</b> — parent's slot is currently null. The new node
    ///     becomes the parent's child; the new node has no children yet.
    ///   </item>
    ///   <item>
    ///     <b>Insert-between</b> — parent's slot already points at child C.
    ///     The new node takes C's place under the parent; C becomes the
    ///     new node's child. If the new node is a Decision, the caller
    ///     specifies which Decision slot (1 or 2) C inherits via
    ///     <see cref="InsertChildRequest.DecisionChildSlot"/>. For Start
    ///     and Process new-nodes, C goes into path1 by definition (the
    ///     only available slot). Terminals can't be inserted between
    ///     because they can't have children — UI must filter them out
    ///     of the picker when the slot is non-empty.
    ///   </item>
    /// </list>
    /// <para>
    /// All work happens in one transaction. On any failure the whole
    /// thing rolls back; partial state is impossible.
    /// </para>
    /// <para>
    /// Argument validation: <see cref="InsertChildRequest.ParentSlot"/>
    /// out of {1, 2}, or <see cref="InsertChildRequest.DecisionChildSlot"/>
    /// missing when required, throw <see cref="ArgumentException"/> —
    /// these are caller bugs, not user-visible state.
    /// </para>
    /// </remarks>
    Task<InsertChildResult> InsertChildAsync(
        InsertChildRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>workflow_definitions.start_node_id</c> for the given
    /// workflow. When <paramref name="nodeId"/> is non-null the node must
    /// be a Start node in this workflow; its execution_level is set to 1
    /// and its subtree is renumbered. When <paramref name="nodeId"/> is
    /// null the start_node_id is cleared with no renumbering.
    /// </summary>
    Task<SetStartNodeOutcome> SetStartNodeAsync(
        int workflowDefinitionId, int? nodeId, CancellationToken ct = default);

    /// <summary>
    /// Atomically deletes a node and all of its descendants reachable via
    /// path1/path2. Used by the designer's "delete entire branch" action
    /// for Decisions (the only delete option, since Decision splice isn't
    /// well-defined — two subtrees, no clean way to merge) and as the
    /// "delete this and N descendants" option on Process node deletion
    /// when the user picks subtree over splice.
    /// </summary>
    /// <remarks>
    /// All work happens in one transaction:
    ///   1. Walk descendants via recursive CTE.
    ///   2. Null the upstream parent's path FK that pointed at the root
    ///      of the subtree being deleted.
    ///   3. Null all path FKs WITHIN the subtree so the cascade delete
    ///      doesn't violate any self-referential FK during row removal.
    ///   4. Delete all rows in the subtree (including the root).
    ///
    /// Start cannot be deleted (every workflow needs its Start). Caller
    /// receives RejectedIsStart and the data is untouched.
    /// </remarks>
    Task<DeleteSubtreeResult> DeleteSubtreeAsync(int nodeId, CancellationToken ct = default);

    /// <summary>
    /// Atomically deletes a node and lifts its single child up into its
    /// place. Valid only for Start (blocked by RejectedIsStart) and
    /// Process — nodes with at most one out-edge (path1). For Decision
    /// or terminal nodes, throws <see cref="ArgumentException"/>: the
    /// UI shouldn't offer splice for those.
    /// </summary>
    /// <remarks>
    /// The "splice into parent" option on Process node deletion. Replaces
    /// the upstream parent's path FK with the deleted node's path1 (the
    /// surviving child, which may be null). Renumbers the surviving
    /// subtree to shift up by 1 via the existing recursive CTE.
    ///
    /// Edge cases handled atomically:
    ///   - Surviving child is null (Process had no descendants): parent's
    ///     FK becomes null; no renumber needed.
    ///   - Surviving child has its own descendants: full subtree
    ///     renumbered upward.
    ///   - Node is orphaned (no upstream parent): defensive — shouldn't
    ///     happen in normal workflows, returns RejectedNodeNotFound to
    ///     avoid producing inconsistent state.
    /// </remarks>
    Task<DeleteAndSpliceOutcome> DeleteAndSpliceAsync(int nodeId, CancellationToken ct = default);
}
