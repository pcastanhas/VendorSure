using VendorSure.Domain.Identity;

namespace VendorSure.Services.Identity;

/// <summary>
/// Outcome of <see cref="IUserRepository.CreateAsync"/>.
/// </summary>
public enum CreateUserOutcome
{
    /// <summary>The user was inserted; <see cref="CreateUserResult.Id"/> is its assigned id.</summary>
    Created,

    /// <summary>The chosen <see cref="User.GroupId"/> points at an inactive group.</summary>
    RejectedInactiveGroup,

    /// <summary>Another user already has the same <see cref="User.Entraid"/>.</summary>
    RejectedEntraidConflict,
}

/// <summary>
/// Result of a create. <see cref="Id"/> is non-null iff
/// <see cref="Outcome"/> == <see cref="CreateUserOutcome.Created"/>.
/// </summary>
public sealed record CreateUserResult(CreateUserOutcome Outcome, int? Id);

/// <summary>
/// Outcome of <see cref="IUserRepository.UpdateAsync"/>.
/// </summary>
public enum UpdateUserResult
{
    /// <summary>The row was found and updated.</summary>
    Updated,

    /// <summary>No row with that id exists.</summary>
    NotFound,

    /// <summary>The update would have pointed the user at an inactive group.</summary>
    RejectedInactiveGroup,

    /// <summary>Another user already has the proposed <see cref="User.Entraid"/>.</summary>
    RejectedEntraidConflict,
}

/// <summary>
/// CRUD operations on the <c>users</c> table.
/// </summary>
/// <remarks>
/// No hard delete by design. To retire a user, call <see cref="UpdateAsync"/>
/// with <c>IsActive = false</c>. (Unlike user_groups, the reverse-direction
/// invariant — 'no user pointing at an inactive group' — is enforced on
/// every create / update so an inactive user record remains valid even if
/// its group is later deactivated, because by that point the deactivation
/// would have been blocked by <see cref="IUserGroupRepository"/>.)
/// </remarks>
public interface IUserRepository
{
    /// <summary>
    /// Returns all users, ordered by name. Includes inactive users; the
    /// admin UI decides whether to filter them.
    /// </summary>
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the user with the given id, or <c>null</c> if no row has
    /// that id.
    /// </summary>
    Task<User?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Returns the user whose <c>entraid</c> column matches the given OID,
    /// or <c>null</c> if no such user exists.
    /// </summary>
    Task<User?> GetByEntraidAsync(string entraid, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new user. The <see cref="User.Id"/> of the passed argument
    /// is ignored. Fails fast with a specific outcome if the chosen group
    /// is inactive or the entraid is already taken.
    /// </summary>
    Task<CreateUserResult> CreateAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// Updates the row whose id matches <see cref="User.Id"/>. Fails with a
    /// specific outcome if the target group is inactive or the proposed
    /// entraid collides with a different user (changing a user's entraid to
    /// match its own current value is allowed).
    /// </summary>
    Task<UpdateUserResult> UpdateAsync(User user, CancellationToken ct = default);
}
