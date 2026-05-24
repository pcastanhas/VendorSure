using VendorSure.Domain.RequestTypes;

namespace VendorSure.Services.RequestTypes;

/// <summary>Outcome of <see cref="IRequestTypeVersionRepository.CreateDraftAsync"/>.</summary>
public enum CreateDraftOutcome
{
    Created,

    /// <summary>The referenced <see cref="RequestType"/> doesn't exist.</summary>
    RejectedRequestTypeNotFound,
}

public sealed record CreateDraftResult(CreateDraftOutcome Outcome, int? Id, int? Version);

/// <summary>Outcome of <see cref="IRequestTypeVersionRepository.UpdateAsync"/>.</summary>
public enum UpdateRequestTypeVersionResult
{
    Updated,
    NotFound,

    /// <summary>
    /// The version is In Service or Superseded — immutable. Edit by creating
    /// a new Draft version of the same Request Type.
    /// </summary>
    RejectedNotDraft,
}

/// <summary>Outcome of <see cref="IRequestTypeVersionRepository.TransitionToInServiceAsync"/>.</summary>
public enum TransitionToInServiceOutcome
{
    /// <summary>
    /// The target version is now In Service. If a prior In Service version
    /// of the same type existed, it was atomically moved to Superseded in
    /// the same transaction with <c>superseded_ts</c> set.
    /// </summary>
    Succeeded,

    /// <summary>The target version doesn't exist.</summary>
    NotFound,

    /// <summary>
    /// The target version exists but isn't currently in Draft state. Only
    /// Drafts can be placed in service.
    /// </summary>
    RejectedNotDraft,

    /// <summary>
    /// The version has at least one workflow with structural problems —
    /// nodes missing required children, orphan nodes that aren't reachable
    /// from Start, or a workflow with no Start at all. The
    /// <see cref="TransitionToInServiceResult.Issues"/> list spells out
    /// what to fix.
    /// </summary>
    /// <remarks>
    /// Chunk 7 dropped <c>CK_workflow_nodes_decision_both_edges</c>,
    /// moving the "every Decision has both children" rule from the data
    /// layer to here. This outcome is now the sole enforcer of that rule
    /// plus the matching "Start/Process need their single child" and
    /// "no orphan nodes" invariants.
    /// </remarks>
    RejectedIncompleteWorkflow,
}

/// <summary>
/// One concrete problem found by promotion-time validation. Aggregated
/// inside <see cref="TransitionToInServiceResult"/> when the outcome is
/// <see cref="TransitionToInServiceOutcome.RejectedIncompleteWorkflow"/>.
/// </summary>
/// <param name="WorkflowId">The workflow row that has the problem.</param>
/// <param name="WorkflowName">Display name of that workflow.</param>
/// <param name="Kind">What's wrong (see <see cref="WorkflowIssueKind"/>).</param>
/// <param name="NodeId">
/// The specific node, when the issue is per-node (incomplete or orphan).
/// Null for workflow-level issues like <see cref="WorkflowIssueKind.NoStartNode"/>.
/// </param>
public sealed record WorkflowIssue(
    int WorkflowId, string WorkflowName, WorkflowIssueKind Kind, int? NodeId);

public enum WorkflowIssueKind
{
    /// <summary>Start or Process node missing path1 (required out-edge).</summary>
    MissingPath1,

    /// <summary>Decision node missing path1 (left branch).</summary>
    DecisionMissingPath1,

    /// <summary>Decision node missing path2 (right branch).</summary>
    DecisionMissingPath2,

    /// <summary>
    /// Node has no incoming path FK from anywhere in the workflow AND is
    /// not the workflow's Start. Shouldn't exist in graphs built via the
    /// UI; flagged loudly here so legacy / regressed data can't be
    /// promoted.
    /// </summary>
    OrphanNode,

    /// <summary>The workflow's <c>start_node_id</c> is NULL.</summary>
    NoStartNode,
}

/// <summary>
/// Result of <see cref="IRequestTypeVersionRepository.TransitionToInServiceAsync"/>.
/// On success or non-validation rejections (<see cref="TransitionToInServiceOutcome.NotFound"/>,
/// <see cref="TransitionToInServiceOutcome.RejectedNotDraft"/>) the
/// <see cref="Issues"/> list is empty. On
/// <see cref="TransitionToInServiceOutcome.RejectedIncompleteWorkflow"/>
/// it contains one entry per concrete problem the user has to fix.
/// </summary>
public sealed record TransitionToInServiceResult(
    TransitionToInServiceOutcome Outcome,
    IReadOnlyList<WorkflowIssue> Issues)
{
    public static TransitionToInServiceResult Succeeded { get; } =
        new(TransitionToInServiceOutcome.Succeeded, Array.Empty<WorkflowIssue>());

    public static TransitionToInServiceResult NotFound { get; } =
        new(TransitionToInServiceOutcome.NotFound, Array.Empty<WorkflowIssue>());

    public static TransitionToInServiceResult RejectedNotDraft { get; } =
        new(TransitionToInServiceOutcome.RejectedNotDraft, Array.Empty<WorkflowIssue>());

    public static TransitionToInServiceResult RejectedIncompleteWorkflow(IReadOnlyList<WorkflowIssue> issues) =>
        new(TransitionToInServiceOutcome.RejectedIncompleteWorkflow, issues);
}

/// <summary>
/// CRUD on <c>request_type_versions</c>. Each row is an immutable snapshot
/// of one version of a Request Type's required-docs, validations, prompts,
/// and workflow choices.
/// </summary>
/// <remarks>
/// Immutability is enforced here: <see cref="UpdateAsync"/> refuses unless
/// the target row is in <see cref="RequestState.Draft"/>. State transitions
/// are intentionally narrow — only <see cref="TransitionToInServiceAsync"/>
/// promotes a Draft to In Service (with the side-effect of demoting the
/// prior In Service to Superseded). There is no "transition back to Draft"
/// path and there is no "delete this version" path; both would violate the
/// snapshot semantics that running requests rely on.
/// </remarks>
public interface IRequestTypeVersionRepository
{
    Task<RequestTypeVersion?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Returns every version belonging to a Request Type, ordered by
    /// version ascending (oldest first).
    /// </summary>
    Task<IReadOnlyList<RequestTypeVersion>> GetByRequestTypeIdAsync(
        int requestTypeId, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new Draft version of the given Request Type. The version
    /// number is computed as (current max version + 1), or 1 if the type
    /// has no prior versions. Atomically computed in SQL so no race
    /// window opens between MAX() and the INSERT.
    /// </summary>
    Task<CreateDraftResult> CreateDraftAsync(int requestTypeId, CancellationToken ct = default);

    /// <summary>
    /// Updates the editable fields of a Draft version (Name,
    /// WorkflowSelectionPrompt). Refuses unless the row is currently Draft —
    /// In Service and Superseded versions are immutable.
    /// </summary>
    /// <remarks>
    /// <see cref="RequestTypeVersion.Version"/>, <see cref="RequestTypeVersion.RequestTypeId"/>,
    /// timestamps, and the state itself are NOT touched by this method;
    /// those are either DB-managed or only changed by
    /// <see cref="TransitionToInServiceAsync"/>. Edits to the version's
    /// required-docs and validations are managed by their own junction
    /// repositories.
    /// </remarks>
    Task<UpdateRequestTypeVersionResult> UpdateAsync(
        RequestTypeVersion version, CancellationToken ct = default);

    /// <summary>
    /// Promotes the given Draft version to In Service. If another version
    /// of the same type is currently In Service, it is atomically demoted
    /// to Superseded in the same transaction; both rows' timestamps
    /// (<c>placed_in_service_ts</c> on the new, <c>superseded_ts</c> on the
    /// old) are set to the same SYSUTCDATETIME value.
    /// </summary>
    /// <remarks>
    /// Concurrent calls for the same version are serialised by an UPDLOCK
    /// on the initial Draft-state check; only one transition can succeed.
    /// </remarks>
    Task<TransitionToInServiceResult> TransitionToInServiceAsync(
        int versionId, CancellationToken ct = default);
}
