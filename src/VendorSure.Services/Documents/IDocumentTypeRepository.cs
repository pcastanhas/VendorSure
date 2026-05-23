using VendorSure.Domain.Documents;

namespace VendorSure.Services.Documents;

/// <summary>Outcome of <see cref="IDocumentTypeRepository.CreateAsync"/>.</summary>
public enum CreateDocumentTypeOutcome
{
    /// <summary>The row was inserted; <see cref="CreateDocumentTypeResult.Id"/> is the new id.</summary>
    Created,

    /// <summary>Another row already has the same <see cref="DocumentType.Name"/>.</summary>
    RejectedNameConflict,
}

public sealed record CreateDocumentTypeResult(CreateDocumentTypeOutcome Outcome, int? Id);

/// <summary>Outcome of <see cref="IDocumentTypeRepository.UpdateAsync"/>.</summary>
public enum UpdateDocumentTypeResult
{
    Updated,
    NotFound,

    /// <summary>The proposed name is already used by a different row.</summary>
    RejectedNameConflict,
}

/// <summary>Outcome of <see cref="IDocumentTypeRepository.DeleteAsync"/>.</summary>
public enum DeleteDocumentTypeResult
{
    Deleted,
    NotFound,

    /// <summary>
    /// The row is referenced by at least one Request Type version (via
    /// <c>request_type_required_documents</c>). Library rows in use cannot
    /// be deleted — they're part of the version's immutable snapshot.
    /// </summary>
    RejectedReferenced,
}

/// <summary>
/// CRUD on <c>required_documents_library</c> — the catalog of document
/// types Request Types can require submitters to upload.
/// </summary>
/// <remarks>
/// Unlike <c>user_groups</c>, this table has no <c>is_active</c> column.
/// 'Retiring' a document type is done by removing it from the library
/// (via <see cref="DeleteAsync"/>), which the repository allows only when
/// no Request Type version references it.
/// </remarks>
public interface IDocumentTypeRepository
{
    Task<IReadOnlyList<DocumentType>> GetAllAsync(CancellationToken ct = default);

    Task<DocumentType?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<CreateDocumentTypeResult> CreateAsync(DocumentType doc, CancellationToken ct = default);

    Task<UpdateDocumentTypeResult> UpdateAsync(DocumentType doc, CancellationToken ct = default);

    Task<DeleteDocumentTypeResult> DeleteAsync(int id, CancellationToken ct = default);
}
