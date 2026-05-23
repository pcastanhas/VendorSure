namespace VendorSure.Domain.Identity;

/// <summary>
/// A user of the VendorSure system. Backed by an Entra (Azure AD) identity;
/// <see cref="Entraid"/> is the durable external identifier we match against
/// when authenticating an incoming request.
/// </summary>
public sealed class User
{
    public int Id { get; init; }
    public string Entraid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int GroupId { get; init; }
    public bool IsAdmin { get; init; }
    public bool IsActive { get; init; }
}
