namespace VendorSure.Domain.RequestTypes;

/// <summary>
/// Junction row linking a <see cref="RequestTypeVersion"/> to a
/// <c>required_documents_library</c> entry. One row per (version, library
/// entry) pair — uniqueness enforced by the DB
/// <c>UQ_rtrd_per_version</c> constraint.
/// </summary>
public sealed class RequestTypeRequiredDocument
{
    public int Id { get; init; }
    public int RequestTypeVersionId { get; init; }
    public int RequiredDocumentLibraryId { get; init; }

    /// <summary>
    /// Whether the submitter MUST attach a document of this type when
    /// submitting a request bound to this version. When false the
    /// upload slot is optional.
    /// </summary>
    public bool Required { get; init; }
}
