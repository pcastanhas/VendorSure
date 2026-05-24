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
public enum TransitionToInServiceResult
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
