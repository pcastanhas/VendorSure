namespace VendorSure.Domain.Ai;

/// <summary>
/// A row in the <c>model_pricing</c> table — per-model token rates with
/// effective-date history. The AI service looks up the currently-effective
/// row (<see cref="EffectiveTo"/> is <c>null</c> or in the future) at call
/// time and uses its rates to compute <see cref="AiUsage.CostUsd"/>.
/// </summary>
/// <remarks>
/// Rate changes are append-only: stamp the current row with
/// <see cref="EffectiveTo"/> = today, insert a new row with
/// <see cref="EffectiveFrom"/> = today and <see cref="EffectiveTo"/> = null.
/// That keeps historical <c>ai_usage</c> rows accurate to the rate that was
/// actually in effect when each call ran. See <c>data-model.sql §15</c>.
/// </remarks>
public sealed class ModelPricing
{
    public int Id { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public decimal InputPerMillionUsd { get; init; }
    public decimal OutputPerMillionUsd { get; init; }
}
