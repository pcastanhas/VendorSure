using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Documents;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.Documents;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.Tests.RequestTypes;

/// <summary>
/// Integration tests for the validation-document junction repository.
/// The interesting tests exercise the same-version invariant (the schema's
/// FK constraints don't enforce it — the repo does).
/// </summary>
public sealed class RequestTypeValidationDocumentRepositoryTests
    : IClassFixture<InfrastructureTestFixture>
{
    private readonly IRequestTypeValidationDocumentRepository _junction;
    private readonly IRequestTypeValidationRepository _validations;
    private readonly IRequestTypeRequiredDocumentRepository _requiredDocs;
    private readonly IRequestTypeRepository _types;
    private readonly IRequestTypeVersionRepository _versions;
    private readonly IDocumentTypeRepository _docs;
    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeValidationDocumentRepositoryTests(InfrastructureTestFixture fixture)
    {
        _junction = fixture.ServiceProvider.GetRequiredService<IRequestTypeValidationDocumentRepository>();
        _validations = fixture.ServiceProvider.GetRequiredService<IRequestTypeValidationRepository>();
        _requiredDocs = fixture.ServiceProvider.GetRequiredService<IRequestTypeRequiredDocumentRepository>();
        _types = fixture.ServiceProvider.GetRequiredService<IRequestTypeRepository>();
        _versions = fixture.ServiceProvider.GetRequiredService<IRequestTypeVersionRepository>();
        _docs = fixture.ServiceProvider.GetRequiredService<IDocumentTypeRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task AddAsync_attaches_existing_validation_to_existing_required_doc()
    {
        await using var f = await SetupAsync();

        var result = await _junction.AddAsync(f.ValidationId, f.RequiredDocId);
        Assert.Equal(AddValidationDocumentOutcome.Added, result.Outcome);
        Assert.NotNull(result.Id);

        var list = await _junction.ListByValidationIdAsync(f.ValidationId);
        var item = Assert.Single(list);
        Assert.Equal(f.ValidationId, item.RequestTypeValidationId);
        Assert.Equal(f.RequiredDocId, item.RequestTypeRequiredDocumentId);
        Assert.Equal(f.DocLibraryId, item.RequiredDocumentLibraryId);
        Assert.Equal(f.DocName, item.LibraryName);
    }

    [Fact]
    public async Task AddAsync_returns_RejectedValidationNotFound_for_unknown_validation()
    {
        await using var f = await SetupAsync();

        var result = await _junction.AddAsync(int.MaxValue - 1, f.RequiredDocId);
        Assert.Equal(AddValidationDocumentOutcome.RejectedValidationNotFound, result.Outcome);
    }

    [Fact]
    public async Task AddAsync_returns_RejectedRequiredDocumentNotFound_for_unknown_doc()
    {
        await using var f = await SetupAsync();

        var result = await _junction.AddAsync(f.ValidationId, int.MaxValue - 1);
        Assert.Equal(AddValidationDocumentOutcome.RejectedRequiredDocumentNotFound, result.Outcome);
    }

    [Fact]
    public async Task AddAsync_returns_RejectedVersionMismatch_when_endpoints_belong_to_different_versions()
    {
        // The interesting case. The schema's FK constraints only enforce
        // existence — both endpoints could exist on different versions.
        // The repo's same-version rule must catch this.
        await using var f = await SetupAsync();

        // Make a second Draft version on the SAME type, attach the same
        // library doc to IT, get the new required-doc junction id. That
        // required-doc lives on version 2; our validation is on version 1.
        var v2 = await _versions.CreateDraftAsync(f.TypeId);
        var v2VersionId = v2.Id!.Value;
        var foreignRequiredDocId = (await _requiredDocs.AddAsync(
            v2VersionId, f.DocLibraryId, required: true)).Id!.Value;

        var result = await _junction.AddAsync(f.ValidationId, foreignRequiredDocId);
        Assert.Equal(AddValidationDocumentOutcome.RejectedVersionMismatch, result.Outcome);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task AddAsync_returns_RejectedNotDraft_when_shared_version_is_not_Draft()
    {
        await using var f = await SetupAsync();
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _junction.AddAsync(f.ValidationId, f.RequiredDocId);
        Assert.Equal(AddValidationDocumentOutcome.RejectedNotDraft, result.Outcome);
    }

    [Fact]
    public async Task AddAsync_returns_RejectedDuplicate_for_second_add_of_same_pair()
    {
        await using var f = await SetupAsync();

        var first = await _junction.AddAsync(f.ValidationId, f.RequiredDocId);
        Assert.Equal(AddValidationDocumentOutcome.Added, first.Outcome);

        var second = await _junction.AddAsync(f.ValidationId, f.RequiredDocId);
        Assert.Equal(AddValidationDocumentOutcome.RejectedDuplicate, second.Outcome);

        // Only one row exists.
        var list = await _junction.ListByValidationIdAsync(f.ValidationId);
        Assert.Single(list);
    }

    [Fact]
    public async Task ListByValidationIdAsync_orders_by_library_name()
    {
        await using var f = await SetupAsync();

        // Add two more library docs + required-doc junctions on the same
        // version, then attach all three to the validation in random order.
        var docB = (await _docs.CreateAsync(new DocumentType
        {
            Name = "_test_doc_AAA_" + Guid.NewGuid().ToString("N"),
            FileTypeRequired = "pdf",
        })).Id!.Value;
        var docC = (await _docs.CreateAsync(new DocumentType
        {
            Name = "_test_doc_ZZZ_" + Guid.NewGuid().ToString("N"),
            FileTypeRequired = "pdf",
        })).Id!.Value;

        var rdB = (await _requiredDocs.AddAsync(f.VersionId, docB, true)).Id!.Value;
        var rdC = (await _requiredDocs.AddAsync(f.VersionId, docC, true)).Id!.Value;

        await _junction.AddAsync(f.ValidationId, rdC);
        await _junction.AddAsync(f.ValidationId, f.RequiredDocId);
        await _junction.AddAsync(f.ValidationId, rdB);

        try
        {
            var list = await _junction.ListByValidationIdAsync(f.ValidationId);
            var names = list.Select(i => i.LibraryName).ToArray();
            var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(sorted, names);
        }
        finally
        {
            // Clean up the extra docs explicitly. Order: junction rows
            // referencing rdB/rdC go via the fixture sweep, but the standalone
            // library entries need explicit deletion AFTER the fixture
            // tears down everything that references them.
            await f.DisposeAsync();
            await DeleteLibraryDocAsync(docB);
            await DeleteLibraryDocAsync(docC);
        }
    }

    [Fact]
    public async Task ListByValidationIdAsync_returns_empty_for_validation_with_no_attachments()
    {
        await using var f = await SetupAsync();
        var list = await _junction.ListByValidationIdAsync(f.ValidationId);
        Assert.Empty(list);
    }

    [Fact]
    public async Task RemoveAsync_removes_junction_row()
    {
        await using var f = await SetupAsync();
        var added = await _junction.AddAsync(f.ValidationId, f.RequiredDocId);

        var result = await _junction.RemoveAsync(added.Id!.Value);
        Assert.Equal(RemoveValidationDocumentResult.Removed, result);

        Assert.Empty(await _junction.ListByValidationIdAsync(f.ValidationId));
    }

    [Fact]
    public async Task RemoveAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _junction.RemoveAsync(int.MaxValue - 1);
        Assert.Equal(RemoveValidationDocumentResult.NotFound, result);
    }

    [Fact]
    public async Task RemoveAsync_returns_RejectedNotDraft_when_parent_is_not_Draft()
    {
        await using var f = await SetupAsync();
        var added = await _junction.AddAsync(f.ValidationId, f.RequiredDocId);
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _junction.RemoveAsync(added.Id!.Value);
        Assert.Equal(RemoveValidationDocumentResult.RejectedNotDraft, result);
    }

    // ---- fixture / helpers ----------------------------------------------

    private sealed class Fixture : IAsyncDisposable
    {
        public required int TypeId { get; init; }
        public required int VersionId { get; init; }
        public required int DocLibraryId { get; init; }
        public required string DocName { get; init; }
        public required int RequiredDocId { get; init; }
        public required int ValidationId { get; init; }
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
        var docLibraryId = (await _docs.CreateAsync(new DocumentType
        {
            Name = docName,
            FileTypeRequired = "pdf",
        })).Id!.Value;

        var requiredDocId = (await _requiredDocs.AddAsync(
            versionId, docLibraryId, required: true)).Id!.Value;

        var validationId = (await _validations.CreateAsync(
            versionId, "_test_v_desc", "_test_v_prompt")).Id!.Value;

        return new Fixture
        {
            TypeId = typeId,
            VersionId = versionId,
            DocLibraryId = docLibraryId,
            DocName = docName,
            RequiredDocId = requiredDocId,
            ValidationId = validationId,
            Cleanup = async () =>
            {
                await DeleteValidationDocsForTypeAsync(typeId);
                await DeleteValidationsForTypeAsync(typeId);
                await DeleteRequiredDocsForTypeAsync(typeId);
                await DeleteVersionsForTypeAsync(typeId);
                await DeleteTypeAsync(typeId);
                await DeleteLibraryDocAsync(docLibraryId);
            },
        };
    }

    private async Task DeleteValidationDocsForTypeAsync(int typeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(@"
            DELETE FROM dbo.request_type_validation_documents
            WHERE request_type_validation_id IN (
                SELECT v.id FROM dbo.request_type_validations v
                INNER JOIN dbo.request_type_versions ver
                    ON ver.id = v.request_type_version_id
                WHERE ver.request_type_id = @typeId);",
            new { typeId });
    }

    private async Task DeleteValidationsForTypeAsync(int typeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(@"
            DELETE FROM dbo.request_type_validations
            WHERE request_type_version_id IN
                (SELECT id FROM dbo.request_type_versions WHERE request_type_id = @typeId);",
            new { typeId });
    }

    private async Task DeleteRequiredDocsForTypeAsync(int typeId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(@"
            DELETE FROM dbo.request_type_required_documents
            WHERE request_type_version_id IN
                (SELECT id FROM dbo.request_type_versions WHERE request_type_id = @typeId);",
            new { typeId });
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
        await connection.ExecuteAsync("DELETE FROM dbo.request_types WHERE id = @id;", new { id });
    }

    private async Task DeleteLibraryDocAsync(int id)
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
