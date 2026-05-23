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
