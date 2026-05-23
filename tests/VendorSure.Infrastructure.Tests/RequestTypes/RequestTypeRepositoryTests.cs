using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.Tests.RequestTypes;

/// <summary>
/// Integration tests for the request-type repository. Talks to the dev DB.
/// Test rows use the <c>_test_rt_</c> prefix and the version table's
/// matching rows are cleaned via FK-order in <c>finally</c>. See BUILD.md
/// for the cleanup query if anything leaks.
/// </summary>
public sealed class RequestTypeRepositoryTests : IClassFixture<InfrastructureTestFixture>
{
    private readonly IRequestTypeRepository _types;
    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeRepositoryTests(InfrastructureTestFixture fixture)
    {
        _types = fixture.ServiceProvider.GetRequiredService<IRequestTypeRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task CreateAsync_returns_new_id_and_persists_fields()
    {
        var type = NewTestType();
        int newId = 0;
        try
        {
            var result = await _types.CreateAsync(type);
            Assert.Equal(CreateRequestTypeOutcome.Created, result.Outcome);
            Assert.NotNull(result.Id);
            newId = result.Id!.Value;

            var fetched = await _types.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.Equal(type.Name, fetched!.Name);
            Assert.Equal(type.IsExplanationRequired, fetched.IsExplanationRequired);
            Assert.Equal(type.IsActive, fetched.IsActive);
        }
        finally
        {
            if (newId > 0) await DeleteTypeAsync(newId);
        }
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_name()
    {
        var first = NewTestType();
        int firstId = 0;
        try
        {
            firstId = (await _types.CreateAsync(first)).Id!.Value;

            var second = new RequestType
            {
                Name = first.Name,
                IsExplanationRequired = !first.IsExplanationRequired,
                IsActive = true,
            };
            var result = await _types.CreateAsync(second);
            Assert.Equal(CreateRequestTypeOutcome.RejectedNameConflict, result.Outcome);
            Assert.Null(result.Id);
        }
        finally
        {
            if (firstId > 0) await DeleteTypeAsync(firstId);
        }
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var result = await _types.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllAsync_includes_inserted_row()
    {
        var type = NewTestType();
        int newId = 0;
        try
        {
            newId = (await _types.CreateAsync(type)).Id!.Value;

            var all = await _types.GetAllAsync();
            Assert.Contains(all, t => t.Id == newId && t.Name == type.Name);
        }
        finally
        {
            if (newId > 0) await DeleteTypeAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_round_trips_field_changes()
    {
        var type = NewTestType();
        int newId = 0;
        try
        {
            newId = (await _types.CreateAsync(type)).Id!.Value;

            var edited = new RequestType
            {
                Id = newId,
                Name = type.Name + "_renamed",
                IsExplanationRequired = !type.IsExplanationRequired,
                IsActive = false,
            };
            var result = await _types.UpdateAsync(edited);
            Assert.Equal(UpdateRequestTypeResult.Updated, result);

            var fetched = await _types.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.Equal(edited.Name, fetched!.Name);
            Assert.Equal(edited.IsExplanationRequired, fetched.IsExplanationRequired);
            Assert.False(fetched.IsActive);
        }
        finally
        {
            if (newId > 0) await DeleteTypeAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_returns_NotFound_for_unknown_id()
    {
        var ghost = new RequestType
        {
            Id = int.MaxValue - 1,
            Name = $"_test_rt_ghost_{Guid.NewGuid():N}",
            IsExplanationRequired = false,
            IsActive = true,
        };
        var result = await _types.UpdateAsync(ghost);
        Assert.Equal(UpdateRequestTypeResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_rejects_name_collision_with_another_row()
    {
        var a = NewTestType();
        var b = NewTestType();
        int idA = 0;
        int idB = 0;
        try
        {
            idA = (await _types.CreateAsync(a)).Id!.Value;
            idB = (await _types.CreateAsync(b)).Id!.Value;

            var collision = new RequestType
            {
                Id = idB,
                Name = a.Name,
                IsExplanationRequired = b.IsExplanationRequired,
                IsActive = b.IsActive,
            };
            var result = await _types.UpdateAsync(collision);
            Assert.Equal(UpdateRequestTypeResult.RejectedNameConflict, result);
        }
        finally
        {
            if (idA > 0) await DeleteTypeAsync(idA);
            if (idB > 0) await DeleteTypeAsync(idB);
        }
    }

    [Fact]
    public async Task UpdateAsync_allows_keeping_same_name()
    {
        var type = NewTestType();
        int newId = 0;
        try
        {
            newId = (await _types.CreateAsync(type)).Id!.Value;

            var edited = new RequestType
            {
                Id = newId,
                Name = type.Name,                          // same name
                IsExplanationRequired = !type.IsExplanationRequired,
                IsActive = type.IsActive,
            };
            var result = await _types.UpdateAsync(edited);
            Assert.Equal(UpdateRequestTypeResult.Updated, result);
        }
        finally
        {
            if (newId > 0) await DeleteTypeAsync(newId);
        }
    }

    [Fact]
    public async Task CreateWithFirstDraftAsync_inserts_type_and_version_atomically()
    {
        var type = NewTestType();
        int newTypeId = 0;
        try
        {
            var result = await _types.CreateWithFirstDraftAsync(type);
            Assert.Equal(CreateRequestTypeOutcome.Created, result.Outcome);
            Assert.NotNull(result.RequestTypeId);
            Assert.NotNull(result.VersionId);
            newTypeId = result.RequestTypeId!.Value;

            // The type row exists.
            var fetchedType = await _types.GetByIdAsync(newTypeId);
            Assert.NotNull(fetchedType);

            // And the version row is version 1, Draft.
            var version = await ReadVersionByIdAsync(result.VersionId!.Value);
            Assert.NotNull(version);
            Assert.Equal(newTypeId, version!.Value.RequestTypeId);
            Assert.Equal(1, version.Value.Version);
            Assert.Equal("D", version.Value.RequestStateCode);
        }
        finally
        {
            if (newTypeId > 0)
            {
                await DeleteVersionsForTypeAsync(newTypeId);
                await DeleteTypeAsync(newTypeId);
            }
        }
    }

    [Fact]
    public async Task CreateWithFirstDraftAsync_rolls_back_when_name_conflicts()
    {
        // Seed: one type already exists with a known name.
        var existing = NewTestType();
        int existingId = 0;
        try
        {
            existingId = (await _types.CreateAsync(existing)).Id!.Value;

            // Try to create a NEW type with the same name via the
            // convenience method. Should reject and leave no orphan
            // version row.
            var collision = new RequestType
            {
                Name = existing.Name,
                IsExplanationRequired = false,
                IsActive = true,
            };
            var result = await _types.CreateWithFirstDraftAsync(collision);

            Assert.Equal(CreateRequestTypeOutcome.RejectedNameConflict, result.Outcome);
            Assert.Null(result.RequestTypeId);
            Assert.Null(result.VersionId);

            // The pre-existing type must not have gained a stray Draft v1
            // from the failed call.
            var versions = await ReadVersionsForTypeAsync(existingId);
            Assert.Empty(versions);
        }
        finally
        {
            if (existingId > 0)
            {
                await DeleteVersionsForTypeAsync(existingId);
                await DeleteTypeAsync(existingId);
            }
        }
    }

    // ---- helpers --------------------------------------------------------

    private static RequestType NewTestType() => new()
    {
        Name = $"_test_rt_{Guid.NewGuid():N}",
        IsExplanationRequired = false,
        IsActive = true,
    };

    private async Task DeleteTypeAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_types WHERE id = @id;",
            new { id });
    }

    private async Task DeleteVersionsForTypeAsync(int typeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_type_versions WHERE request_type_id = @typeId;",
            new { typeId });
    }

    // Tuple-returning raw-SQL probes for the integration tests. We avoid
    // calling the version repository here so the type-repo tests can fail
    // independently of the version repo's implementation.
    private async Task<(int Id, int RequestTypeId, int Version, string RequestStateCode)?> ReadVersionByIdAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<VersionProbe>(
            @"SELECT id AS Id, request_type_id AS RequestTypeId,
                     version AS Version, request_state AS RequestStateCode
              FROM dbo.request_type_versions WHERE id = @id;",
            new { id });
        return row is null ? null : (row.Id, row.RequestTypeId, row.Version, row.RequestStateCode);
    }

    private async Task<IReadOnlyList<int>> ReadVersionsForTypeAsync(int typeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        var rows = await connection.QueryAsync<int>(
            "SELECT id FROM dbo.request_type_versions WHERE request_type_id = @typeId;",
            new { typeId });
        return rows.ToList();
    }

    private sealed class VersionProbe
    {
        public int Id { get; init; }
        public int RequestTypeId { get; init; }
        public int Version { get; init; }
        public string RequestStateCode { get; init; } = string.Empty;
    }
}
