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

/// <summary>
/// CRUD on <c>request_type_versions</c>. Each row is an immutable snapshot
/// of one version of a Request Type's required-docs, validations, prompts,
/// and workflow choices.
/// </summary>
/// <remarks>
/// Immutability is enforced here: <see cref="UpdateAsync"/> refuses unless
/// the target row is in <see cref="RequestState.Draft"/>. State transitions
/// (Draft → InService → Superseded) are intentionally NOT exposed in this
/// chunk; they ship in Phase 4 / Chunk 9 with their own focused method,
/// because transitioning requires atomically updating multiple rows (the
/// new InService and the currently-InService-becoming-Superseded sibling).
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
    /// those are either DB-managed or only changed by the state-transition
    /// method shipped in Chunk 9. Edits to the version's required-docs and
    /// validations are managed by their own junction repositories.
    /// </remarks>
    Task<UpdateRequestTypeVersionResult> UpdateAsync(
        RequestTypeVersion version, CancellationToken ct = default);
}
