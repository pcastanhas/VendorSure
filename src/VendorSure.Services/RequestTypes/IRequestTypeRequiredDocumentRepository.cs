using VendorSure.Domain.RequestTypes;

namespace VendorSure.Services.RequestTypes;

/// <summary>
/// Junction row enriched with the library entry's display fields, so the
/// admin detail page can render a list without a second round-trip per row.
/// </summary>
public sealed record RequestTypeRequiredDocumentListItem(
    int Id,
    int RequestTypeVersionId,
    int RequiredDocumentLibraryId,
    string LibraryName,
    string? FileTypeRequired,
    bool Required);

/// <summary>
/// Outcome of <see cref="IRequestTypeRequiredDocumentRepository.AddAsync"/>.
/// </summary>
public enum AddRequiredDocumentOutcome
{
    Added,

    /// <summary>The target version doesn't exist.</summary>
    RejectedVersionNotFound,

    /// <summary>The target version exists but isn't Draft.</summary>
    RejectedNotDraft,

    /// <summary>The library entry doesn't exist.</summary>
    RejectedDocumentNotFound,

    /// <summary>This (version, library entry) pair already has a junction row.</summary>
    RejectedDuplicate,
}

public sealed record AddRequiredDocumentResult(AddRequiredDocumentOutcome Outcome, int? Id);

/// <summary>
/// Outcome of <see cref="IRequestTypeRequiredDocumentRepository.RemoveAsync"/>
/// and <see cref="IRequestTypeRequiredDocumentRepository.SetRequiredAsync"/>.
/// </summary>
public enum MutateRequiredDocumentResult
{
    Succeeded,

    /// <summary>No junction row with that id.</summary>
    NotFound,

    /// <summary>The junction row exists but its parent version isn't Draft.</summary>
    RejectedNotDraft,
}

/// <summary>
/// Manages the <c>request_type_required_documents</c> junction — which
/// document-type library entries a given Request Type version demands.
/// </summary>
/// <remarks>
/// Like the version repository's own UpdateAsync, all mutations here are
/// gated on the parent version being in <see cref="RequestState.Draft"/>.
/// Once a version is placed In Service its required-docs set is frozen
/// along with everything else about the version.
/// </remarks>
public interface IRequestTypeRequiredDocumentRepository
{
    /// <summary>
    /// Returns the document-type junctions for the given version, joined
    /// to <c>required_documents_library</c> so callers have the display
    /// fields (Name, FileTypeRequired) without another round-trip.
    /// Ordered by the library entry's name for stable listing.
    /// </summary>
    Task<IReadOnlyList<RequestTypeRequiredDocumentListItem>> ListByVersionIdAsync(
        int requestTypeVersionId, CancellationToken ct = default);

    /// <summary>
    /// Attaches a document-type to the given Draft version. Atomically
    /// checks that the version exists and is Draft, that the library
    /// entry exists, and that this pair isn't already linked.
    /// </summary>
    Task<AddRequiredDocumentResult> AddAsync(
        int requestTypeVersionId,
        int requiredDocumentLibraryId,
        bool required,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a junction row by its id. Refused if the parent version
    /// isn't Draft.
    /// </summary>
    Task<MutateRequiredDocumentResult> RemoveAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Toggles the <c>required</c> flag on an existing junction row.
    /// Refused if the parent version isn't Draft.
    /// </summary>
    Task<MutateRequiredDocumentResult> SetRequiredAsync(
        int id, bool required, CancellationToken ct = default);
}
