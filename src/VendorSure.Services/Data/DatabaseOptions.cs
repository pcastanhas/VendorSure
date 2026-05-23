namespace VendorSure.Services.Data;

/// <summary>
/// Strongly-typed binding for the <c>Database</c> configuration section.
/// </summary>
/// <remarks>
/// Populated from <c>appsettings.json</c> via the standard
/// <c>IOptions&lt;T&gt;</c> pattern. The connection string itself lives under
/// <c>ConnectionStrings:VenSure</c> in configuration; the
/// <see cref="ConnectionStringName"/> property tells the factory which entry
/// to read so we don't hard-code the name.
/// </remarks>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// The key in <c>ConnectionStrings</c> that holds the live connection
    /// string. Defaults to <c>VenSure</c>.
    /// </summary>
    public string ConnectionStringName { get; init; } = "VenSure";
}
