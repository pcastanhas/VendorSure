namespace VendorSure.Domain.RequestTypes;

/// <summary>
/// An immutable bundle of the things that can change about a Request Type:
/// required documents, validations, workflow choices, and the workflow-
/// selection prompt. Once a version is placed In Service it is locked;
/// further edits require a new version.
/// </summary>
/// <remarks>
/// Snapshot semantics: a request that starts under v1 finishes under v1,
/// even after v2 is placed in service. Each running request is bound to
/// its Request Type version for life.
/// </remarks>
public sealed class RequestTypeVersion
{
    public int Id { get; init; }
    public int RequestTypeId { get; init; }

    /// <summary>
    /// Sequential per-type version number, 1-based. Unique on
    /// (request_type_id, version) by DB constraint.
    /// </summary>
    public int Version { get; init; }

    /// <summary>Optional display label for this version. Free text.</summary>
    public string? Name { get; init; }

    public RequestState RequestState { get; init; }

    /// <summary>
    /// The prompt the AI triage layer uses to pick a workflow among the
    /// candidates available for this version. Free text; may be null in
    /// Draft.
    /// </summary>
    public string? WorkflowSelectionPrompt { get; init; }

    /// <summary>When the row was inserted. Set by DB default at insert.</summary>
    public DateTime CreatedTs { get; init; }

    /// <summary>When the version transitioned Draft → In Service. Null until then.</summary>
    public DateTime? PlacedInServiceTs { get; init; }

    /// <summary>When the version transitioned In Service → Superseded. Null until then.</summary>
    public DateTime? SupersededTs { get; init; }
}
