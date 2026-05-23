using Dapper;
using VendorSure.Domain.RequestTypes;
using VendorSure.Services.Data;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure.RequestTypes;

internal sealed class RequestTypeValidationDocumentRepository
    : IRequestTypeValidationDocumentRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RequestTypeValidationDocumentRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<RequestTypeValidationDocumentListItem>> ListByValidationIdAsync(
        int requestTypeValidationId, CancellationToken ct = default)
    {
        // Join all the way down to the library for display fields. The
        // junction → required_documents row gives us the library_id, and
        // we join the library for its name + file-type hint.
        const string sql = @"
            SELECT
                jd.id                                  AS Id,
                jd.request_type_validation_id          AS RequestTypeValidationId,
                jd.request_type_required_document_id   AS RequestTypeRequiredDocumentId,
                rd.required_document_library_id        AS RequiredDocumentLibraryId,
                lib.name                               AS LibraryName,
                lib.file_type_required                 AS FileTypeRequired
            FROM dbo.request_type_validation_documents jd
            INNER JOIN dbo.request_type_required_documents rd
                ON rd.id = jd.request_type_required_document_id
            INNER JOIN dbo.required_documents_library lib
                ON lib.id = rd.required_document_library_id
            WHERE jd.request_type_validation_id = @requestTypeValidationId
            ORDER BY lib.name ASC;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, new { requestTypeValidationId }, cancellationToken: ct);
        var rows = await connection.QueryAsync<RequestTypeValidationDocumentListItem>(command);
        return rows.ToList();
    }

    public async Task<AddValidationDocumentResult> AddAsync(
        int requestTypeValidationId,
        int requestTypeRequiredDocumentId,
        CancellationToken ct = default)
    {
        // Atomic conditional INSERT enforcing four rules at once:
        //   - validation exists (joined into the version-matching check)
        //   - required-doc junction exists (joined too)
        //   - both endpoints share the same parent version
        //   - that version is Draft
        // Plus a NOT EXISTS guard for the (validation, required-doc) pair
        // UNIQUE constraint.
        //
        // The 'same-version' check is the interesting one: an INNER JOIN
        // between request_type_validations and request_type_required_documents
        // on request_type_version_id only returns a row when both records
        // exist AND their version_ids match. If either is missing OR the
        // versions differ, the EXISTS subquery returns 0 rows and the
        // INSERT is skipped.
        const string insertSql = @"
            INSERT INTO dbo.request_type_validation_documents
                (request_type_validation_id, request_type_required_document_id)
            SELECT @validationId, @requiredDocId
            WHERE EXISTS (
                SELECT 1
                FROM dbo.request_type_validations val
                INNER JOIN dbo.request_type_required_documents rd
                    ON rd.request_type_version_id = val.request_type_version_id
                INNER JOIN dbo.request_type_versions ver
                    ON ver.id = val.request_type_version_id
                WHERE val.id = @validationId
                  AND rd.id = @requiredDocId
                  AND ver.request_state = @draftCode)
              AND NOT EXISTS (
                SELECT 1 FROM dbo.request_type_validation_documents
                WHERE request_type_validation_id = @validationId
                  AND request_type_required_document_id = @requiredDocId);
            SELECT CAST(SCOPE_IDENTITY() AS int);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var newId = await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                insertSql,
                new
                {
                    validationId = requestTypeValidationId,
                    requiredDocId = requestTypeRequiredDocumentId,
                    draftCode = RequestStateCodes.Draft,
                },
                cancellationToken: ct));

        if (newId is not null)
        {
            return new AddValidationDocumentResult(AddValidationDocumentOutcome.Added, newId);
        }

        // Insert skipped. Run focused probes in specificity order.

        // 1. Validation exists at all?
        var validationVersionId = await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                "SELECT request_type_version_id FROM dbo.request_type_validations WHERE id = @id;",
                new { id = requestTypeValidationId },
                cancellationToken: ct));
        if (validationVersionId is null)
        {
            return new AddValidationDocumentResult(
                AddValidationDocumentOutcome.RejectedValidationNotFound, null);
        }

        // 2. Required-doc junction exists at all?
        var requiredDocVersionId = await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                "SELECT request_type_version_id FROM dbo.request_type_required_documents WHERE id = @id;",
                new { id = requestTypeRequiredDocumentId },
                cancellationToken: ct));
        if (requiredDocVersionId is null)
        {
            return new AddValidationDocumentResult(
                AddValidationDocumentOutcome.RejectedRequiredDocumentNotFound, null);
        }

        // 3. Same version?
        if (validationVersionId.Value != requiredDocVersionId.Value)
        {
            return new AddValidationDocumentResult(
                AddValidationDocumentOutcome.RejectedVersionMismatch, null);
        }

        // 4. Both endpoints share a version. Is it Draft?
        var versionIsDraft = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"SELECT COUNT(*) FROM dbo.request_type_versions
                  WHERE id = @id AND request_state = @draftCode;",
                new { id = validationVersionId.Value, draftCode = RequestStateCodes.Draft },
                cancellationToken: ct)) > 0;
        if (!versionIsDraft)
        {
            return new AddValidationDocumentResult(
                AddValidationDocumentOutcome.RejectedNotDraft, null);
        }

        // 5. Everything else checks out — the UNIQUE constraint must have
        //    refused.
        return new AddValidationDocumentResult(
            AddValidationDocumentOutcome.RejectedDuplicate, null);
    }

    public async Task<RemoveValidationDocumentResult> RemoveAsync(int id, CancellationToken ct = default)
    {
        // Conditional DELETE gated on the (shared) parent version being
        // Draft. Single statement.
        const string deleteSql = @"
            DELETE jd
            FROM dbo.request_type_validation_documents jd
            INNER JOIN dbo.request_type_validations val
                ON val.id = jd.request_type_validation_id
            INNER JOIN dbo.request_type_versions ver
                ON ver.id = val.request_type_version_id
            WHERE jd.id = @id
              AND ver.request_state = @draftCode;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var rowsAffected = await connection.ExecuteAsync(
            new CommandDefinition(
                deleteSql,
                new { id, draftCode = RequestStateCodes.Draft },
                cancellationToken: ct));

        if (rowsAffected > 0)
        {
            return RemoveValidationDocumentResult.Removed;
        }

        // Disambiguate.
        var rowExists = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.request_type_validation_documents WHERE id = @id;",
                new { id },
                cancellationToken: ct)) > 0;

        return rowExists
            ? RemoveValidationDocumentResult.RejectedNotDraft
            : RemoveValidationDocumentResult.NotFound;
    }
}
