using VendorSure.Domain.Identity;

namespace VendorSure.Services.Identity;

/// <summary>
/// Reads and writes <see cref="User"/> records. Phase 1 / Chunk 4 only
/// needs the lookup-by-Entraid path; the full CRUD surface grows in
/// Phase 2 (Users admin page).
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// Returns the user whose <c>entraid</c> column matches the given OID,
    /// or <c>null</c> if no such user exists.
    /// </summary>
    Task<User?> GetByEntraidAsync(string entraid, CancellationToken ct = default);
}
