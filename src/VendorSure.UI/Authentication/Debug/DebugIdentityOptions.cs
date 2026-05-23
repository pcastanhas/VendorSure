// REMOVE-BEFORE-PROD: this entire file. The Debug.Identity options exist
// only to feed the Entra bypass shim. When real Entra auth lands, this
// class and its config section in appsettings.json both go away.

namespace VendorSure.UI.Authentication.Debug;

internal sealed class DebugIdentityOptions
{
    public const string SectionName = "Debug:Identity";

    /// <summary>
    /// When <c>true</c>, every request is authenticated as the user whose
    /// <c>entraid</c> matches <see cref="Entraid"/>. When <c>false</c>, the
    /// shim is inert and the default ASP.NET Core behaviour applies (which,
    /// until real Entra is wired, means unauthenticated requests).
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The OID (object ID) the shim stamps onto requests. Must match an
    /// existing row in the <c>users</c> table; if not, the shim logs a
    /// warning and authentication fails.
    /// </summary>
    public string Entraid { get; init; } = string.Empty;
}
