using VendorSure.Domain.Identity;

namespace VendorSure.Services.Identity;

/// <summary>
/// Outcome of <see cref="IUserGroupRepository.UpdateAsync"/>. Encoded as an
/// enum rather than via exceptions because each value represents an expected
/// business outcome the caller should react to (typically by surfacing a
/// snackbar of different severity).
/// </summary>
public enum UpdateUserGroupResult
{
    /// <summary>The row was found and updated.</summary>
    Updated,

    /// <summary>No row with that id exists.</summary>
    NotFound,

    /// <summary>
    /// The update would have set <c>IsActive = false</c>, but the group has
    /// one or more users still assigned to it. Reassign or deactivate those
    /// users first.
    /// </summary>
    RejectedHasUsers,
}

/// <summary>
/// CRUD operations on the <c>user_groups</c> table.
/// </summary>
/// <remarks>
/// No hard delete by design — see <see cref="UserGroup"/>. To retire a
/// group, call <see cref="UpdateAsync"/> with <c>IsActive = false</c>; the
/// repository refuses this when the group still has users assigned.
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
    /// Counts the users whose <c>group_id</c> points at the given group,
    /// regardless of whether those users are themselves active. Used by
    /// the admin UI to drive both the "assigned users" column and the
    /// disabled state of the IsActive toggle.
    /// </summary>
    Task<int> CountAssignedUsersAsync(int groupId, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new group and returns its assigned id. The
    /// <see cref="UserGroup.Id"/> of the passed argument is ignored.
    /// </summary>
    Task<int> CreateAsync(UserGroup group, CancellationToken ct = default);

    /// <summary>
    /// Updates the row whose id matches <see cref="UserGroup.Id"/>.
    /// </summary>
    /// <remarks>
    /// If the update would set <see cref="UserGroup.IsActive"/> to
    /// <c>false</c> and the group has users assigned, the row is left
    /// untouched and the result is
    /// <see cref="UpdateUserGroupResult.RejectedHasUsers"/>. All other field
    /// edits (name, permission flags) are allowed regardless of assigned
    /// users.
    /// </remarks>
    Task<UpdateUserGroupResult> UpdateAsync(UserGroup group, CancellationToken ct = default);
}
