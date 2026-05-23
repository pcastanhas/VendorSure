using VendorSure.Domain.Identity;

namespace VendorSure.Services.Identity;

/// <summary>
/// CRUD operations on the <c>user_groups</c> table.
/// </summary>
/// <remarks>
/// No hard delete by design — see <see cref="UserGroup"/>. To retire a
/// group, call <see cref="UpdateAsync"/> with <c>IsActive = false</c>.
/// </remarks>
public interface IUserGroupRepository
{
    /// <summary>
    /// Returns all groups, ordered by name. Includes inactive groups; the
    /// admin UI decides whether to filter them.
    /// </summary>
    Task<IReadOnlyList<UserGroup>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the group with the given id, or <c>null</c> if no row has
    /// that id.
    /// </summary>
    Task<UserGroup?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new group and returns its assigned id. The
    /// <see cref="UserGroup.Id"/> of the passed argument is ignored.
    /// </summary>
    Task<int> CreateAsync(UserGroup group, CancellationToken ct = default);

    /// <summary>
    /// Updates the row whose id matches <see cref="UserGroup.Id"/>. Returns
    /// <c>true</c> if a row was updated, <c>false</c> if no row with that
    /// id exists.
    /// </summary>
    Task<bool> UpdateAsync(UserGroup group, CancellationToken ct = default);
}
