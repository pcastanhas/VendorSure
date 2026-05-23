namespace VendorSure.Domain.RequestTypes;

/// <summary>
/// Junction row attaching a required document (from
/// <c>request_type_required_documents</c>, not the library itself) to a
/// validation. Zero rows for a given validation means it runs on
/// metadata + submitter notes only — no document content fed in.
/// </summary>
/// <remarks>
/// Critical invariant the schema doesn't enforce: both endpoints
/// (validation and required-doc) must belong to the same Request Type
/// version. The schema's FKs only enforce existence — same-version is
/// enforced by <see cref="VendorSure.Services.RequestTypes.IRequestTypeValidationDocumentRepository"/>.
/// </remarks>
public sealed class RequestTypeValidationDocument
{
    public int Id { get; init; }
    public int RequestTypeValidationId { get; init; }
    public int RequestTypeRequiredDocumentId { get; init; }
}
