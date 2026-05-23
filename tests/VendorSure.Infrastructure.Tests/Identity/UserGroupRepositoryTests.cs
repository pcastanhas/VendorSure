using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Identity;
using VendorSure.Services.Data;
using VendorSure.Services.Identity;

namespace VendorSure.Infrastructure.Tests.Identity;

/// <summary>
/// Integration tests for the user-group repository. Talks to the dev DB.
/// Each test creates fresh row(s) with recognizable <c>_test_</c>-prefixed
/// names and hard-deletes them in a <c>try/finally</c>. Stray <c>_test_*</c>
/// rows are harmless leftovers from a mid-flight crash; cleanup query:
///   DELETE FROM dbo.users       WHERE entraid LIKE '_test_%';
///   DELETE FROM dbo.user_groups WHERE name    LIKE '_test_%';
/// </summary>
public sealed class UserGroupRepositoryTests : IClassFixture<InfrastructureTestFixture>
{
    private readonly IUserGroupRepository _repository;
    private readonly IDbConnectionFactory _connectionFactory;

    public UserGroupRepositoryTests(InfrastructureTestFixture fixture)
    {
        _repository = fixture.ServiceProvider.GetRequiredService<IUserGroupRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task CreateAsync_returns_new_id_and_persists_fields()
    {
        var group = NewTestGroup();
        int newId = 0;
        try
        {
            newId = await _repository.CreateAsync(group);
            Assert.True(newId > 0);

            var fetched = await _repository.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.Equal(group.Name, fetched!.Name);
            Assert.Equal(group.IsActive, fetched.IsActive);
            Assert.Equal(group.CanRestartWorkflow, fetched.CanRestartWorkflow);
            Assert.Equal(group.CanChangeWorkflow, fetched.CanChangeWorkflow);
            Assert.Equal(group.CanSubmitRequests, fetched.CanSubmitRequests);
        }
        finally
        {
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var result = await _repository.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllAsync_includes_inserted_row()
    {
        var group = NewTestGroup();
        int newId = 0;
        try
        {
            newId = await _repository.CreateAsync(group);

            var all = await _repository.GetAllAsync();
            Assert.Contains(all, g => g.Id == newId && g.Name == group.Name);
        }
        finally
        {
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_round_trips_field_changes_for_group_with_no_users()
    {
        var group = NewTestGroup();
        int newId = 0;
        try
        {
            newId = await _repository.CreateAsync(group);

            // No users assigned — the deactivation rule does not fire.
            var updated = new UserGroup
            {
                Id = newId,
                Name = group.Name + "_updated",
                IsActive = false,
                CanRestartWorkflow = !group.CanRestartWorkflow,
                CanChangeWorkflow = !group.CanChangeWorkflow,
                CanSubmitRequests = !group.CanSubmitRequests,
            };

            var result = await _repository.UpdateAsync(updated);
            Assert.Equal(UpdateUserGroupResult.Updated, result);

            var fetched = await _repository.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.Equal(updated.Name, fetched!.Name);
            Assert.False(fetched.IsActive);
            Assert.Equal(updated.CanRestartWorkflow, fetched.CanRestartWorkflow);
            Assert.Equal(updated.CanChangeWorkflow, fetched.CanChangeWorkflow);
            Assert.Equal(updated.CanSubmitRequests, fetched.CanSubmitRequests);
        }
        finally
        {
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_returns_NotFound_for_unknown_id()
    {
        var ghost = new UserGroup
        {
            Id = int.MaxValue - 1,
            Name = "_test_ghost",
            IsActive = true,
            CanRestartWorkflow = false,
            CanChangeWorkflow = false,
            CanSubmitRequests = true,
        };

        var result = await _repository.UpdateAsync(ghost);
        Assert.Equal(UpdateUserGroupResult.NotFound, result);
    }

    [Fact]
    public async Task ListWithUserCountsAsync_returns_zero_for_empty_group()
    {
        var group = NewTestGroup();
        int newId = 0;
        try
        {
            newId = await _repository.CreateAsync(group);

            var list = await _repository.ListWithUserCountsAsync();
            var item = list.SingleOrDefault(i => i.Group.Id == newId);
            Assert.NotNull(item);
            Assert.Equal(0, item!.AssignedUserCount);
        }
        finally
        {
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    [Fact]
    public async Task ListWithUserCountsAsync_includes_correct_count_for_group_with_users()
    {
        var group = NewTestGroup();
        int newId = 0;
        var userIds = new List<int>();
        try
        {
            newId = await _repository.CreateAsync(group);
            userIds.Add(await InsertTestUserAsync(newId, isActive: true));
            userIds.Add(await InsertTestUserAsync(newId, isActive: true));
            userIds.Add(await InsertTestUserAsync(newId, isActive: false));

            var list = await _repository.ListWithUserCountsAsync();
            var item = list.SingleOrDefault(i => i.Group.Id == newId);
            Assert.NotNull(item);
            Assert.Equal(3, item!.AssignedUserCount);
            // Sanity check that the embedded UserGroup carries fields through.
            Assert.Equal(group.Name, item.Group.Name);
            Assert.True(item.Group.IsActive);
        }
        finally
        {
            foreach (var uid in userIds) await DeleteUserAsync(uid);
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    [Fact]
    public async Task CountAssignedUsersAsync_returns_zero_for_new_group()
    {
        var group = NewTestGroup();
        int newId = 0;
        try
        {
            newId = await _repository.CreateAsync(group);

            var count = await _repository.CountAssignedUsersAsync(newId);
            Assert.Equal(0, count);
        }
        finally
        {
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    [Fact]
    public async Task CountAssignedUsersAsync_counts_assigned_users_regardless_of_user_activity()
    {
        var group = NewTestGroup();
        int newId = 0;
        var insertedUserIds = new List<int>();
        try
        {
            newId = await _repository.CreateAsync(group);
            insertedUserIds.Add(await InsertTestUserAsync(newId, isActive: true));
            insertedUserIds.Add(await InsertTestUserAsync(newId, isActive: false));

            var count = await _repository.CountAssignedUsersAsync(newId);
            Assert.Equal(2, count);
        }
        finally
        {
            foreach (var uid in insertedUserIds) await DeleteUserAsync(uid);
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_rejects_deactivation_when_users_are_assigned()
    {
        var group = NewTestGroup();
        int newId = 0;
        int userId = 0;
        try
        {
            newId = await _repository.CreateAsync(group);
            userId = await InsertTestUserAsync(newId, isActive: true);

            var deactivate = new UserGroup
            {
                Id = newId,
                Name = group.Name,
                IsActive = false,
                CanRestartWorkflow = group.CanRestartWorkflow,
                CanChangeWorkflow = group.CanChangeWorkflow,
                CanSubmitRequests = group.CanSubmitRequests,
            };

            var result = await _repository.UpdateAsync(deactivate);
            Assert.Equal(UpdateUserGroupResult.RejectedHasUsers, result);

            // Row should be unchanged — still active.
            var fetched = await _repository.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.True(fetched!.IsActive);
        }
        finally
        {
            if (userId > 0) await DeleteUserAsync(userId);
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_allows_name_and_permission_edits_when_users_are_assigned()
    {
        var group = NewTestGroup();
        int newId = 0;
        int userId = 0;
        try
        {
            newId = await _repository.CreateAsync(group);
            userId = await InsertTestUserAsync(newId, isActive: true);

            var edited = new UserGroup
            {
                Id = newId,
                Name = group.Name + "_renamed",
                IsActive = true,  // unchanged
                CanRestartWorkflow = !group.CanRestartWorkflow,
                CanChangeWorkflow = !group.CanChangeWorkflow,
                CanSubmitRequests = !group.CanSubmitRequests,
            };

            var result = await _repository.UpdateAsync(edited);
            Assert.Equal(UpdateUserGroupResult.Updated, result);

            var fetched = await _repository.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.Equal(edited.Name, fetched!.Name);
            Assert.Equal(edited.CanRestartWorkflow, fetched.CanRestartWorkflow);
        }
        finally
        {
            if (userId > 0) await DeleteUserAsync(userId);
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_allows_renaming_an_already_inactive_group_with_assigned_users()
    {
        // Edge case caught while writing the rule SQL: the rule should fire
        // only on an active->inactive transition, not when a row is already
        // inactive. Set up: create a group, deactivate it (no users yet),
        // then assign a user, then try to rename while keeping IsActive=0.
        // Renaming should succeed.
        var group = NewTestGroup();
        int newId = 0;
        int userId = 0;
        try
        {
            newId = await _repository.CreateAsync(group);

            var deactivate = new UserGroup
            {
                Id = newId,
                Name = group.Name,
                IsActive = false,
                CanRestartWorkflow = group.CanRestartWorkflow,
                CanChangeWorkflow = group.CanChangeWorkflow,
                CanSubmitRequests = group.CanSubmitRequests,
            };
            var deactResult = await _repository.UpdateAsync(deactivate);
            Assert.Equal(UpdateUserGroupResult.Updated, deactResult);

            userId = await InsertTestUserAsync(newId, isActive: true);

            var rename = new UserGroup
            {
                Id = newId,
                Name = group.Name + "_late_rename",
                IsActive = false,  // still inactive — no transition
                CanRestartWorkflow = group.CanRestartWorkflow,
                CanChangeWorkflow = group.CanChangeWorkflow,
                CanSubmitRequests = group.CanSubmitRequests,
            };
            var renameResult = await _repository.UpdateAsync(rename);
            Assert.Equal(UpdateUserGroupResult.Updated, renameResult);

            var fetched = await _repository.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.Equal(rename.Name, fetched!.Name);
            Assert.False(fetched.IsActive);
        }
        finally
        {
            if (userId > 0) await DeleteUserAsync(userId);
            if (newId > 0) await DeleteGroupAsync(newId);
        }
    }

    // ---- helpers --------------------------------------------------------

    private static UserGroup NewTestGroup() => new()
    {
        Name = $"_test_grp_{Guid.NewGuid():N}",
        IsActive = true,
        CanRestartWorkflow = true,
        CanChangeWorkflow = false,
        CanSubmitRequests = true,
    };

    private async Task<int> InsertTestUserAsync(int groupId, bool isActive)
    {
        const string sql = @"
            INSERT INTO dbo.users (entraid, name, group_id, is_admin, is_active)
            VALUES (@entraid, @name, @groupId, 0, @isActive);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        var suffix = Guid.NewGuid().ToString("N");
        var entraid = $"_test_user_{suffix}";
        var name = $"_test_user_name_{suffix}";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            sql, new { entraid, name, groupId, isActive });
    }

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
