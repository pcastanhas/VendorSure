namespace VendorSure.Services.Documents;

/// <summary>Outcome of <see cref="IDocumentStorage.StoreAsync"/>.</summary>
public enum StoreDocumentOutcome
{
    /// <summary>The file was written successfully.</summary>
    Stored,

    /// <summary>
    /// The stream's length exceeds the <c>Storage.MaxFileSizeBytes</c>
    /// setting. <see cref="StoreDocumentResult.SizeBytes"/> carries the
    /// actual size for surfacing to the submitter.
    /// </summary>
    RejectedFileTooLarge,

    /// <summary>
    /// The filename's extension is not in the
    /// <c>Storage.AllowedFileExtensions</c> setting.
    /// <see cref="StoreDocumentResult.Extension"/> carries the offending
    /// extension (lowercase, no dot) for surfacing to the submitter.
    /// </summary>
    RejectedDisallowedExtension,
}

/// <summary>
/// Result of <see cref="IDocumentStorage.StoreAsync"/>. <see cref="SizeBytes"/>
/// is populated only on <see cref="StoreDocumentOutcome.RejectedFileTooLarge"/>;
/// <see cref="Extension"/> is populated only on
/// <see cref="StoreDocumentOutcome.RejectedDisallowedExtension"/>.
/// </summary>
public sealed record StoreDocumentResult(
    StoreDocumentOutcome Outcome,
    long? SizeBytes,
    string? Extension);

/// <summary>Outcome of <see cref="IDocumentStorage.RetrieveAsync"/>.</summary>
public enum RetrieveDocumentOutcome
{
    /// <summary>The file was found and opened for reading.</summary>
    Retrieved,

    /// <summary>No file with the given name exists under the request's directory.</summary>
    NotFound,
}

/// <summary>
/// Result of <see cref="IDocumentStorage.RetrieveAsync"/>. On
/// <see cref="RetrieveDocumentOutcome.Retrieved"/>, <see cref="Content"/> is
/// non-null and the caller owns it (must be disposed). On
/// <see cref="RetrieveDocumentOutcome.NotFound"/>, <see cref="Content"/> is null.
/// </summary>
public sealed record RetrieveDocumentResult(
    RetrieveDocumentOutcome Outcome,
    Stream? Content);

/// <summary>
/// Storage abstraction for submitter-uploaded request documents. The v1 impl
/// (<c>LocalDiskDocumentStorage</c>) writes to a NAS path configured by
/// <c>Storage.BasePath</c>; future impls may target blob storage. The
/// interface is deliberately small — three operations are all the submission
/// portal and re-submit flow need.
/// </summary>
/// <remarks>
/// Files are stored under <c>{BasePath}/{requestId}/{fileName}</c>. The
/// <c>requestId</c> partitioning isolates submissions from each other and
/// makes <see cref="DeleteAllForRequestAsync"/> a directory-level operation.
///
/// Filename safety (no path separators, no <c>..</c>, no null bytes, length
/// cap of 200 chars) is enforced as a programmer-error condition: callers
/// are expected to pass already-validated filenames from the UI's file
/// picker, and a violation throws <see cref="InvalidDocumentFileNameException"/>
/// rather than returning a soft outcome. The two soft rejections —
/// disallowed extension and oversized file — are user-facing and modelled
/// as outcomes on <see cref="StoreDocumentResult"/>.
///
/// Same-name uploads within one request are an overwrite. The re-submit
/// flow in 6C wipes the whole request directory first, so within-request
/// name collisions shouldn't arise in normal use; overwrite is the safe
/// default for the corner case.
/// </remarks>
public interface IDocumentStorage
{
    /// <summary>
    /// Writes <paramref name="content"/> to <c>{BasePath}/{requestId}/{fileName}</c>,
    /// creating the request directory if needed. Validates the filename's
    /// extension against <c>Storage.AllowedFileExtensions</c> and the
    /// stream's length against <c>Storage.MaxFileSizeBytes</c> before
    /// writing. <paramref name="content"/> must be seekable so its length
    /// can be checked up front.
    /// </summary>
    /// <exception cref="InvalidDocumentFileNameException">
    /// <paramref name="fileName"/> is empty, contains path separators, contains
    /// <c>..</c>, contains a null byte, or exceeds 200 characters.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="content"/> is not seekable.
    /// </exception>
    Task<StoreDocumentResult> StoreAsync(
        int requestId,
        string fileName,
        Stream content,
        CancellationToken ct = default);

    /// <summary>
    /// Opens <c>{BasePath}/{requestId}/{fileName}</c> for reading. On
    /// success the caller owns the returned stream and must dispose it.
    /// </summary>
    /// <exception cref="InvalidDocumentFileNameException">
    /// <paramref name="fileName"/> fails the same filename safety rules
    /// as <see cref="StoreAsync"/>.
    /// </exception>
    Task<RetrieveDocumentResult> RetrieveAsync(
        int requestId,
        string fileName,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the request's directory and everything in it. No-op if the
    /// directory doesn't exist (idempotent — re-submit can call this
    /// regardless of prior state).
    /// </summary>
    Task DeleteAllForRequestAsync(int requestId, CancellationToken ct = default);
}
