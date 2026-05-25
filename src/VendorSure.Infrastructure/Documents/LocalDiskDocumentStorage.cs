using Microsoft.Extensions.Logging;
using VendorSure.Services.Configuration;
using VendorSure.Services.Documents;

namespace VendorSure.Infrastructure.Documents;

/// <summary>
/// <see cref="IDocumentStorage"/> impl that writes files to a local /
/// network filesystem path. The v1 production target is a NAS UNC path
/// configured by <c>Storage.BasePath</c>; the impl is filesystem-agnostic
/// otherwise.
/// </summary>
/// <remarks>
/// Layout: <c>{BasePath}/{requestId}/{fileName}</c>.
///
/// Settings are read on every operation (not cached) so admin-panel edits
/// take effect immediately. Settings reads are a single indexed lookup;
/// the overhead is negligible against the filesystem IO itself.
///
/// Filename safety is enforced in <see cref="ValidateFileName"/> and is
/// the same check on store and retrieve. The check refuses anything that
/// could escape the request's directory (<c>..</c>, path separators) and
/// caps length at 200 chars — well under NTFS's 255-char per-segment
/// limit and Windows' default 260-char MAX_PATH once basepath +
/// requestId + separators are added.
/// </remarks>
internal sealed class LocalDiskDocumentStorage : IDocumentStorage
{
    internal const int MaxFileNameLength = 200;

    private const string BasePathKey = "Storage.BasePath";
    private const string AllowedExtensionsKey = "Storage.AllowedFileExtensions";
    private const string MaxFileSizeBytesKey = "Storage.MaxFileSizeBytes";

    private readonly ISettingsRepository _settings;
    private readonly ILogger<LocalDiskDocumentStorage> _logger;

    public LocalDiskDocumentStorage(
        ISettingsRepository settings,
        ILogger<LocalDiskDocumentStorage> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<StoreDocumentResult> StoreAsync(
        int requestId,
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        ValidateFileName(fileName);

        if (!content.CanSeek)
        {
            throw new ArgumentException(
                "Stream must be seekable so its length can be checked before writing.",
                nameof(content));
        }

        var allowed = await ReadAllowedExtensionsAsync(ct);
        var extension = GetExtensionLowercaseNoDot(fileName);
        if (!allowed.Contains(extension))
        {
            _logger.LogInformation(
                "Rejected upload for request {RequestId}: extension '{Extension}' not in allow-list ({AllowList}).",
                requestId, extension, string.Join(",", allowed));
            return new StoreDocumentResult(
                StoreDocumentOutcome.RejectedDisallowedExtension,
                SizeBytes: null,
                Extension: extension);
        }

        var maxBytes = await ReadMaxFileSizeBytesAsync(ct);
        var size = content.Length;
        if (size > maxBytes)
        {
            _logger.LogInformation(
                "Rejected upload for request {RequestId}: size {SizeBytes} exceeds cap {MaxBytes}.",
                requestId, size, maxBytes);
            return new StoreDocumentResult(
                StoreDocumentOutcome.RejectedFileTooLarge,
                SizeBytes: size,
                Extension: null);
        }

        var basePath = await ReadBasePathAsync(ct);
        var requestDirectory = Path.Combine(basePath, requestId.ToString());
        Directory.CreateDirectory(requestDirectory);

        var destinationPath = Path.Combine(requestDirectory, fileName);

        // Reset to start in case the caller already inspected the stream
        // (Length above doesn't move the position, but other paths might).
        if (content.Position != 0)
        {
            content.Position = 0;
        }

        await using (var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            await content.CopyToAsync(destination, ct);
        }

        _logger.LogInformation(
            "Stored {SizeBytes} bytes for request {RequestId} at '{Path}'.",
            size, requestId, destinationPath);

        return new StoreDocumentResult(
            StoreDocumentOutcome.Stored,
            SizeBytes: size,
            Extension: extension);
    }

    public async Task<RetrieveDocumentResult> RetrieveAsync(
        int requestId,
        string fileName,
        CancellationToken ct = default)
    {
        ValidateFileName(fileName);

        var basePath = await ReadBasePathAsync(ct);
        var sourcePath = Path.Combine(basePath, requestId.ToString(), fileName);

        if (!File.Exists(sourcePath))
        {
            return new RetrieveDocumentResult(RetrieveDocumentOutcome.NotFound, Content: null);
        }

        // Caller owns the returned stream; opened async so reads don't block.
        var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        return new RetrieveDocumentResult(RetrieveDocumentOutcome.Retrieved, stream);
    }

    public async Task DeleteAllForRequestAsync(int requestId, CancellationToken ct = default)
    {
        var basePath = await ReadBasePathAsync(ct);
        var requestDirectory = Path.Combine(basePath, requestId.ToString());

        if (!Directory.Exists(requestDirectory))
        {
            return;
        }

        Directory.Delete(requestDirectory, recursive: true);

        _logger.LogInformation(
            "Deleted storage directory for request {RequestId} at '{Path}'.",
            requestId, requestDirectory);
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            throw new InvalidDocumentFileNameException(fileName ?? string.Empty, "empty");
        }

        if (fileName.Length > MaxFileNameLength)
        {
            throw new InvalidDocumentFileNameException(
                fileName,
                $"exceeds maximum length of {MaxFileNameLength} characters");
        }

        if (fileName.Contains('\0'))
        {
            throw new InvalidDocumentFileNameException(fileName, "contains a null byte");
        }

        if (fileName.Contains('/') || fileName.Contains('\\'))
        {
            throw new InvalidDocumentFileNameException(fileName, "contains a path separator");
        }

        if (fileName.Contains(".."))
        {
            throw new InvalidDocumentFileNameException(fileName, "contains '..'");
        }
    }

    private static string GetExtensionLowercaseNoDot(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return string.Empty;
        }
        return extension.TrimStart('.').ToLowerInvariant();
    }

    private async Task<string> ReadBasePathAsync(CancellationToken ct)
    {
        var setting = await _settings.GetByKeyAsync(BasePathKey, ct);
        var value = setting?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required setting '{BasePathKey}' is missing or empty.");
        }
        return value;
    }

    private async Task<HashSet<string>> ReadAllowedExtensionsAsync(CancellationToken ct)
    {
        var setting = await _settings.GetByKeyAsync(AllowedExtensionsKey, ct);
        var raw = setting?.Value ?? string.Empty;

        var parsed = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0);

        return new HashSet<string>(parsed, StringComparer.Ordinal);
    }

    private async Task<long> ReadMaxFileSizeBytesAsync(CancellationToken ct)
    {
        var setting = await _settings.GetByKeyAsync(MaxFileSizeBytesKey, ct);
        var raw = setting?.Value;
        if (string.IsNullOrWhiteSpace(raw) || !long.TryParse(raw, out var bytes) || bytes <= 0)
        {
            throw new InvalidOperationException(
                $"Required setting '{MaxFileSizeBytesKey}' is missing, non-numeric, or non-positive (got '{raw}').");
        }
        return bytes;
    }
}
