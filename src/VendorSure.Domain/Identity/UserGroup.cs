namespace VendorSure.Domain.Identity;

/// <summary>
/// A grouping of users that shares permission flags. Every <see cref="User"/>
/// belongs to exactly one group; the group's flags decide what actions the
/// user's <see cref="User.IsAdmin"/> flag plus group membership allow.
/// </summary>
/// <remarks>
/// There is no hard delete. To retire a group, set <see cref="IsActive"/> to
/// <c>false</c>. The schema has no FK cascade behaviour, and hard-deleting a
/// group that any <see cref="User"/> still references would break referential
/// integrity.
/// </remarks>
public sealed class UserGroup
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool CanRestartWorkflow { get; init; }
    public bool CanChangeWorkflow { get; init; }
    public bool CanSubmitRequests { get; init; }
}
