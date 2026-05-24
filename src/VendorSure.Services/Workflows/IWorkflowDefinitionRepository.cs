using VendorSure.Domain.Workflows;

namespace VendorSure.Services.Workflows;

/// <summary>Outcome of <see cref="IWorkflowDefinitionRepository.CreateAsync"/>.</summary>
public enum CreateWorkflowOutcome
{
    Created,

    /// <summary>The target Request Type version doesn't exist.</summary>
    RejectedVersionNotFound,

    /// <summary>The target version exists but isn't currently Draft.</summary>
    RejectedNotDraft,

    /// <summary>
    /// Another workflow on the same version already has this name
    /// (UQ_workflow_definitions_name).
    /// </summary>
    RejectedNameConflict,
}

public sealed record CreateWorkflowResult(CreateWorkflowOutcome Outcome, int? Id);

/// <summary>Outcome of <see cref="IWorkflowDefinitionRepository.UpdateAsync"/>.</summary>
public enum UpdateWorkflowResult
{
    Updated,
    NotFound,

    /// <summary>The workflow's parent version isn't Draft.</summary>
    RejectedNotDraft,

    /// <summary>
    /// The new name collides with another workflow on the same version.
    /// </summary>
    RejectedNameConflict,
}

/// <summary>Outcome of <see cref="IWorkflowDefinitionRepository.DeleteAsync"/>.</summary>
public enum DeleteWorkflowResult
{
    Deleted,
    NotFound,

    /// <summary>The workflow's parent version isn't Draft.</summary>
    RejectedNotDraft,
}

/// <summary>
/// CRUD on <c>workflow_definitions</c> — the per-Request-Type-version list
/// of candidate workflows the engine could run for incoming submissions.
/// </summary>
/// <remarks>
/// All mutations gated on the parent Request Type version being in
/// <see cref="VendorSure.Domain.RequestTypes.RequestState.Draft"/>, same
/// pattern as the Phase 4 junction and validation repositories.
///
/// <see cref="DeleteAsync"/> is transactional and cleans up the workflow's
/// nodes too — null out <c>start_node_id</c>, null out node
/// <c>path1_node_id</c> / <c>path2_node_id</c> self-references, delete
/// nodes, delete the workflow. Without that the FK constraints would block
/// the delete the moment Chunk 3 starts inserting nodes.
///
/// This repository does NOT manage <see cref="WorkflowDefinition.StartNodeId"/>.
/// That field is read for completeness but set by the node repository
/// (Chunk 3) when the designer drops a Start node onto the canvas.
/// </remarks>
public interface IWorkflowDefinitionRepository
{
    /// <summary>
    /// Returns all workflow definitions for a Request Type version, ordered
    /// by name ascending. Empty list if the version has no workflows.
    /// </summary>
    Task<IReadOnlyList<WorkflowDefinition>> ListByVersionIdAsync(
        int requestTypeVersionId, CancellationToken ct = default);

    Task<WorkflowDefinition?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Creates a new workflow on the given Draft version. The new row's
    /// <c>start_node_id</c> is null — the designer sets it later when a
    /// Start node is dropped onto the canvas.
    /// </summary>
    Task<CreateWorkflowResult> CreateAsync(
        int requestTypeVersionId,
        string name,
        string? notes,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the editable workflow-level fields (Name, Notes). Does NOT
    /// touch <c>start_node_id</c> — that's the node repository's job.
    /// Refused if the parent version isn't Draft.
    /// </summary>
    Task<UpdateWorkflowResult> UpdateAsync(
        int id, string name, string? notes, CancellationToken ct = default);

    /// <summary>
    /// Transactionally deletes a workflow and all of its nodes. Refused
    /// unless the parent version is Draft.
    /// </summary>
    /// <remarks>
    /// Cleanup order inside the transaction handles the self-referential
    /// FKs on <c>workflow_nodes</c>:
    ///   1. Null out <c>workflow_definitions.start_node_id</c>.
    ///   2. Null out <c>workflow_nodes.path1_node_id</c> /
    ///      <c>path2_node_id</c> for this workflow's nodes.
    ///   3. Delete nodes.
    ///   4. Delete the workflow definition.
    /// </remarks>
    Task<DeleteWorkflowResult> DeleteAsync(int id, CancellationToken ct = default);
}
