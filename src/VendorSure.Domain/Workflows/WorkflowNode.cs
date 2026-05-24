namespace VendorSure.Domain.Workflows;

/// <summary>
/// One node in a workflow's static graph. Six possible <see cref="NodeTypeId"/>
/// values (see <see cref="WorkflowNodeTypeIds"/>) determine which other
/// fields are meaningful and how many out-edges the node has.
/// </summary>
/// <remarks>
/// The schema enforces per-type invariants via CHECK constraints:
/// <list type="bullet">
///   <item>Process (2) and Decision (3) require a <see cref="BlockCatalogId"/>.</item>
///   <item>Start (1) and the three terminals (4/5/6) forbid one.</item>
///   <item>Terminals have no out-edges.</item>
///   <item>Process and Start have exactly one out-edge (path1).</item>
///   <item>Decisions have two out-edges (both path1 and path2 required at runtime,
///         but the designer can leave them NULL on partial Drafts).</item>
/// </list>
/// <see cref="ExecutionLevel"/> = topological depth from the workflow's
/// Start node, 1-indexed. Unwired (orphan) nodes carry level 0 until the
/// designer wires them in.
/// </remarks>
public sealed class WorkflowNode
{
    public int Id { get; init; }
    public int WorkflowDefinitionId { get; init; }
    public int NodeTypeId { get; init; }

    /// <summary>
    /// Reference to <c>block_catalog</c>. Required for Process and Decision
    /// nodes, must be null for Start and terminal nodes (schema CHECK).
    /// </summary>
    public int? BlockCatalogId { get; init; }

    /// <summary>
    /// Topological depth from the workflow's Start node, 1-indexed.
    /// 0 = unwired (orphan). Maintained by the repository; the engine
    /// walks levels at runtime in Phase 6+.
    /// </summary>
    public int ExecutionLevel { get; init; }

    /// <summary>Approver user group for user-decision blocks. Optional.</summary>
    public int? ApproverGroupId { get; init; }

    /// <summary>Days the node can sit idle before being flagged stale. Optional.</summary>
    public int? StaleThresholdDays { get; init; }

    /// <summary>Text on the stale alarm email. Optional.</summary>
    public string? StaleMessageText { get; init; }

    /// <summary>Free-form notes shown in the designer; not surfaced to submitters.</summary>
    public string? Notes { get; init; }

    /// <summary>Out-edge target (single edge for Process/Start; first edge for Decision).</summary>
    public int? Path1NodeId { get; init; }

    /// <summary>Out-edge target (second edge for Decision; null otherwise).</summary>
    public int? Path2NodeId { get; init; }

    /// <summary>The question on a Decision node, e.g. "Is this vendor foreign?".</summary>
    public string? PromptText { get; init; }

    /// <summary>Label on the path1 out-edge, e.g. "Yes".</summary>
    public string? Path1PromptText { get; init; }

    /// <summary>Label on the path2 out-edge, e.g. "No".</summary>
    public string? Path2PromptText { get; init; }
}

/// <summary>
/// Stable, hardcoded node-type ids matching the seed rows inserted by
/// <c>data-model.sql</c>. These are the numeric ids the schema's CHECK
/// constraints reference; treat them as fixed forever.
/// </summary>
public static class WorkflowNodeTypeIds
{
    public const int Start = 1;
    public const int Process = 2;
    public const int Decision = 3;
    public const int Approved = 4;
    public const int Rejected = 5;
    public const int Cancelled = 6;

    /// <summary>True for node types that require a block_catalog_id.</summary>
    public static bool RequiresBlock(int nodeTypeId) => nodeTypeId is Process or Decision;

    /// <summary>True for node types that have two out-edges (path1 + path2).</summary>
    public static bool IsDecision(int nodeTypeId) => nodeTypeId == Decision;

    /// <summary>True for terminal node types (Approved/Rejected/Cancelled).</summary>
    public static bool IsTerminal(int nodeTypeId) => nodeTypeId is Approved or Rejected or Cancelled;
}
