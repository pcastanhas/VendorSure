using VendorSure.Domain.RequestTypes;

namespace VendorSure.Services.RequestTypes;

/// <summary>Outcome of <see cref="IRequestTypeValidationRepository.CreateAsync"/>.</summary>
public enum CreateValidationOutcome
{
    Created,

    /// <summary>The target version doesn't exist.</summary>
    RejectedVersionNotFound,

    /// <summary>The target version isn't Draft.</summary>
    RejectedNotDraft,
}

public sealed record CreateValidationResult(
    CreateValidationOutcome Outcome,
    int? Id,
    int? ExecutionOrder);

/// <summary>
/// Shared with <see cref="IRequestTypeValidationDocumentRepository"/>'s
/// Remove operation; both share the same 'mutating a row whose parent is
/// non-Draft' rejection semantics.
/// </summary>
public enum UpdateValidationResult
{
    Updated,
    NotFound,
    RejectedNotDraft,
}

public enum DeleteValidationResult
{
    Deleted,
    NotFound,
    RejectedNotDraft,
}

/// <summary>
/// CRUD on <c>request_type_validations</c> — the per-version list of AI
/// checks the triage layer runs on submissions.
/// </summary>
/// <remarks>
/// All mutations are gated on the parent Request Type version being in
/// <see cref="RequestState.Draft"/>. Reordering existing validations
/// (changing <see cref="RequestTypeValidation.ExecutionOrder"/> on an
/// existing row) is not exposed in Chunk 3 — only the create-path's
/// auto-append. If a future chunk needs drag-reorder, it'll need a
/// dedicated SQL path because the UNIQUE constraint on
/// (version_id, execution_order) makes naive updates collide.
/// </remarks>
public interface IRequestTypeValidationRepository
{
    /// <summary>
    /// Returns the validations for a version, ordered by
    /// <see cref="RequestTypeValidation.ExecutionOrder"/> ascending.
    /// </summary>
    Task<IReadOnlyList<RequestTypeValidation>> ListByVersionIdAsync(
        int requestTypeVersionId, CancellationToken ct = default);

    Task<RequestTypeValidation?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Appends a new validation to the given Draft version. The
    /// execution_order is computed as (current max + 1) atomically, so no
    /// race window opens between MAX() and INSERT.
    /// </summary>
    Task<CreateValidationResult> CreateAsync(
        int requestTypeVersionId,
        string description,
        string aiPrompt,
        CancellationToken ct = default);

    /// <summary>
    /// Edits the description and ai_prompt of a validation. Does not
    /// change <see cref="RequestTypeValidation.ExecutionOrder"/> or its
    /// parent version. Refused unless the parent version is Draft.
    /// </summary>
    Task<UpdateValidationResult> UpdateAsync(
        int id,
        string description,
        string aiPrompt,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a validation and all of its attached
    /// <c>request_type_validation_documents</c> junction rows. Transactional;
    /// refused unless the parent version is Draft.
    /// </summary>
    Task<DeleteValidationResult> DeleteAsync(int id, CancellationToken ct = default);
}
