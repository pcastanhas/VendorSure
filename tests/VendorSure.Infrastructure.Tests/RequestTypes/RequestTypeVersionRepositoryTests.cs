using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.Tests.RequestTypes;

/// <summary>
/// Integration tests for the request-type-version repository. Cleanup
/// order matters: versions before types (FK).
/// </summary>
public sealed class RequestTypeVersionRepositoryTests : IClassFixture<InfrastructureTestFixture>
{
    private readonly IRequestTypeRepository _types;
    private readonly IRequestTypeVersionRepository _versions;
    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeVersionRepositoryTests(InfrastructureTestFixture fixture)
    {
        _types = fixture.ServiceProvider.GetRequiredService<IRequestTypeRepository>();
        _versions = fixture.ServiceProvider.GetRequiredService<IRequestTypeVersionRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task CreateDraftAsync_first_call_creates_version_1()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var result = await _versions.CreateDraftAsync(typeId);
            Assert.Equal(CreateDraftOutcome.Created, result.Outcome);
            Assert.NotNull(result.Id);
            Assert.Equal(1, result.Version);

            var fetched = await _versions.GetByIdAsync(result.Id!.Value);
            Assert.NotNull(fetched);
            Assert.Equal(typeId, fetched!.RequestTypeId);
            Assert.Equal(1, fetched.Version);
            Assert.Equal(RequestState.Draft, fetched.RequestState);
            Assert.NotEqual(default, fetched.CreatedTs);
            Assert.Null(fetched.PlacedInServiceTs);
            Assert.Null(fetched.SupersededTs);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task CreateDraftAsync_subsequent_calls_increment_version()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var first = await _versions.CreateDraftAsync(typeId);
            var second = await _versions.CreateDraftAsync(typeId);
            var third = await _versions.CreateDraftAsync(typeId);

            Assert.Equal(1, first.Version);
            Assert.Equal(2, second.Version);
            Assert.Equal(3, third.Version);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task CreateDraftAsync_returns_RejectedRequestTypeNotFound_for_unknown_type()
    {
        var result = await _versions.CreateDraftAsync(int.MaxValue - 1);
        Assert.Equal(CreateDraftOutcome.RejectedRequestTypeNotFound, result.Outcome);
        Assert.Null(result.Id);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var result = await _versions.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByRequestTypeIdAsync_returns_versions_in_ascending_order()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            await _versions.CreateDraftAsync(typeId);
            await _versions.CreateDraftAsync(typeId);
            await _versions.CreateDraftAsync(typeId);

            var list = await _versions.GetByRequestTypeIdAsync(typeId);
            Assert.Equal(3, list.Count);
            Assert.Equal(new[] { 1, 2, 3 }, list.Select(v => v.Version).ToArray());
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task UpdateAsync_round_trips_editable_fields_on_a_Draft()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var created = await _versions.CreateDraftAsync(typeId);
            var fresh = (await _versions.GetByIdAsync(created.Id!.Value))!;

            var edited = new RequestTypeVersion
            {
                Id = fresh.Id,
                RequestTypeId = fresh.RequestTypeId,        // not edited by UpdateAsync
                Version = fresh.Version,                    // not edited
                Name = "v1 — initial draft",
                RequestState = fresh.RequestState,          // not edited
                WorkflowSelectionPrompt = "Pick the simplest workflow.",
                CreatedTs = fresh.CreatedTs,                // not edited
            };

            var result = await _versions.UpdateAsync(edited);
            Assert.Equal(UpdateRequestTypeVersionResult.Updated, result);

            var fetched = await _versions.GetByIdAsync(fresh.Id);
            Assert.Equal("v1 — initial draft", fetched!.Name);
            Assert.Equal("Pick the simplest workflow.", fetched.WorkflowSelectionPrompt);
            // Version, RequestState, RequestTypeId, CreatedTs unchanged.
            Assert.Equal(fresh.Version, fetched.Version);
            Assert.Equal(fresh.RequestState, fetched.RequestState);
            Assert.Equal(fresh.RequestTypeId, fetched.RequestTypeId);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task UpdateAsync_returns_NotFound_for_unknown_id()
    {
        var ghost = new RequestTypeVersion
        {
            Id = int.MaxValue - 1,
            Name = "_test_ghost",
            WorkflowSelectionPrompt = null,
        };
        var result = await _versions.UpdateAsync(ghost);
        Assert.Equal(UpdateRequestTypeVersionResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_rejects_edits_on_an_InService_version()
    {
        // Immutability rule: even though Chunk 9 owns the official Draft→
        // InService transition path, this chunk should refuse updates on
        // any non-Draft row. Set the state directly via raw SQL to exercise
        // the rule, since the repository intentionally exposes no state-
        // changing API yet.
        var typeId = await CreateTestTypeAsync();
        try
        {
            var created = await _versions.CreateDraftAsync(typeId);
            await ForceVersionStateAsync(created.Id!.Value, RequestStateCodes.InService);

            var edited = new RequestTypeVersion
            {
                Id = created.Id!.Value,
                Name = "I should be refused",
                WorkflowSelectionPrompt = null,
            };
            var result = await _versions.UpdateAsync(edited);
            Assert.Equal(UpdateRequestTypeVersionResult.RejectedNotDraft, result);

            // Row should NOT have been updated.
            var fetched = await _versions.GetByIdAsync(created.Id.Value);
            Assert.NotEqual("I should be refused", fetched!.Name);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task UpdateAsync_rejects_edits_on_a_Superseded_version()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var created = await _versions.CreateDraftAsync(typeId);
            await ForceVersionStateAsync(created.Id!.Value, RequestStateCodes.Superseded);

            var edited = new RequestTypeVersion
            {
                Id = created.Id!.Value,
                Name = "I should also be refused",
            };
            var result = await _versions.UpdateAsync(edited);
            Assert.Equal(UpdateRequestTypeVersionResult.RejectedNotDraft, result);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task RequestState_round_trips_through_the_enum_mapper()
    {
        // Sanity: when we force a row into each of the three states via SQL,
        // the repository reads it back as the correct enum value. Cheap
        // coverage of RequestStateExtensions.FromCode beyond the unit-level
        // 'D'/'I'/'S' cases.
        var typeId = await CreateTestTypeAsync();
        try
        {
            var created = await _versions.CreateDraftAsync(typeId);
            var versionId = created.Id!.Value;

            await ForceVersionStateAsync(versionId, RequestStateCodes.Draft);
            Assert.Equal(RequestState.Draft, (await _versions.GetByIdAsync(versionId))!.RequestState);

            await ForceVersionStateAsync(versionId, RequestStateCodes.InService);
            Assert.Equal(RequestState.InService, (await _versions.GetByIdAsync(versionId))!.RequestState);

            await ForceVersionStateAsync(versionId, RequestStateCodes.Superseded);
            Assert.Equal(RequestState.Superseded, (await _versions.GetByIdAsync(versionId))!.RequestState);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_promotes_draft_with_no_prior_in_service()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var v1 = await _versions.CreateDraftAsync(typeId);
            var versionId = v1.Id!.Value;

            var result = await _versions.TransitionToInServiceAsync(versionId);
            Assert.Equal(TransitionToInServiceResult.Succeeded, result);

            var fetched = await _versions.GetByIdAsync(versionId);
            Assert.NotNull(fetched);
            Assert.Equal(RequestState.InService, fetched!.RequestState);
            Assert.NotNull(fetched.PlacedInServiceTs);
            Assert.Null(fetched.SupersededTs);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_supersedes_prior_in_service_atomically()
    {
        // The headline test. v1 is forced In Service, then v2 is created
        // and promoted via TransitionToInServiceAsync. After the call:
        //   - v2 is In Service with placed_in_service_ts set
        //   - v1 is Superseded with superseded_ts set
        //   - the timestamps are equal (single value captured in C# and
        //     passed to both UPDATEs).
        var typeId = await CreateTestTypeAsync();
        try
        {
            var v1 = await _versions.CreateDraftAsync(typeId);
            var v1Id = v1.Id!.Value;
            await ForceVersionStateAsync(v1Id, RequestStateCodes.InService);

            var v2 = await _versions.CreateDraftAsync(typeId);
            var v2Id = v2.Id!.Value;

            var result = await _versions.TransitionToInServiceAsync(v2Id);
            Assert.Equal(TransitionToInServiceResult.Succeeded, result);

            var v2Fetched = await _versions.GetByIdAsync(v2Id);
            var v1Fetched = await _versions.GetByIdAsync(v1Id);

            Assert.Equal(RequestState.InService, v2Fetched!.RequestState);
            Assert.NotNull(v2Fetched.PlacedInServiceTs);
            Assert.Null(v2Fetched.SupersededTs);

            Assert.Equal(RequestState.Superseded, v1Fetched!.RequestState);
            Assert.NotNull(v1Fetched.SupersededTs);

            // The two timestamps should be identical (same DateTime value
            // captured once and used for both UPDATEs). Allow a tiny
            // millisecond fudge for any rounding at the SQL Server side
            // — datetime2 precision is fine but Dapper round-tripping
            // through DateTime can lose sub-tick precision.
            var delta = (v2Fetched.PlacedInServiceTs!.Value - v1Fetched.SupersededTs!.Value).Duration();
            Assert.True(delta < TimeSpan.FromMilliseconds(1),
                $"Expected matching transition timestamps; got delta={delta}.");
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _versions.TransitionToInServiceAsync(int.MaxValue - 1);
        Assert.Equal(TransitionToInServiceResult.NotFound, result);
    }

    [Fact]
    public async Task TransitionToInServiceAsync_returns_RejectedNotDraft_for_InService_target()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var v1 = await _versions.CreateDraftAsync(typeId);
            await ForceVersionStateAsync(v1.Id!.Value, RequestStateCodes.InService);

            var result = await _versions.TransitionToInServiceAsync(v1.Id.Value);
            Assert.Equal(TransitionToInServiceResult.RejectedNotDraft, result);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_returns_RejectedNotDraft_for_Superseded_target()
    {
        var typeId = await CreateTestTypeAsync();
        try
        {
            var v1 = await _versions.CreateDraftAsync(typeId);
            await ForceVersionStateAsync(v1.Id!.Value, RequestStateCodes.Superseded);

            var result = await _versions.TransitionToInServiceAsync(v1.Id.Value);
            Assert.Equal(TransitionToInServiceResult.RejectedNotDraft, result);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeId);
            await DeleteTypeAsync(typeId);
        }
    }

    [Fact]
    public async Task TransitionToInServiceAsync_does_not_touch_other_types()
    {
        // Two unrelated types each with an In Service version. Promoting
        // v2 of type A must NOT supersede the In Service version of type
        // B. (The demote SQL filters on request_type_id; this test
        // guards against a regression where the filter is dropped.)
        var typeAId = await CreateTestTypeAsync();
        var typeBId = await CreateTestTypeAsync();
        try
        {
            // Type A: v1 InService, v2 Draft (target).
            var a1 = await _versions.CreateDraftAsync(typeAId);
            await ForceVersionStateAsync(a1.Id!.Value, RequestStateCodes.InService);
            var a2 = await _versions.CreateDraftAsync(typeAId);

            // Type B: v1 InService, untouched.
            var b1 = await _versions.CreateDraftAsync(typeBId);
            await ForceVersionStateAsync(b1.Id!.Value, RequestStateCodes.InService);

            var result = await _versions.TransitionToInServiceAsync(a2.Id!.Value);
            Assert.Equal(TransitionToInServiceResult.Succeeded, result);

            // Type B's InService should be untouched.
            var b1After = await _versions.GetByIdAsync(b1.Id.Value);
            Assert.Equal(RequestState.InService, b1After!.RequestState);
            Assert.Null(b1After.SupersededTs);
        }
        finally
        {
            await DeleteVersionsForTypeAsync(typeAId);
            await DeleteVersionsForTypeAsync(typeBId);
            await DeleteTypeAsync(typeAId);
            await DeleteTypeAsync(typeBId);
        }
    }

    // ---- helpers --------------------------------------------------------

    private async Task<int> CreateTestTypeAsync()
    {
        var result = await _types.CreateAsync(new RequestType
        {
            Name = $"_test_rt_{Guid.NewGuid():N}",
            IsExplanationRequired = false,
            IsActive = true,
        });
        return result.Id!.Value;
    }

    private async Task ForceVersionStateAsync(int versionId, string stateCode)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE dbo.request_type_versions SET request_state = @stateCode WHERE id = @versionId;",
            new { versionId, stateCode });
    }

    private async Task DeleteVersionsForTypeAsync(int typeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_type_versions WHERE request_type_id = @typeId;",
            new { typeId });
    }

    private async Task DeleteTypeAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_types WHERE id = @id;",
            new { id });
    }
}
