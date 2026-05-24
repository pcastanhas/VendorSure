namespace VendorSure.Domain.Workflows;

/// <summary>
/// One candidate workflow attached to a Request Type version. A version can
/// have several workflow definitions (named uniquely within the version);
/// the AI triage layer picks among them at submission time via the version's
/// <c>workflow_selection_prompt</c>.
/// </summary>
/// <remarks>
/// The workflow's graph lives in <c>workflow_nodes</c> (shipping in Phase 5 /
/// Chunk 3). <see cref="StartNodeId"/> points at the node where the engine
/// begins; it's nullable for newly-created workflows that don't have any
/// nodes yet, and set by the designer once a Start node is dropped onto the
/// canvas.
///
/// Mutations on workflow_definitions are gated on the parent
/// <see cref="VendorSure.Domain.RequestTypes.RequestTypeVersion"/> being in
/// Draft — once the version is placed in service the workflow is frozen
/// alongside everything else about the version.
/// </remarks>
public sealed class WorkflowDefinition
{
    public int Id { get; init; }
    public int RequestTypeVersionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public int? StartNodeId { get; init; }
}
