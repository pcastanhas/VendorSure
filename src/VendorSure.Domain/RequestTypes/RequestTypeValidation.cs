namespace VendorSure.Domain.RequestTypes;

/// <summary>
/// One AI-driven check on submissions of a Request Type version. Carries
/// a human-readable description, the prompt the AI service sends, and a
/// per-version execution order.
/// </summary>
/// <remarks>
/// The ordering is enforced by the DB's <c>UQ_request_type_validations_order</c>
/// constraint — distinct slots per version. Reordering existing validations
/// is intentionally not exposed by the repository in Chunk 3; it'll be
/// added when a UI surface needs it (Chunk 7 candidate).
/// </remarks>
public sealed class RequestTypeValidation
{
    public int Id { get; init; }
    public int RequestTypeVersionId { get; init; }
    public string Description { get; init; } = string.Empty;
    public string AiPrompt { get; init; } = string.Empty;

    /// <summary>
    /// Sequential per-version slot, 1-based, ordered by the order the
    /// AI service runs the validations during submission triage.
    /// </summary>
    public int ExecutionOrder { get; init; }
}
