using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Identity;
using VendorSure.Services.Data;
using VendorSure.Services.Identity;

namespace VendorSure.Infrastructure.Tests.Identity;

/// <summary>
/// Integration tests for the user-group repository. Talks to the dev DB.
/// Each test creates a fresh group with a recognizable
/// <c>_test_</c>-prefixed name and hard-deletes it in a <c>try/finally</c>.
/// If a test crashes mid-flight a stray <c>_test_*</c> row may be left
/// behind — harmless (no FKs point to test groups) but cleanup is
/// <c>DELETE FROM dbo.user_groups WHERE name LIKE '_test_%';</c>.
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
            if (newId > 0) await DeleteAsync(newId);
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
            if (newId > 0) await DeleteAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_round_trips_field_changes()
    {
        var group = NewTestGroup();
        int newId = 0;
        try
        {
            newId = await _repository.CreateAsync(group);

            var updated = new UserGroup
            {
                Id = newId,
                Name = group.Name + "_updated",
                IsActive = false,
                CanRestartWorkflow = !group.CanRestartWorkflow,
                CanChangeWorkflow = !group.CanChangeWorkflow,
                CanSubmitRequests = !group.CanSubmitRequests,
            };

            var ok = await _repository.UpdateAsync(updated);
            Assert.True(ok);

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
            if (newId > 0) await DeleteAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_returns_false_for_unknown_id()
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

        var ok = await _repository.UpdateAsync(ghost);
        Assert.False(ok);
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

    private async Task DeleteAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.user_groups WHERE id = @id;",
            new { id });
    }
}
