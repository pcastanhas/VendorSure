using Dapper;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.RequestTypes;

internal sealed class RequestTypeRequiredDocumentRepository
    : IRequestTypeRequiredDocumentRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeRequiredDocumentRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<RequestTypeRequiredDocumentListItem>> ListByVersionIdAsync(
        int requestTypeVersionId, CancellationToken ct = default)
    {
        // Join the junction to the library so the admin detail page gets
        // the doc's display name and file-type hint without another query.
        // Ordered by library name for stable rendering.
        const string sql = @"
            SELECT
                j.id                          AS Id,
                j.request_type_version_id     AS RequestTypeVersionId,
                j.required_document_library_id AS RequiredDocumentLibraryId,
                lib.name                      AS LibraryName,
                lib.file_type_required        AS FileTypeRequired,
                j.required                    AS Required
            FROM dbo.request_type_required_documents j
            INNER JOIN dbo.required_documents_library lib
                ON lib.id = j.required_document_library_id
            WHERE j.request_type_version_id = @requestTypeVersionId
            ORDER BY lib.name ASC;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { requestTypeVersionId }, cancellationToken: ct);
        var rows = await connection.QueryAsync<RequestTypeRequiredDocumentListItem>(command);
        return rows.ToList();
    }

    public async Task<AddRequiredDocumentResult> AddAsync(
        int requestTypeVersionId,
        int requiredDocumentLibraryId,
        bool required,
        CancellationToken ct = default)
    {
        // One conditional INSERT does all four checks atomically:
        //   - the version exists AND is Draft
        //   - the library entry exists
        //   - no existing junction row for this (version, library) pair
        // When any check fails the INSERT is skipped and SCOPE_IDENTITY
        // returns NULL. Probes below disambiguate which one failed.
        const string insertSql = @"
            INSERT INTO dbo.request_type_required_documents
                (request_type_version_id, required_document_library_id, required)
            SELECT @versionId, @libraryId, @required
            WHERE EXISTS (
                    SELECT 1 FROM dbo.request_type_versions
                    WHERE id = @versionId AND request_state = @draftCode)
              AND EXISTS (
                    SELECT 1 FROM dbo.required_documents_library
                    WHERE id = @libraryId)
              AND NOT EXISTS (
                    SELECT 1 FROM dbo.request_type_required_documents
                    WHERE request_type_version_id = @versionId
                      AND required_document_library_id = @libraryId);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var newId = await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                insertSql,
                new
                {
                    versionId = requestTypeVersionId,
                    libraryId = requiredDocumentLibraryId,
                    required,
                    draftCode = RequestStateCodes.Draft,
                },
                cancellationToken: ct));

        if (newId is not null)
        {
            return new AddRequiredDocumentResult(AddRequiredDocumentOutcome.Added, newId);
        }

        // Insert was skipped. Run focused probes in specificity order.
        // 1. Does the version row exist at all?
        var versionExists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.request_type_versions WHERE id = @versionId;",
                new { versionId = requestTypeVersionId },
                cancellationToken: ct)) > 0;
        if (!versionExists)
        {
            return new AddRequiredDocumentResult(AddRequiredDocumentOutcome.RejectedVersionNotFound, null);
        }

        // 2. Version exists. Is it Draft?
        var versionIsDraft = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"SELECT COUNT(*) FROM dbo.request_type_versions
                  WHERE id = @versionId AND request_state = @draftCode;",
                new { versionId = requestTypeVersionId, draftCode = RequestStateCodes.Draft },
                cancellationToken: ct)) > 0;
        if (!versionIsDraft)
        {
            return new AddRequiredDocumentResult(AddRequiredDocumentOutcome.RejectedNotDraft, null);
        }

        // 3. Version is fine. Does the library row exist?
        var libraryExists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.required_documents_library WHERE id = @libraryId;",
                new { libraryId = requiredDocumentLibraryId },
                cancellationToken: ct)) > 0;
        if (!libraryExists)
        {
            return new AddRequiredDocumentResult(AddRequiredDocumentOutcome.RejectedDocumentNotFound, null);
        }

        // 4. All parents present, version is Draft — the UNIQUE check must
        //    have rejected it.
        return new AddRequiredDocumentResult(AddRequiredDocumentOutcome.RejectedDuplicate, null);
    }

    public async Task<MutateRequiredDocumentResult> RemoveAsync(int id, CancellationToken ct = default)
    {
        // Conditional DELETE — only proceed if the junction's parent version
        // is Draft. SQL Server supports DELETE … FROM … JOIN; the same
        // shape SetRequiredAsync uses.
        const string deleteSql = @"
            DELETE j
            FROM dbo.request_type_required_documents j
            INNER JOIN dbo.request_type_versions v
                ON v.id = j.request_type_version_id
            WHERE j.id = @id
              AND v.request_state = @draftCode;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var rowsAffected = await connection.ExecuteAsync(
            new CommandDefinition(
                deleteSql,
                new { id, draftCode = RequestStateCodes.Draft },
                cancellationToken: ct));

        if (rowsAffected > 0)
        {
            return MutateRequiredDocumentResult.Succeeded;
        }

        // 0 rows. Either no such junction, or parent version isn't Draft.
        return await DisambiguateMutateMissAsync(connection, id, ct);
    }

    public async Task<MutateRequiredDocumentResult> SetRequiredAsync(
        int id, bool required, CancellationToken ct = default)
    {
        // Conditional UPDATE — same Draft-only gate. Toggles only the
        // 'required' bit; identity columns and FK columns are not touched.
        const string updateSql = @"
            UPDATE j
            SET j.required = @required
            FROM dbo.request_type_required_documents j
            INNER JOIN dbo.request_type_versions v
                ON v.id = j.request_type_version_id
            WHERE j.id = @id
              AND v.request_state = @draftCode;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var rowsAffected = await connection.ExecuteAsync(
            new CommandDefinition(
                updateSql,
                new { id, required, draftCode = RequestStateCodes.Draft },
                cancellationToken: ct));

        if (rowsAffected > 0)
        {
            return MutateRequiredDocumentResult.Succeeded;
        }

        return await DisambiguateMutateMissAsync(connection, id, ct);
    }

    /// <summary>
    /// Shared disambiguation for Remove/SetRequired when 0 rows changed.
    /// Either the junction row doesn't exist, or its parent version is
    /// in a non-Draft state.
    /// </summary>
    private static async Task<MutateRequiredDocumentResult> DisambiguateMutateMissAsync(
        System.Data.IDbConnection connection, int id, CancellationToken ct)
    {
        var rowExists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.request_type_required_documents WHERE id = @id;",
                new { id },
                cancellationToken: ct)) > 0;

        return rowExists
            ? MutateRequiredDocumentResult.RejectedNotDraft
            : MutateRequiredDocumentResult.NotFound;
    }
}
