using VendorSure.Domain.RequestTypes;

namespace VendorSure.Services.RequestTypes;

/// <summary>Outcome of <see cref="IRequestTypeRepository.CreateAsync"/>.</summary>
public enum CreateRequestTypeOutcome
{
    Created,
    RejectedNameConflict,
}

public sealed record CreateRequestTypeResult(CreateRequestTypeOutcome Outcome, int? Id);

/// <summary>
/// Outcome of the convenience method
/// <see cref="IRequestTypeRepository.CreateWithFirstDraftAsync"/>, which
/// atomically creates a type and its initial version 1 Draft.
/// </summary>
public sealed record CreateRequestTypeWithDraftResult(
    CreateRequestTypeOutcome Outcome,
    int? RequestTypeId,
    int? VersionId);

/// <summary>
/// A Request Type with summary information about its versions, for the
/// list page. <see cref="InServiceVersion"/> and <see cref="DraftVersion"/>
/// are the version numbers (1-based) of the type's currently In Service /
/// Draft versions, or <c>null</c> if no version is in that state.
/// </summary>
/// <remarks>
/// At most one In Service version per type at any moment, per the concept
/// (enforced atomically by the Chunk 9 state-transition code, not by the
/// schema). Drafts likewise are conventionally one-at-a-time but the
/// schema allows more; this projection returns whichever has the
/// highest version number if there are several, which is the only one
/// the UI ever cares about.
/// </remarks>
public sealed record RequestTypeListItem(
    RequestType Type,
    int? InServiceVersion,
    int? DraftVersion,
    int SupersededCount);

public enum UpdateRequestTypeResult
{
    Updated,
    NotFound,
    RejectedNameConflict,
}

/// <summary>
/// CRUD on <c>request_types</c> — the logical containers for each kind of
/// vendor request. Mutable fields are name, IsExplanationRequired, and
/// IsActive; all per-version state hangs off <c>request_type_versions</c>
/// and is managed by <see cref="IRequestTypeVersionRepository"/>.
/// </summary>
public interface IRequestTypeRepository
{
    Task<IReadOnlyList<RequestType>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all Request Types along with a summary of their version
    /// states (the current In Service version number, Draft version number,
    /// and Superseded count). One SQL round-trip via conditional
    /// aggregation; used by the admin list page so it doesn't N+1.
    /// </summary>
    Task<IReadOnlyList<RequestTypeListItem>> ListWithVersionInfoAsync(
        CancellationToken ct = default);

    Task<RequestType?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<CreateRequestTypeResult> CreateAsync(RequestType type, CancellationToken ct = default);

    /// <summary>
    /// Atomically inserts a new <see cref="RequestType"/> and its initial
    /// version-1 Draft <see cref="RequestTypeVersion"/>. Wraps both writes
    /// in a single transaction; either both land or neither does.
    /// </summary>
    /// <remarks>
    /// The convenience method exists because every new Request Type starts
    /// with a Draft — the page should not be on the hook for sequencing
    /// the two inserts. Callers that need finer control can use
    /// <see cref="CreateAsync"/> + <see cref="IRequestTypeVersionRepository.CreateDraftAsync"/>
    /// directly.
    /// </remarks>
    Task<CreateRequestTypeWithDraftResult> CreateWithFirstDraftAsync(
        RequestType type, CancellationToken ct = default);

    Task<UpdateRequestTypeResult> UpdateAsync(RequestType type, CancellationToken ct = default);
}
