using VendorSure.Domain.RequestTypes;

namespace VendorSure.Services.RequestTypes;

/// <summary>
/// Projection for a validation's attached required-document junction.
/// Carries the library entry's display name + file type hint so the
/// admin page renders without N+1 round-trips.
/// </summary>
public sealed record RequestTypeValidationDocumentListItem(
    int Id,
    int RequestTypeValidationId,
    int RequestTypeRequiredDocumentId,
    int RequiredDocumentLibraryId,
    string LibraryName,
    string? FileTypeRequired);

/// <summary>Outcome of <see cref="IRequestTypeValidationDocumentRepository.AddAsync"/>.</summary>
public enum AddValidationDocumentOutcome
{
    Added,

    /// <summary>The target validation doesn't exist.</summary>
    RejectedValidationNotFound,

    /// <summary>The referenced required-document junction doesn't exist.</summary>
    RejectedRequiredDocumentNotFound,

    /// <summary>
    /// Both endpoints exist, but they belong to different Request Type versions.
    /// The schema's FK constraints don't enforce same-version — the repository does.
    /// </summary>
    RejectedVersionMismatch,

    /// <summary>The (single, shared) parent version isn't Draft.</summary>
    RejectedNotDraft,

    /// <summary>This (validation, required-doc) pair already has a junction row.</summary>
    RejectedDuplicate,
}

public sealed record AddValidationDocumentResult(AddValidationDocumentOutcome Outcome, int? Id);

public enum RemoveValidationDocumentResult
{
    Removed,
    NotFound,
    RejectedNotDraft,
}

/// <summary>
/// Manages the <c>request_type_validation_documents</c> junction — which
/// of a version's required-document slots a given validation looks at.
/// </summary>
/// <remarks>
/// Critical invariant enforced here (the schema FKs don't): the validation
/// and the required-document must belong to the same version. Otherwise
/// the runtime AI service would be asked to validate against documents
/// from a foreign version, which makes no sense.
/// </remarks>
public interface IRequestTypeValidationDocumentRepository
{
    Task<IReadOnlyList<RequestTypeValidationDocumentListItem>> ListByValidationIdAsync(
        int requestTypeValidationId, CancellationToken ct = default);

    Task<AddValidationDocumentResult> AddAsync(
        int requestTypeValidationId,
        int requestTypeRequiredDocumentId,
        CancellationToken ct = default);

    Task<RemoveValidationDocumentResult> RemoveAsync(int id, CancellationToken ct = default);
}
