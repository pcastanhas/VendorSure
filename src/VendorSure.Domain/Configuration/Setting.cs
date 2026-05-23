namespace VendorSure.Domain.Configuration;

/// <summary>
/// A row from the <c>settings</c> table — system-wide configuration the
/// admin panel can edit at runtime.
/// </summary>
/// <remarks>
/// Rows are seeded by <c>data-model.sql §16</c>. The application looks
/// rows up by <see cref="Key"/>; the admin UI edits <see cref="Value"/>.
/// Adding or removing rows is a code-and-schema change, not a runtime
/// concern, so the repository deliberately has no Insert / Delete.
/// </remarks>
public sealed class Setting
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Required { get; init; }
    public bool Sensitive { get; init; }
    public string? Value { get; init; }
}
