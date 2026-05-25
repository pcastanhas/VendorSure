using VendorSure.Domain.Ai;

namespace VendorSure.Services.Ai;

/// <summary>
/// Optional context keys passed to <see cref="IAiService.CompleteAsync"/>.
/// Any combination is allowed; all four nullable so the
/// <c>/test/ai</c> harness can supply none. The validation runner (6B.2)
/// supplies <c>RequestId</c> + <c>ValidationId</c>; workflow blocks
/// (Phase 7) supply the workflow keys.
/// </summary>
public sealed record AiCallContext(
    int? RequestId = null,
    int? WorkflowInstanceId = null,
    int? WorkflowNodeId = null,
    int? ValidationId = null,
    int? PromptVersionId = null);

/// <summary>
/// Result of a successful <see cref="IAiService.CompleteAsync"/> call.
/// On non-success, <see cref="AiCallFailedException"/> is thrown instead;
/// it carries the <c>ai_usage</c> id of the row that was written for the
/// failed call so the caller can correlate.
/// </summary>
public sealed record AiCallResult(
    int AiUsageId,
    string Model,
    string ResponseText,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    int LatencyMs);

/// <summary>
/// Thrown by <see cref="IAiService.CompleteAsync"/> when
/// <c>AI.Disabled</c> is set to <c>1</c>. No API call is made and no
/// <c>ai_usage</c> row is written — the budget worker sets the flag
/// precisely to prevent further spend, and the AI service is the
/// gatekeeper.
/// </summary>
public sealed class AiDisabledException : Exception
{
    public AiDisabledException()
        : base("AI is disabled (AI.Disabled setting is 1). No call was made.")
    {
    }
}

/// <summary>
/// Thrown by <see cref="IAiService.CompleteAsync"/> when no
/// <c>model_pricing</c> row covers the configured model as of "now". Per
/// the schema's policy, silent zero-cost is worse than a loud failure:
/// the caller must seed a pricing row before the model can be used.
/// </summary>
public sealed class ModelPricingMissingException : Exception
{
    public string Model { get; }

    public ModelPricingMissingException(string model)
        : base($"No model_pricing row covers model '{model}' as of today. " +
               $"Seed a row in dbo.model_pricing before calling the AI service.")
    {
        Model = model;
    }
}

/// <summary>
/// Thrown by <see cref="IAiService.CompleteAsync"/> when the Claude
/// call fails (HTTP error, timeout, SDK exception). The
/// <c>ai_usage</c> row has already been written with the appropriate
/// status (<see cref="AiUsageStatus.Error"/> /
/// <see cref="AiUsageStatus.Timeout"/>); <see cref="AiUsageId"/> points
/// at it for downstream correlation.
/// </summary>
public sealed class AiCallFailedException : Exception
{
    public int AiUsageId { get; }
    public AiUsageStatus Status { get; }

    public AiCallFailedException(int aiUsageId, AiUsageStatus status, string message, Exception? inner = null)
        : base(message, inner)
    {
        AiUsageId = aiUsageId;
        Status = status;
    }
}

/// <summary>
/// The AI service: a one-shot prompt-in / response-out wrapper around the
/// Anthropic SDK. Every call writes an <c>ai_usage</c> row before returning.
/// Doesn't interpret the response — callers do that (the validation runner
/// expects PASS/FAIL JSON; future block callers will expect their own shapes).
/// </summary>
public interface IAiService
{
    /// <summary>
    /// Sends <paramref name="prompt"/> as a single-message user turn to the
    /// configured model and returns the textual response plus token / cost
    /// bookkeeping. Throws <see cref="AiDisabledException"/> if the kill
    /// switch is on, <see cref="ModelPricingMissingException"/> if pricing
    /// isn't seeded for the model, or <see cref="AiCallFailedException"/>
    /// on transport / API error or timeout.
    /// </summary>
    Task<AiCallResult> CompleteAsync(
        string prompt,
        AiCallContext context,
        CancellationToken ct = default);
}
