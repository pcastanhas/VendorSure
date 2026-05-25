namespace VendorSure.Services.Documents;

/// <summary>
/// Thrown by <see cref="IDocumentStorage"/> when a filename violates the
/// safety rules: empty, contains a path separator (<c>/</c> or <c>\</c>),
/// contains <c>..</c>, contains a null byte, or exceeds 200 characters.
///
/// This is a programmer-error / malicious-input exception, not a
/// user-facing rejection. Filenames produced by the UI's file picker
/// will not naturally hit these conditions. The two user-facing
/// rejections (disallowed extension, oversized file) are returned as
/// <see cref="StoreDocumentOutcome"/> values instead.
/// </summary>
public sealed class InvalidDocumentFileNameException : Exception
{
    public string FileName { get; }
    public string Reason { get; }

    public InvalidDocumentFileNameException(string fileName, string reason)
        : base($"Filename '{fileName}' is invalid: {reason}.")
    {
        FileName = fileName;
        Reason = reason;
    }
}
