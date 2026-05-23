using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Documents;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.Documents;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.Tests.RequestTypes;

/// <summary>
/// Integration tests for the request-type-required-documents junction
/// repository. Each test creates its own RequestType + Draft Version +
/// one or more DocumentType library entries, exercises the junction, and
/// cleans up in FK order (junction → version → type → doc).
/// </summary>
public sealed class RequestTypeRequiredDocumentRepositoryTests
    : IClassFixture<InfrastructureTestFixture>
{
    private readonly IRequestTypeRequiredDocumentRepository _junction;
    private readonly IRequestTypeRepository _types;
    private readonly IRequestTypeVersionRepository _versions;
    private readonly IDocumentTypeRepository _docs;
    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeRequiredDocumentRepositoryTests(InfrastructureTestFixture fixture)
    {
        _junction = fixture.ServiceProvider.GetRequiredService<IRequestTypeRequiredDocumentRepository>();
        _types = fixture.ServiceProvider.GetRequiredService<IRequestTypeRepository>();
        _versions = fixture.ServiceProvider.GetRequiredService<IRequestTypeVersionRepository>();
        _docs = fixture.ServiceProvider.GetRequiredService<IDocumentTypeRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task AddAsync_returns_new_id_and_persists_fields()
    {
        await using var f = await SetupAsync();
        var result = await _junction.AddAsync(f.VersionId, f.DocId, required: true);
        Assert.Equal(AddRequiredDocumentOutcome.Added, result.Outcome);
        Assert.NotNull(result.Id);

        var list = await _junction.ListByVersionIdAsync(f.VersionId);
        var item = Assert.Single(list);
        Assert.Equal(result.Id, item.Id);
        Assert.Equal(f.VersionId, item.RequestTypeVersionId);
        Assert.Equal(f.DocId, item.RequiredDocumentLibraryId);
        Assert.Equal(f.DocName, item.LibraryName);
        Assert.True(item.Required);
    }

    [Fact]
    public async Task AddAsync_persists_required_false()
    {
        await using var f = await SetupAsync();
        var result = await _junction.AddAsync(f.VersionId, f.DocId, required: false);
        Assert.Equal(AddRequiredDocumentOutcome.Added, result.Outcome);

        var item = Assert.Single(await _junction.ListByVersionIdAsync(f.VersionId));
        Assert.False(item.Required);
    }

    [Fact]
    public async Task AddAsync_returns_RejectedVersionNotFound_for_unknown_version()
    {
        await using var f = await SetupAsync();
        var result = await _junction.AddAsync(int.MaxValue - 1, f.DocId, required: true);
        Assert.Equal(AddRequiredDocumentOutcome.RejectedVersionNotFound, result.Outcome);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task AddAsync_returns_RejectedDocumentNotFound_for_unknown_doc()
    {
        await using var f = await SetupAsync();
        var result = await _junction.AddAsync(f.VersionId, int.MaxValue - 1, required: true);
        Assert.Equal(AddRequiredDocumentOutcome.RejectedDocumentNotFound, result.Outcome);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task AddAsync_returns_RejectedNotDraft_when_version_is_InService()
    {
        await using var f = await SetupAsync();
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _junction.AddAsync(f.VersionId, f.DocId, required: true);
        Assert.Equal(AddRequiredDocumentOutcome.RejectedNotDraft, result.Outcome);
        Assert.Null(result.Id);

        // Confirm nothing was inserted.
        Assert.Empty(await _junction.ListByVersionIdAsync(f.VersionId));
    }

    [Fact]
    public async Task AddAsync_returns_RejectedDuplicate_for_second_add_of_same_pair()
    {
        await using var f = await SetupAsync();
        var first = await _junction.AddAsync(f.VersionId, f.DocId, required: true);
        Assert.Equal(AddRequiredDocumentOutcome.Added, first.Outcome);

        var second = await _junction.AddAsync(f.VersionId, f.DocId, required: false);
        Assert.Equal(AddRequiredDocumentOutcome.RejectedDuplicate, second.Outcome);
        Assert.Null(second.Id);

        // The first row's required flag must NOT have been overwritten
        // by the second attempt.
        var item = Assert.Single(await _junction.ListByVersionIdAsync(f.VersionId));
        Assert.True(item.Required);
    }

    [Fact]
    public async Task AddAsync_allows_same_doc_on_two_different_versions()
    {
        // The UNIQUE constraint is per (version, doc), so the same doc can
        // be attached to multiple versions independently. Add a second
        // version to the same type and verify.
        await using var f = await SetupAsync();

        var first = await _junction.AddAsync(f.VersionId, f.DocId, required: true);
        Assert.Equal(AddRequiredDocumentOutcome.Added, first.Outcome);

        // Make a sibling Draft version on the same type. No need for
        // separate cleanup — the fixture's DeleteVersionsForTypeAsync
        // sweeps all versions of this type.
        var v2 = await _versions.CreateDraftAsync(f.TypeId);
        var secondVersionId = v2.Id!.Value;

        var second = await _junction.AddAsync(secondVersionId, f.DocId, required: false);
        Assert.Equal(AddRequiredDocumentOutcome.Added, second.Outcome);
    }

    [Fact]
    public async Task ListByVersionIdAsync_orders_by_library_name()
    {
        await using var f = await SetupAsync();
        int extraDoc1 = 0, extraDoc2 = 0;
        try
        {
            // Create two more docs with names that sort around the seeded one.
            extraDoc1 = (await _docs.CreateAsync(new DocumentType
            {
                Name = "_test_doc_aaa_" + Guid.NewGuid().ToString("N"),
                FileTypeRequired = "pdf",
            })).Id!.Value;
            extraDoc2 = (await _docs.CreateAsync(new DocumentType
            {
                Name = "_test_doc_zzz_" + Guid.NewGuid().ToString("N"),
                FileTypeRequired = "pdf",
            })).Id!.Value;

            // Add in non-alphabetical order.
            await _junction.AddAsync(f.VersionId, extraDoc2, required: true);
            await _junction.AddAsync(f.VersionId, f.DocId, required: true);
            await _junction.AddAsync(f.VersionId, extraDoc1, required: true);

            var list = await _junction.ListByVersionIdAsync(f.VersionId);
            Assert.Equal(3, list.Count);
            // Sorted ascending by library name.
            var names = list.Select(i => i.LibraryName).ToArray();
            var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(sorted, names);
        }
        finally
        {
            await f.DisposeAsync();
            if (extraDoc1 > 0) await DeleteDocAsync(extraDoc1);
            if (extraDoc2 > 0) await DeleteDocAsync(extraDoc2);
        }
    }

    [Fact]
    public async Task ListByVersionIdAsync_returns_empty_for_version_with_no_junctions()
    {
        await using var f = await SetupAsync();
        var list = await _junction.ListByVersionIdAsync(f.VersionId);
        Assert.Empty(list);
    }

    [Fact]
    public async Task RemoveAsync_removes_existing_row()
    {
        await using var f = await SetupAsync();
        var added = await _junction.AddAsync(f.VersionId, f.DocId, required: true);
        var junctionId = added.Id!.Value;

        var result = await _junction.RemoveAsync(junctionId);
        Assert.Equal(MutateRequiredDocumentResult.Succeeded, result);

        Assert.Empty(await _junction.ListByVersionIdAsync(f.VersionId));
    }

    [Fact]
    public async Task RemoveAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _junction.RemoveAsync(int.MaxValue - 1);
        Assert.Equal(MutateRequiredDocumentResult.NotFound, result);
    }

    [Fact]
    public async Task RemoveAsync_returns_RejectedNotDraft_when_version_is_InService()
    {
        await using var f = await SetupAsync();
        var added = await _junction.AddAsync(f.VersionId, f.DocId, required: true);
        var junctionId = added.Id!.Value;

        // Now place the version In Service (via raw SQL — Chunk 9 owns
        // the legitimate path).
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _junction.RemoveAsync(junctionId);
        Assert.Equal(MutateRequiredDocumentResult.RejectedNotDraft, result);

        // Row still exists.
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Draft);
        Assert.Single(await _junction.ListByVersionIdAsync(f.VersionId));
    }

    [Fact]
    public async Task SetRequiredAsync_toggles_required_flag()
    {
        await using var f = await SetupAsync();
        var added = await _junction.AddAsync(f.VersionId, f.DocId, required: true);
        var junctionId = added.Id!.Value;

        var result = await _junction.SetRequiredAsync(junctionId, required: false);
        Assert.Equal(MutateRequiredDocumentResult.Succeeded, result);

        var item = Assert.Single(await _junction.ListByVersionIdAsync(f.VersionId));
        Assert.False(item.Required);

        // Toggle back.
        await _junction.SetRequiredAsync(junctionId, required: true);
        item = Assert.Single(await _junction.ListByVersionIdAsync(f.VersionId));
        Assert.True(item.Required);
    }

    [Fact]
    public async Task SetRequiredAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _junction.SetRequiredAsync(int.MaxValue - 1, required: true);
        Assert.Equal(MutateRequiredDocumentResult.NotFound, result);
    }

    [Fact]
    public async Task SetRequiredAsync_returns_RejectedNotDraft_when_version_is_not_Draft()
    {
        await using var f = await SetupAsync();
        var added = await _junction.AddAsync(f.VersionId, f.DocId, required: true);
        var junctionId = added.Id!.Value;

        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Superseded);

        var result = await _junction.SetRequiredAsync(junctionId, required: false);
        Assert.Equal(MutateRequiredDocumentResult.RejectedNotDraft, result);

        // Flag unchanged.
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Draft);
        var item = Assert.Single(await _junction.ListByVersionIdAsync(f.VersionId));
        Assert.True(item.Required);
    }

    // ---- fixture / helpers ----------------------------------------------

    /// <summary>
    /// Per-test scaffold: a fresh RequestType with a Draft version 1, plus
    /// one DocumentType library entry. Disposing tears them down in FK
    /// order. Safe to call DisposeAsync more than once — the cleanup is
    /// guarded so tests that need to interleave their own cleanup before
    /// the fixture's can explicitly call DisposeAsync without worrying
    /// about the `await using` scope-exit also firing.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public required int TypeId { get; init; }
        public required int VersionId { get; init; }
        public required int DocId { get; init; }
        public required string DocName { get; init; }
        public required Func<Task> Cleanup { get; init; }

        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await Cleanup();
        }
    }

    private async Task<Fixture> SetupAsync()
    {
        var typeResult = await _types.CreateWithFirstDraftAsync(new RequestType
        {
            Name = $"_test_rt_{Guid.NewGuid():N}",
            IsExplanationRequired = false,
            IsActive = true,
        });
        var typeId = typeResult.RequestTypeId!.Value;
        var versionId = typeResult.VersionId!.Value;

        var docName = $"_test_doc_{Guid.NewGuid():N}";
        var docId = (await _docs.CreateAsync(new DocumentType
        {
            Name = docName,
            FileTypeRequired = "pdf",
        })).Id!.Value;

        return new Fixture
        {
            TypeId = typeId,
            VersionId = versionId,
            DocId = docId,
            DocName = docName,
            Cleanup = async () =>
            {
                await DeleteJunctionsForVersionAsync(versionId);
                await DeleteVersionsForTypeAsync(typeId);
                await DeleteTypeAsync(typeId);
                await DeleteDocAsync(docId);
            },
        };
    }

    private async Task DeleteJunctionsForVersionAsync(int versionId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_type_required_documents WHERE request_type_version_id = @versionId;",
            new { versionId });
    }

    private async Task DeleteVersionsForTypeAsync(int typeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        // Junctions on any version of this type must go first.
        await connection.ExecuteAsync(@"
            DELETE FROM dbo.request_type_required_documents
            WHERE request_type_version_id IN
                (SELECT id FROM dbo.request_type_versions WHERE request_type_id = @typeId);
            DELETE FROM dbo.request_type_versions WHERE request_type_id = @typeId;",
            new { typeId });
    }

    private async Task DeleteTypeAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_types WHERE id = @id;",
            new { id });
    }

    private async Task DeleteDocAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.required_documents_library WHERE id = @id;",
            new { id });
    }

    private async Task ForceVersionStateAsync(int versionId, string stateCode)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE dbo.request_type_versions SET request_state = @stateCode WHERE id = @versionId;",
            new { versionId, stateCode });
    }
}
