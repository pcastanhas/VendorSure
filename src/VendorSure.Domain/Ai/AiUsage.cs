namespace VendorSure.Domain.Ai;

/// <summary>
/// A row in the <c>ai_usage</c> table — one row per Claude API call,
/// successful or not. Captures both bookkeeping (tokens, cost, latency)
/// and a verbatim audit trail (<see cref="InputJson"/> /
/// <see cref="OutputJson"/>) for the mineable-corpus goal.
/// </summary>
/// <remarks>
/// The four context keys (<see cref="RequestId"/>,
/// <see cref="WorkflowInstanceId"/>, <see cref="WorkflowNodeId"/>,
/// <see cref="ValidationId"/>) are all nullable; their combinations
/// identify the call kind. See <c>data-model.sql §14</c>.
/// </remarks>
public sealed class AiUsage
{
    public int Id { get; init; }

    public int? RequestId { get; init; }
    public int? WorkflowInstanceId { get; init; }
    public int? WorkflowNodeId { get; init; }
    public int? ValidationId { get; init; }

    public DateTime CallTs { get; init; }
    public string Model { get; init; } = string.Empty;
    public int? PromptVersionId { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public int LatencyMs { get; init; }
    public AiUsageStatus Status { get; init; }

    public string InputJson { get; init; } = string.Empty;
    public string? OutputJson { get; init; }
    public string? ErrorText { get; init; }
}
