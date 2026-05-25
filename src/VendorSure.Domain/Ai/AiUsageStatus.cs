namespace VendorSure.Domain.Ai;

/// <summary>
/// Outcome status stamped on every <see cref="AiUsage"/> row. The
/// repository maps this to the single-character <c>status</c> column
/// (S/E/T) at insert/read time.
/// </summary>
public enum AiUsageStatus
{
    /// <summary>'S' — call returned a usable response.</summary>
    Success,

    /// <summary>'E' — call returned an error response (HTTP error,
    /// SDK exception, malformed body). Response is unusable.</summary>
    Error,

    /// <summary>'T' — call timed out before any response.</summary>
    Timeout,
}
