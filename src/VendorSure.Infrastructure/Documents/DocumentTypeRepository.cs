using Dapper;
using VendorSure.Domain.Documents;
using VendorSure.Services.Data;
using VendorSure.Services.Documents;

namespace VendorSure.Infrastructure.Documents;

internal sealed class DocumentTypeRepository : IDocumentTypeRepository
{
    private const string SelectColumns = @"
        id                  AS Id,
        name                AS Name,
        file_type_required  AS FileTypeRequired";

    private readonly IDbConnectionFactory _connectionFactory;

    public DocumentTypeRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<DocumentType>> GetAllAsync(CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.required_documents_library ORDER BY name;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync<DocumentType>(command);
        return rows.ToList();
    }

    public async Task<DocumentType?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM dbo.required_documents_library WHERE id = @id;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { id }, cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<DocumentType>(command);
    }

    public async Task<CreateDocumentTypeResult> CreateAsync(DocumentType doc, CancellationToken ct = default)
    {
        // Conditional INSERT: only proceed if no row already has this name.
        // If the conflict check fails the INSERT is skipped and
        // SCOPE_IDENTITY returns NULL.
        const string insertSql = @"
            INSERT INTO dbo.required_documents_library (name, file_type_required)
            SELECT @Name, @FileTypeRequired
            WHERE NOT EXISTS (SELECT 1 FROM dbo.required_documents_library WHERE name = @Name);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var insertCommand = new CommandDefinition(insertSql, doc, cancellationToken: ct);
        var newId = await connection.QuerySingleOrDefaultAsync<int?>(insertCommand);

        return newId is not null
            ? new CreateDocumentTypeResult(CreateDocumentTypeOutcome.Created, newId)
            : new CreateDocumentTypeResult(CreateDocumentTypeOutcome.RejectedNameConflict, null);
    }

    public async Task<UpdateDocumentTypeResult> UpdateAsync(DocumentType doc, CancellationToken ct = default)
    {
        // Conflict check is 'no OTHER row has this name' so renaming a row
        // to its current name (or any other no-op update) is legal.
        const string updateSql = @"
            UPDATE dbo.required_documents_library
            SET name                = @Name,
                file_type_required  = @FileTypeRequired
            WHERE id = @Id
              AND NOT EXISTS (SELECT 1 FROM dbo.required_documents_library WHERE name = @Name AND id <> @Id);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var updateCommand = new CommandDefinition(updateSql, doc, cancellationToken: ct);
        var rowsAffected = await connection.ExecuteAsync(updateCommand);
        if (rowsAffected > 0)
        {
            return UpdateDocumentTypeResult.Updated;
        }

        // 0 rows updated. Either the id doesn't exist or the rename conflicts.
        // Probe in specificity order: row exists? then conflict.
        const string rowExistsSql =
            "SELECT COUNT(*) FROM dbo.required_documents_library WHERE id = @Id;";
        var rowExistsCommand = new CommandDefinition(rowExistsSql, new { doc.Id }, cancellationToken: ct);
        var rowExists = await connection.ExecuteScalarAsync<int>(rowExistsCommand) > 0;

        return rowExists
            ? UpdateDocumentTypeResult.RejectedNameConflict
            : UpdateDocumentTypeResult.NotFound;
    }

    public async Task<DeleteDocumentTypeResult> DeleteAsync(int id, CancellationToken ct = default)
    {
        // Conditional DELETE: only proceed if no junction row references this
        // library entry. Same atomic pattern as the Phase 2 rules — closes
        // the race window between a check and the mutation.
        const string deleteSql = @"
            DELETE FROM dbo.required_documents_library
            WHERE id = @id
              AND NOT EXISTS (
                    SELECT 1 FROM dbo.request_type_required_documents
                    WHERE required_document_library_id = @id);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var deleteCommand = new CommandDefinition(deleteSql, new { id }, cancellationToken: ct);
        var rowsAffected = await connection.ExecuteAsync(deleteCommand);
        if (rowsAffected > 0)
        {
            return DeleteDocumentTypeResult.Deleted;
        }

        // 0 rows deleted. Either the id doesn't exist or the row is referenced.
        const string rowExistsSql =
            "SELECT COUNT(*) FROM dbo.required_documents_library WHERE id = @id;";
        var rowExistsCommand = new CommandDefinition(rowExistsSql, new { id }, cancellationToken: ct);
        var rowExists = await connection.ExecuteScalarAsync<int>(rowExistsCommand) > 0;

        return rowExists
            ? DeleteDocumentTypeResult.RejectedReferenced
            : DeleteDocumentTypeResult.NotFound;
    }
}
