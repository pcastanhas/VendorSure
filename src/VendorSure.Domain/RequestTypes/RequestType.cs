namespace VendorSure.Domain.RequestTypes;

/// <summary>
/// The logical container for a kind of vendor request (e.g. "New Vendor",
/// "Vendor Bank Change"). Almost nothing lives here — see
/// <see cref="RequestTypeVersion"/> for the immutable bundle of required
/// docs, validations, prompts, and workflow choices.
/// </summary>
public sealed class RequestType
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// If true, submitters must enter free-form explanation text alongside
    /// document uploads when submitting a request of this type.
    /// </summary>
    public bool IsExplanationRequired { get; init; }

    /// <summary>
    /// Whether this request type is currently offered to new submissions.
    /// Independent of any individual version's state — an inactive type can
    /// still have Superseded versions referenced by in-flight requests.
    /// </summary>
    public bool IsActive { get; init; }
}
