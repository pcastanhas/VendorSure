namespace VendorSure.Domain.Documents;

/// <summary>
/// A type of document that Request Types can require submitters to upload.
/// Sits in <c>required_documents_library</c> — the catalog from which
/// Request Type versions pick. The library is a slowly-changing dimension
/// of metadata; the per-Request-Type-version selections are held in the
/// <c>request_type_required_documents</c> junction.
/// </summary>
public sealed class DocumentType
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The file extension the UI hints submitters to provide (e.g. "pdf",
    /// "docx"). The schema notes this is display-hint only — no enforcement
    /// at upload time.
    /// </summary>
    public string? FileTypeRequired { get; init; }
}
