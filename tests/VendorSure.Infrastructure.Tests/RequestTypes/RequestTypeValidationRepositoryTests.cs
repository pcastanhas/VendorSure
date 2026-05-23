using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.Tests.RequestTypes;

/// <summary>
/// Integration tests for the request-type-validations repository.
/// </summary>
public sealed class RequestTypeValidationRepositoryTests
    : IClassFixture<InfrastructureTestFixture>
{
    private readonly IRequestTypeValidationRepository _validations;
    private readonly IRequestTypeRepository _types;
    private readonly IRequestTypeVersionRepository _versions;
    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeValidationRepositoryTests(InfrastructureTestFixture fixture)
    {
        _validations = fixture.ServiceProvider.GetRequiredService<IRequestTypeValidationRepository>();
        _types = fixture.ServiceProvider.GetRequiredService<IRequestTypeRepository>();
        _versions = fixture.ServiceProvider.GetRequiredService<IRequestTypeVersionRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task CreateAsync_returns_new_id_and_execution_order_1_for_first()
    {
        await using var f = await SetupAsync();

        var result = await _validations.CreateAsync(
            f.VersionId, "_test_desc_1", "_test_prompt_1");

        Assert.Equal(CreateValidationOutcome.Created, result.Outcome);
        Assert.NotNull(result.Id);
        Assert.Equal(1, result.ExecutionOrder);

        var fetched = await _validations.GetByIdAsync(result.Id!.Value);
        Assert.NotNull(fetched);
        Assert.Equal(f.VersionId, fetched!.RequestTypeVersionId);
        Assert.Equal("_test_desc_1", fetched.Description);
        Assert.Equal("_test_prompt_1", fetched.AiPrompt);
        Assert.Equal(1, fetched.ExecutionOrder);
    }

    [Fact]
    public async Task CreateAsync_increments_execution_order_per_version()
    {
        await using var f = await SetupAsync();

        var a = await _validations.CreateAsync(f.VersionId, "a", "pa");
        var b = await _validations.CreateAsync(f.VersionId, "b", "pb");
        var c = await _validations.CreateAsync(f.VersionId, "c", "pc");

        Assert.Equal(1, a.ExecutionOrder);
        Assert.Equal(2, b.ExecutionOrder);
        Assert.Equal(3, c.ExecutionOrder);
    }

    [Fact]
    public async Task CreateAsync_returns_RejectedVersionNotFound_for_unknown_version()
    {
        var result = await _validations.CreateAsync(int.MaxValue - 1, "desc", "prompt");
        Assert.Equal(CreateValidationOutcome.RejectedVersionNotFound, result.Outcome);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task CreateAsync_returns_RejectedNotDraft_for_InService_version()
    {
        await using var f = await SetupAsync();
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _validations.CreateAsync(f.VersionId, "desc", "prompt");
        Assert.Equal(CreateValidationOutcome.RejectedNotDraft, result.Outcome);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task ListByVersionIdAsync_orders_by_execution_order()
    {
        await using var f = await SetupAsync();

        await _validations.CreateAsync(f.VersionId, "first", "pa");
        await _validations.CreateAsync(f.VersionId, "second", "pb");
        await _validations.CreateAsync(f.VersionId, "third", "pc");

        var list = await _validations.ListByVersionIdAsync(f.VersionId);
        Assert.Equal(new[] { 1, 2, 3 }, list.Select(v => v.ExecutionOrder).ToArray());
        Assert.Equal(new[] { "first", "second", "third" }, list.Select(v => v.Description).ToArray());
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var result = await _validations.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_edits_description_and_prompt()
    {
        await using var f = await SetupAsync();
        var created = await _validations.CreateAsync(f.VersionId, "orig", "orig-p");

        var result = await _validations.UpdateAsync(
            created.Id!.Value, "edited desc", "edited prompt");
        Assert.Equal(UpdateValidationResult.Updated, result);

        var fetched = await _validations.GetByIdAsync(created.Id.Value);
        Assert.Equal("edited desc", fetched!.Description);
        Assert.Equal("edited prompt", fetched.AiPrompt);
        // execution_order untouched.
        Assert.Equal(created.ExecutionOrder, fetched.ExecutionOrder);
    }

    [Fact]
    public async Task UpdateAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _validations.UpdateAsync(int.MaxValue - 1, "d", "p");
        Assert.Equal(UpdateValidationResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_returns_RejectedNotDraft_when_parent_version_is_InService()
    {
        await using var f = await SetupAsync();
        var created = await _validations.CreateAsync(f.VersionId, "orig", "orig-p");
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.InService);

        var result = await _validations.UpdateAsync(
            created.Id!.Value, "should not change", "neither");
        Assert.Equal(UpdateValidationResult.RejectedNotDraft, result);

        // Restore Draft to read back; row must be unchanged.
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Draft);
        var fetched = await _validations.GetByIdAsync(created.Id.Value);
        Assert.Equal("orig", fetched!.Description);
    }

    [Fact]
    public async Task DeleteAsync_removes_validation_with_no_attached_docs()
    {
        await using var f = await SetupAsync();
        var created = await _validations.CreateAsync(f.VersionId, "to-delete", "p");

        var result = await _validations.DeleteAsync(created.Id!.Value);
        Assert.Equal(DeleteValidationResult.Deleted, result);

        Assert.Null(await _validations.GetByIdAsync(created.Id.Value));
    }

    [Fact]
    public async Task DeleteAsync_also_removes_attached_validation_documents()
    {
        // Stand up: validation + required-doc + validation-doc junction.
        // DeleteAsync(validation) should remove BOTH the junction row and
        // the validation row, transactionally.
        await using var f = await SetupAsync();
        var validation = await _validations.CreateAsync(f.VersionId, "v", "p");
        var rdId = await InsertRequiredDocAsync(f.VersionId, f.DocId, required: true);
        var jdId = await InsertValidationDocAsync(validation.Id!.Value, rdId);

        var result = await _validations.DeleteAsync(validation.Id.Value);
        Assert.Equal(DeleteValidationResult.Deleted, result);

        // Both rows gone.
        Assert.False(await ValidationDocExistsAsync(jdId));
        Assert.Null(await _validations.GetByIdAsync(validation.Id.Value));
    }

    [Fact]
    public async Task DeleteAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _validations.DeleteAsync(int.MaxValue - 1);
        Assert.Equal(DeleteValidationResult.NotFound, result);
    }

    [Fact]
    public async Task DeleteAsync_returns_RejectedNotDraft_when_parent_version_is_not_Draft()
    {
        await using var f = await SetupAsync();
        var created = await _validations.CreateAsync(f.VersionId, "v", "p");
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Superseded);

        var result = await _validations.DeleteAsync(created.Id!.Value);
        Assert.Equal(DeleteValidationResult.RejectedNotDraft, result);

        // Restore Draft to verify the row's still there.
        await ForceVersionStateAsync(f.VersionId, RequestStateCodes.Draft);
        Assert.NotNull(await _validations.GetByIdAsync(created.Id.Value));
    }

    // ---- fixture / helpers ----------------------------------------------

    private sealed class Fixture : IAsyncDisposable
    {
        public required int TypeId { get; init; }
        public required int VersionId { get; init; }
        public required int DocId { get; init; }
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

        // We also need a DocumentType library entry on hand for the
        // "validation + attached doc" delete test.
        var docId = await InsertLibraryDocAsync($"_test_doc_{Guid.NewGuid():N}");

        return new Fixture
        {
            TypeId = typeId,
            VersionId = versionId,
            DocId = docId,
            Cleanup = async () =>
            {
                // FK order: validation_documents → validations → required_documents
                //          → versions → type, plus the standalone library doc.
                await DeleteValidationDocsForVersionAsync(versionId);
                await DeleteValidationsForVersionAsync(versionId);
                await DeleteRequiredDocsForVersionAsync(versionId);
                await DeleteVersionsForTypeAsync(typeId);
                await DeleteTypeAsync(typeId);
                await DeleteLibraryDocAsync(docId);
            },
        };
    }

    private async Task<int> InsertLibraryDocAsync(string name)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            @"INSERT INTO dbo.required_documents_library (name, file_type_required)
              VALUES (@name, 'pdf');
              SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { name });
    }

    private async Task<int> InsertRequiredDocAsync(int versionId, int libraryId, bool required)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            @"INSERT INTO dbo.request_type_required_documents
                (request_type_version_id, required_document_library_id, required)
              VALUES (@versionId, @libraryId, @required);
              SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { versionId, libraryId, required });
    }

    private async Task<int> InsertValidationDocAsync(int validationId, int requiredDocId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            @"INSERT INTO dbo.request_type_validation_documents
                (request_type_validation_id, request_type_required_document_id)
              VALUES (@validationId, @requiredDocId);
              SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { validationId, requiredDocId });
    }

    private async Task<bool> ValidationDocExistsAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.request_type_validation_documents WHERE id = @id;",
            new { id }) > 0;
    }

    private async Task DeleteValidationDocsForVersionAsync(int versionId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(@"
            DELETE FROM dbo.request_type_validation_documents
            WHERE request_type_validation_id IN
                (SELECT id FROM dbo.request_type_validations WHERE request_type_version_id = @versionId);",
            new { versionId });
    }

    private async Task DeleteValidationsForVersionAsync(int versionId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_type_validations WHERE request_type_version_id = @versionId;",
            new { versionId });
    }

    private async Task DeleteRequiredDocsForVersionAsync(int versionId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_type_required_documents WHERE request_type_version_id = @versionId;",
            new { versionId });
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
