using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Documents;
using VendorSure.Services.Data;
using VendorSure.Services.Documents;

namespace VendorSure.Infrastructure.Tests.Documents;

/// <summary>
/// Integration tests for the document-type repository. Talks to the dev DB.
/// Each test creates its own _test_-prefixed rows and hard-deletes them in
/// <c>try/finally</c>. Some tests also need to stand up a Request Type +
/// Request Type Version + junction row to exercise the
/// 'RejectedReferenced' delete rule; cleanup order matters for FK reasons:
///   1. dbo.request_type_required_documents
///   2. dbo.request_type_versions
///   3. dbo.request_types
///   4. dbo.required_documents_library
/// </summary>
public sealed class DocumentTypeRepositoryTests : IClassFixture<InfrastructureTestFixture>
{
    private readonly IDocumentTypeRepository _docs;
    private readonly IDbConnectionFactory _connectionFactory;

    public DocumentTypeRepositoryTests(InfrastructureTestFixture fixture)
    {
        _docs = fixture.ServiceProvider.GetRequiredService<IDocumentTypeRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task CreateAsync_returns_new_id_and_persists_fields()
    {
        var doc = NewTestDoc();
        int newId = 0;
        try
        {
            var result = await _docs.CreateAsync(doc);
            Assert.Equal(CreateDocumentTypeOutcome.Created, result.Outcome);
            Assert.NotNull(result.Id);
            newId = result.Id!.Value;

            var fetched = await _docs.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.Equal(doc.Name, fetched!.Name);
            Assert.Equal(doc.FileTypeRequired, fetched.FileTypeRequired);
        }
        finally
        {
            if (newId > 0) await DeleteDocAsync(newId);
        }
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_name()
    {
        var first = NewTestDoc();
        int firstId = 0;
        try
        {
            firstId = (await _docs.CreateAsync(first)).Id!.Value;

            var second = new DocumentType
            {
                Name = first.Name,         // collision
                FileTypeRequired = "txt",
            };
            var result = await _docs.CreateAsync(second);
            Assert.Equal(CreateDocumentTypeOutcome.RejectedNameConflict, result.Outcome);
            Assert.Null(result.Id);
        }
        finally
        {
            if (firstId > 0) await DeleteDocAsync(firstId);
        }
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var result = await _docs.GetByIdAsync(int.MaxValue - 1);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllAsync_includes_inserted_row()
    {
        var doc = NewTestDoc();
        int newId = 0;
        try
        {
            newId = (await _docs.CreateAsync(doc)).Id!.Value;

            var all = await _docs.GetAllAsync();
            Assert.Contains(all, d => d.Id == newId && d.Name == doc.Name);
        }
        finally
        {
            if (newId > 0) await DeleteDocAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_round_trips_field_changes()
    {
        var doc = NewTestDoc();
        int newId = 0;
        try
        {
            newId = (await _docs.CreateAsync(doc)).Id!.Value;

            var edited = new DocumentType
            {
                Id = newId,
                Name = doc.Name + "_renamed",
                FileTypeRequired = "docx",
            };
            var result = await _docs.UpdateAsync(edited);
            Assert.Equal(UpdateDocumentTypeResult.Updated, result);

            var fetched = await _docs.GetByIdAsync(newId);
            Assert.NotNull(fetched);
            Assert.Equal(edited.Name, fetched!.Name);
            Assert.Equal(edited.FileTypeRequired, fetched.FileTypeRequired);
        }
        finally
        {
            if (newId > 0) await DeleteDocAsync(newId);
        }
    }

    [Fact]
    public async Task UpdateAsync_returns_NotFound_for_unknown_id()
    {
        var ghost = new DocumentType
        {
            Id = int.MaxValue - 1,
            Name = $"_test_doc_ghost_{Guid.NewGuid():N}",
            FileTypeRequired = "pdf",
        };
        var result = await _docs.UpdateAsync(ghost);
        Assert.Equal(UpdateDocumentTypeResult.NotFound, result);
    }

    [Fact]
    public async Task UpdateAsync_rejects_name_collision_with_another_row()
    {
        var a = NewTestDoc();
        var b = NewTestDoc();
        int idA = 0;
        int idB = 0;
        try
        {
            idA = (await _docs.CreateAsync(a)).Id!.Value;
            idB = (await _docs.CreateAsync(b)).Id!.Value;

            var collision = new DocumentType
            {
                Id = idB,
                Name = a.Name,  // attempt to take A's name
                FileTypeRequired = b.FileTypeRequired,
            };
            var result = await _docs.UpdateAsync(collision);
            Assert.Equal(UpdateDocumentTypeResult.RejectedNameConflict, result);
        }
        finally
        {
            if (idA > 0) await DeleteDocAsync(idA);
            if (idB > 0) await DeleteDocAsync(idB);
        }
    }

    [Fact]
    public async Task UpdateAsync_allows_keeping_same_name()
    {
        // Regression: the conflict check must use id <> @Id so a no-op edit
        // of the same row doesn't appear to conflict with itself.
        var doc = NewTestDoc();
        int newId = 0;
        try
        {
            newId = (await _docs.CreateAsync(doc)).Id!.Value;

            var edited = new DocumentType
            {
                Id = newId,
                Name = doc.Name,            // SAME name
                FileTypeRequired = "txt",   // only file_type changes
            };
            var result = await _docs.UpdateAsync(edited);
            Assert.Equal(UpdateDocumentTypeResult.Updated, result);

            var fetched = await _docs.GetByIdAsync(newId);
            Assert.Equal("txt", fetched!.FileTypeRequired);
        }
        finally
        {
            if (newId > 0) await DeleteDocAsync(newId);
        }
    }

    [Fact]
    public async Task DeleteAsync_removes_unreferenced_row()
    {
        var doc = NewTestDoc();
        var newId = (await _docs.CreateAsync(doc)).Id!.Value;

        var result = await _docs.DeleteAsync(newId);
        Assert.Equal(DeleteDocumentTypeResult.Deleted, result);

        var fetched = await _docs.GetByIdAsync(newId);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task DeleteAsync_returns_NotFound_for_unknown_id()
    {
        var result = await _docs.DeleteAsync(int.MaxValue - 1);
        Assert.Equal(DeleteDocumentTypeResult.NotFound, result);
    }

    [Fact]
    public async Task DeleteAsync_rejects_when_referenced_by_request_type_version()
    {
        // Stand up the full chain: a request type, a draft version of it,
        // and a junction row tying the doc to that version. Then verify
        // the delete is refused, and clean everything up in reverse order.
        var doc = NewTestDoc();
        int docId = 0;
        int rtId = 0;
        int rtvId = 0;
        int junctionId = 0;
        try
        {
            docId = (await _docs.CreateAsync(doc)).Id!.Value;
            (rtId, rtvId) = await CreateTestRequestTypeWithDraftVersionAsync();
            junctionId = await CreateJunctionAsync(rtvId, docId);

            var result = await _docs.DeleteAsync(docId);
            Assert.Equal(DeleteDocumentTypeResult.RejectedReferenced, result);

            // Row should still exist.
            var fetched = await _docs.GetByIdAsync(docId);
            Assert.NotNull(fetched);
        }
        finally
        {
            // FK order: junction first, then version, then type, then doc.
            if (junctionId > 0) await DeleteJunctionAsync(junctionId);
            if (rtvId > 0) await DeleteRequestTypeVersionAsync(rtvId);
            if (rtId > 0) await DeleteRequestTypeAsync(rtId);
            if (docId > 0) await DeleteDocAsync(docId);
        }
    }

    // ---- helpers --------------------------------------------------------

    private static DocumentType NewTestDoc() => new()
    {
        Name = $"_test_doc_{Guid.NewGuid():N}",
        FileTypeRequired = "pdf",
    };

    private async Task DeleteDocAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.required_documents_library WHERE id = @id;",
            new { id });
    }

    private async Task<(int RequestTypeId, int VersionId)> CreateTestRequestTypeWithDraftVersionAsync()
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();

        var rtName = $"_test_rt_{Guid.NewGuid():N}";
        const string insertRt = @"
            INSERT INTO dbo.request_types (name, is_explanation_required, is_active)
            VALUES (@name, 0, 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);";
        var rtId = await connection.QuerySingleAsync<int>(insertRt, new { name = rtName });

        const string insertRtv = @"
            INSERT INTO dbo.request_type_versions
                (request_type_id, version, request_state)
            VALUES (@rtId, 1, 'D');
            SELECT CAST(SCOPE_IDENTITY() AS int);";
        var rtvId = await connection.QuerySingleAsync<int>(insertRtv, new { rtId });

        return (rtId, rtvId);
    }

    private async Task<int> CreateJunctionAsync(int rtvId, int docId)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        const string sql = @"
            INSERT INTO dbo.request_type_required_documents
                (request_type_version_id, required_document_library_id, required)
            VALUES (@rtvId, @docId, 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);";
        return await connection.QuerySingleAsync<int>(sql, new { rtvId, docId });
    }

    private async Task DeleteJunctionAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_type_required_documents WHERE id = @id;",
            new { id });
    }

    private async Task DeleteRequestTypeVersionAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_type_versions WHERE id = @id;",
            new { id });
    }

    private async Task DeleteRequestTypeAsync(int id)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.request_types WHERE id = @id;",
            new { id });
    }
}
