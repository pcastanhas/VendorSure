using VendorSure.Domain.Ai;

namespace VendorSure.Services.Ai;

/// <summary>
/// Reads <c>model_pricing</c>. The AI service looks up the currently-effective
/// row for a model before each Claude call, both to compute cost and to
/// refuse the call if no pricing is on record (silent zero-cost is worse
/// than a loud failure — see <c>data-model.sql §15</c>).
/// </summary>
public interface IModelPricingRepository
{
    /// <summary>
    /// Returns the row whose <c>effective_from &lt;= </c><paramref name="asOf"/>
    /// and (<c>effective_to</c> IS NULL OR <c>effective_to &gt; </c><paramref name="asOf"/>)
    /// for the given model, or <c>null</c> if no such row exists.
    /// </summary>
    Task<ModelPricing?> GetCurrentForModelAsync(
        string modelName,
        DateOnly asOf,
        CancellationToken ct = default);
}
