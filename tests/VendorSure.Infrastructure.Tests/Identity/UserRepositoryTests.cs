using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Identity;
using VendorSure.Services.Data;
using VendorSure.Services.Identity;

namespace VendorSure.Infrastructure.Tests.Identity;

/// <summary>
/// Integration tests for the user repository. Talks to the dev DB.
/// Each test creates its own _test_-prefixed user_groups + users rows and
/// hard-deletes them in <c>try/finally</c> (users first, then groups, to
/// satisfy the FK). Stray rows from a mid-flight crash: see BUILD.md for
/// the cleanup query.
/// </summary>
public sealed class UserRepositoryTests : IClassFixture<InfrastructureTestFixture>
{
    private readonly IUserRepository _users;
    private readonly IUserGroupRepository _groups;
    private readonly IDbConnectionFactory _connectionFactory;

    public UserRepositoryTests(InfrastructureTestFixture fixture)
    {
        _users = fixture.ServiceProvider.GetRequiredService<IUserRepository>();
        _groups = fixture.ServiceProvider.GetRequiredService<IUserGroupRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task CreateAsync_returns_new_id_and_persists_fields()
    {
        var groupId = await CreateActiveTestGroupAsync();
        var user = NewTestUser(groupId);
        int newUserId = 0;
        try
        {
            var result = await _users.CreateAsync(user);
            Assert.Equal(CreateUserOutcome.Created, result.Outcome);
            Assert.NotNull(result.Id);
            newUserId = result.Id!.Value;

            var fetched = await _users.GetByIdAsync(newUserId);
            Assert.NotNull(fetched);
            Assert.Equal(user.Entraid, fetched!.Entraid);
            Assert.Equal(user.Name, fetched.Name);
            Assert.Equal(user.GroupId, fetched.GroupId);
            Assert.Equal(user.IsAdmin, fetched.IsAdmin);
            Assert.Equal(user.IsActive, fetched.IsActive);
        }
        finally
        {
            if (newUserId > 0) await DeleteUserAsync(newUserId);
            await DeleteGroupAsync(groupId);
        }
    }

    [Fact]
    public async Task CreateAsync_rejects_inactive_group()
    {
        var groupId = await CreateActiveTestGroupAsync();
        // Deactivate the group (legal because it has no users yet).
        var group = await _groups.GetByIdAsync(groupId);
        Assert.NotNull(group);
        var deactivated = new UserGroup
        {
            Id = group!.Id,
            Name = group.Name,
            IsActive = false,
            CanRestartWorkflow = group.CanRestartWorkflow,
            CanChangeWorkflow = group.CanChangeWorkflow,
            CanSubmitRequests = group.CanSubmitRequests,
        };
        var deactivateResult = await _groups.UpdateAsync(deactivated);
        Assert.Equal(UpdateUserGroupResult.Updated, deactivateResult);

        try
        {
            var user = NewTestUser(groupId);
            var result = await _users.CreateAsync(user);
            Assert.Equal(CreateUserOutcome.RejectedInactiveGroup, result.Outcome);
            Assert.Null(result.Id);
        }
        finally
        {
            await DeleteGroupAsync(groupId);
        }
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_entraid()
    {
        var groupId = await CreateActiveTestGroupAsync();
        var first = NewTestUser(groupId);
        int firstId = 0;
        try
        {
            var firstResult = await _users.CreateAsync(first);
            Assert.Equal(CreateUserOutcome.Created, firstResult.Outcome);
            firstId = firstResult.Id!.Value;

            var second = new User
            {
                Entraid = first.Entraid, // collision
                Name = first.Name + "_dupe",
                GroupId = groupId,
                IsAdmin = false,
                IsActive = true,
            };
            var secondResult = await _users.CreateAsync(second);
            Assert.Equal(CreateUserOutcome.RejectedEntraidConflict, secondResult.Outcome);
            Assert.Null(secondResult.Id);
        }
        finally
        {
            if (firstId > 0) await DeleteUserAsync(firstId);
            await DeleteGroupAsync(groupId);
        }
    }

    [Fact]
    public async Task ListWithGroupNamesAsync_returns_users_with_their_group_names()
    {
        var groupId = await CreateActiveTestGroupAsync();
        var groupName = (await _groups.GetByIdAsync(groupId))!.Name;
        var user = NewTestUser(groupId);
        int newId = 0;
        try
        {
            newId = (await _users.CreateAsync(user)).Id!.Value;

            var list = await _users.ListWithGroupNamesAsync();
            var item = list.SingleOrDefault(i => i.User.Id == newId);
            Assert.NotNull(item);
            Assert.Equal(user.Name, item!.User.Name);
            Assert.Equal(groupName, item.GroupName);
        }
        finally
        {
            if (newId > 0) await DeleteUserAsync(newId);
            await DeleteGroupAsync(groupId);
        }
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var result = await _users.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByEntraidAsync_returns_user_for_known_entraid()
    {
        var groupId = await CreateActiveTestGroupAsync();
        var user = NewTestUser(groupId);
        int newId = 0;
        try
        {
            var createResult = await _users.CreateAsync(user);
            newId = createResult.Id!.Value;

            var fetched = await _users.GetByEntraidAsync(user.Entraid);
            Assert.NotNull(fetched);
            Assert.Equal(newId, fetched!.Id);
        }
        finally
        {
            if (newId > 0) await DeleteUserAsync(newId);
            await DeleteGroupAsync(groupId);
        }
    }

    [Fact]
    public async Task GetAllAsync_includes_inserted_row()
    {
        var groupId = await CreateActiveTestGroupAsync();
        var user = NewTestUser(groupId);
        int newId = 0;
        try
        {
            var createResult = await _users.CreateAsync(user);
            newId = createResult.Id!.Value;

            var all = await _users.GetAllAsync();
            Assert.Contains(all, u => u.Id == newId && u.Entraid == user.Entraid);
        }
        finally
        {
            if (newId > 0) await DeleteUserAsync(newId);
            await DeleteGroupAsync(groupId);
        }
    }

    [Fact]
    public async Task UpdateAsync_round_trips_field_changes()
    {
        var groupId = await CreateActiveTestGroupAsync();
        var user = NewTestUser(groupId);
        int newId = 0;
        try
        {
            var createResult = await _users.CreateAsync(user);
            newId = createResult.Id!.Value;

            var updated = new User
            {
                Id = newId,
                Entraid = user.Entraid,            // unchanged
                Name = user.Name + "_renamed",
                GroupId = groupId,                 // unchanged
                IsAdmin = !user.IsAdmin,
                IsActive = false,
            };
            var result = await _users.UpdateAsync(updated);
            Assert.Equal(UpdateUserResult.Updated, result);

            var fetched = await _users.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.Equal(updated.Name, fetched!.Name);
            Assert.Equal(updated.IsAdmin, fetched.IsAdmin);
            Assert.False(fetched.IsActive);
        }
        finally
        {
            if (newId > 0) await DeleteUserAsync(newId);
            await DeleteGroupAsync(groupId);
        }
    }

    [Fact]
    public async Task UpdateAsync_returns_NotFound_for_unknown_id()
    {
        var groupId = await CreateActiveTestGroupAsync();
        try
        {
            var ghost = new User
            {
                Id = int.MaxValue - 1,
                Entraid = $"_test_ghost_{Guid.NewGuid():N}",
                Name = "_test_ghost",
                GroupId = groupId,
                IsAdmin = false,
                IsActive = true,
            };
            var result = await _users.UpdateAsync(ghost);
            Assert.Equal(UpdateUserResult.NotFound, result);
        }
        finally
        {
            await DeleteGroupAsync(groupId);
        }
    }

    [Fact]
    public async Task UpdateAsync_rejects_reassignment_to_inactive_group()
    {
        var activeGroupId = await CreateActiveTestGroupAsync();
        var inactiveGroupId = await CreateActiveTestGroupAsync();
        // Deactivate the second group before assigning anyone.
        var g = await _groups.GetByIdAsync(inactiveGroupId);
        await _groups.UpdateAsync(new UserGroup
        {
            Id = g!.Id, Name = g.Name, IsActive = false,
            CanRestartWorkflow = g.CanRestartWorkflow,
            CanChangeWorkflow = g.CanChangeWorkflow,
            CanSubmitRequests = g.CanSubmitRequests,
        });

        var user = NewTestUser(activeGroupId);
        int newId = 0;
        try
        {
            var createResult = await _users.CreateAsync(user);
            newId = createResult.Id!.Value;

            var reassignment = new User
            {
                Id = newId,
                Entraid = user.Entraid,
                Name = user.Name,
                GroupId = inactiveGroupId,
                IsAdmin = user.IsAdmin,
                IsActive = user.IsActive,
            };
            var result = await _users.UpdateAsync(reassignment);
            Assert.Equal(UpdateUserResult.RejectedInactiveGroup, result);
        }
        finally
        {
            if (newId > 0) await DeleteUserAsync(newId);
            await DeleteGroupAsync(activeGroupId);
            await DeleteGroupAsync(inactiveGroupId);
        }
    }

    [Fact]
    public async Task UpdateAsync_rejects_entraid_collision_with_another_user()
    {
        var groupId = await CreateActiveTestGroupAsync();
        var userA = NewTestUser(groupId);
        var userB = NewTestUser(groupId);
        int idA = 0;
        int idB = 0;
        try
        {
            idA = (await _users.CreateAsync(userA)).Id!.Value;
            idB = (await _users.CreateAsync(userB)).Id!.Value;

            // Try to set B's entraid to A's entraid.
            var collision = new User
            {
                Id = idB,
                Entraid = userA.Entraid,
                Name = userB.Name,
                GroupId = groupId,
                IsAdmin = userB.IsAdmin,
                IsActive = userB.IsActive,
            };
            var result = await _users.UpdateAsync(collision);
            Assert.Equal(UpdateUserResult.RejectedEntraidConflict, result);
        }
        finally
        {
            if (idA > 0) await DeleteUserAsync(idA);
            if (idB > 0) await DeleteUserAsync(idB);
            await DeleteGroupAsync(groupId);
        }
    }

    [Fact]
    public async Task UpdateAsync_allows_keeping_same_entraid()
    {
        // Regression: the conflict check must say 'does any OTHER user
        // have this entraid', not 'does any user have this entraid'.
        // Editing your own row while submitting the same entraid is
        // legal and the rule must not fire.
        var groupId = await CreateActiveTestGroupAsync();
        var user = NewTestUser(groupId);
        int newId = 0;
        try
        {
            newId = (await _users.CreateAsync(user)).Id!.Value;

            var edited = new User
            {
                Id = newId,
                Entraid = user.Entraid, // SAME entraid
                Name = user.Name + "_edited",
                GroupId = groupId,
                IsAdmin = user.IsAdmin,
                IsActive = user.IsActive,
            };
            var result = await _users.UpdateAsync(edited);
            Assert.Equal(UpdateUserResult.Updated, result);

            var fetched = await _users.GetByIdAsync(newId);
            Assert.Equal(edited.Name, fetched!.Name);
        }
        finally
        {
            if (newId > 0) await DeleteUserAsync(newId);
            await DeleteGroupAsync(groupId);
        }
    }

    // ---- helpers --------------------------------------------------------

    private async Task<int> CreateActiveTestGroupAsync()
    {
        return await _groups.CreateAsync(new UserGroup
        {
            Name = $"_test_grp_{Guid.NewGuid():N}",
            IsActive = true,
            CanRestartWorkflow = false,
            CanChangeWorkflow = false,
            CanSubmitRequests = true,
        });
    }

    private static User NewTestUser(int groupId) => new()
    {
        Entraid = $"_test_user_{Guid.NewGuid():N}",
        Name = $"_test_user_name_{Guid.NewGuid():N}",
        GroupId = groupId,
        IsAdmin = false,
        IsActive = true,
    };

    private async Task DeleteUserAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.users WHERE id = @id;",
            new { id });
    }

    private async Task DeleteGroupAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.user_groups WHERE id = @id;",
            new { id });
    }
}
