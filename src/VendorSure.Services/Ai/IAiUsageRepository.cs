using VendorSure.Domain.Ai;

namespace VendorSure.Services.Ai;

/// <summary>
/// Writes <c>ai_usage</c> rows. The AI service inserts one row per Claude
/// call, regardless of outcome (success / error / timeout). The repo's
/// surface is intentionally write-only for now — queries against
/// <c>ai_usage</c> will be added when the budget polling worker and the
/// reporting pages need them.
/// </summary>
public interface IAiUsageRepository
{
    /// <summary>
    /// Inserts a new <c>ai_usage</c> row and returns the generated id.
    /// </summary>
    Task<int> InsertAsync(AiUsage row, CancellationToken ct = default);
}
