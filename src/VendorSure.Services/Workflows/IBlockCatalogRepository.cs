using VendorSure.Domain.Workflows;

namespace VendorSure.Services.Workflows;

/// <summary>Outcome of <see cref="IBlockCatalogRepository.CreateAsync"/>.</summary>
public enum CreateBlockCatalogOutcome
{
    /// <summary>Row inserted; the new id is on the result.</summary>
    Created,

    /// <summary>
    /// node_type_id wasn't one of {2 (Process), 3 (Decision)}. CHECK
    /// constraint <c>CK_block_catalog_node_type</c> would reject; we
    /// pre-check to give the caller a clean enum value rather than a
    /// SqlException.
    /// </summary>
    RejectedInvalidNodeType,

    /// <summary>
    /// actor_type wasn't one of {1,2,3}. Pre-check for the
    /// <c>CK_block_catalog_actor_type</c> constraint.
    /// </summary>
    RejectedInvalidActorType,

    /// <summary>
    /// Decision row (node_type_id=3) with NULL Path1Decision or
    /// Path2Decision. Pre-check for
    /// <c>CK_block_catalog_decision_labels</c>.
    /// </summary>
    RejectedDecisionLabelsRequired,

    /// <summary>
    /// Process row (node_type_id=2) with non-NULL Path1Decision or
    /// Path2Decision. Same constraint as above.
    /// </summary>
    RejectedProcessLabelsForbidden,

    /// <summary>
    /// Color was provided but didn't match the 7-char #rrggbb pattern
    /// the <c>CK_block_catalog_color</c> constraint expects.
    /// </summary>
    RejectedInvalidColor,
}

/// <summary>
/// Result of <see cref="IBlockCatalogRepository.CreateAsync"/>. On
/// <see cref="CreateBlockCatalogOutcome.Created"/> the <see cref="Id"/>
/// is populated; otherwise it's null.
/// </summary>
public sealed record CreateBlockCatalogResult(CreateBlockCatalogOutcome Outcome, int? Id);

/// <summary>Outcome of <see cref="IBlockCatalogRepository.UpdateAsync"/>.</summary>
public enum UpdateBlockCatalogOutcome
{
    Updated,
    NotFound,

    /// <summary>
    /// The caller tried to change <c>class_name</c> on a block that's
    /// referenced by at least one <c>workflow_nodes</c> row. Per design,
    /// class_name is the .NET dispatch target and changing it on an
    /// in-use block would silently alter every workflow that uses it.
    /// The admin must deactivate this block and create a new one with
    /// the new class_name instead.
    /// </summary>
    RejectedClassNameChangeBlocked,

    /// <summary>Same as <see cref="CreateBlockCatalogOutcome.RejectedInvalidActorType"/>.</summary>
    RejectedInvalidActorType,

    /// <summary>Same as <see cref="CreateBlockCatalogOutcome.RejectedDecisionLabelsRequired"/>.</summary>
    RejectedDecisionLabelsRequired,

    /// <summary>Same as <see cref="CreateBlockCatalogOutcome.RejectedProcessLabelsForbidden"/>.</summary>
    RejectedProcessLabelsForbidden,

    /// <summary>Same as <see cref="CreateBlockCatalogOutcome.RejectedInvalidColor"/>.</summary>
    RejectedInvalidColor,
}

/// <summary>Outcome of <see cref="IBlockCatalogRepository.SetActiveAsync"/>.</summary>
public enum SetActiveOutcome
{
    Updated,
    NotFound,
}

/// <summary>
/// Read and authoring access to the <c>block_catalog</c> table — the
/// IT-authored list of Process/Decision blocks the designer can drop
/// onto a workflow canvas.
/// </summary>
/// <remarks>
/// Authoring was added in Phase 5 cleanup to support an admin page;
/// before that, rows were maintained by hand via SQL. The runtime
/// workflow engine (Phase 6+) will use <see cref="GetByIdAsync"/> to
/// resolve <see cref="BlockCatalog.ClassName"/> during execution.
///
/// Blocks are never deleted — only deactivated via
/// <see cref="SetActiveAsync"/>. Workflow nodes may reference inactive
/// rows (FK still resolves), but the picker dialog hides them.
///
/// Changing <see cref="BlockCatalog.ClassName"/> on an in-use block is
/// refused (<see cref="UpdateBlockCatalogOutcome.RejectedClassNameChangeBlocked"/>);
/// the admin must deactivate the block and create a new one with the
/// new class. Other fields (name, description, color, labels, actor)
/// are editable freely.
/// </remarks>
public interface IBlockCatalogRepository
{
    /// <summary>
    /// Returns all active catalog entries, ordered by node type ascending
    /// then name ascending. Inactive rows are excluded — they're retained
    /// in the table for FK integrity but should never appear in the
    /// workflow designer's block picker.
    /// </summary>
    Task<IReadOnlyList<BlockCatalog>> ListActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns ALL catalog entries (active and inactive), same ordering
    /// as <see cref="ListActiveAsync"/>. Used by the admin Blocks page,
    /// which needs to show inactive rows so the admin can reactivate.
    /// </summary>
    Task<IReadOnlyList<BlockCatalog>> ListAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a single catalog entry by id, or null if it doesn't exist.
    /// Loaded by the edit dialog on open; will also be used by the Phase
    /// 6+ runtime engine to resolve the block's class name.
    /// </summary>
    Task<BlockCatalog?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new catalog row. Validates node_type, actor_type, the
    /// decision-labels constraint, and the color format in C# before
    /// reaching the DB so the caller gets a clean outcome enum rather
    /// than a SqlException.
    /// </summary>
    Task<CreateBlockCatalogResult> CreateAsync(BlockCatalog seed, CancellationToken ct = default);

    /// <summary>
    /// Updates the editable fields of an existing block. node_type_id is
    /// NEVER updated — changing a Process block to a Decision block
    /// would break existing workflow_nodes referencing it. class_name is
    /// only updatable when no workflow_nodes row references the block
    /// (otherwise <see cref="UpdateBlockCatalogOutcome.RejectedClassNameChangeBlocked"/>).
    /// </summary>
    Task<UpdateBlockCatalogOutcome> UpdateAsync(BlockCatalog edited, CancellationToken ct = default);

    /// <summary>
    /// Toggles <c>is_active</c>. Inactive blocks are hidden from the
    /// workflow designer's block picker but remain referenceable by
    /// existing workflow_nodes.
    /// </summary>
    Task<SetActiveOutcome> SetActiveAsync(int id, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// Counts <c>workflow_nodes</c> rows that reference this block.
    /// Used by the edit dialog to decide whether to disable the
    /// class_name field. Returns 0 when the block isn't referenced
    /// (class_name is freely editable).
    /// </summary>
    Task<int> CountWorkflowNodeReferencesAsync(int blockId, CancellationToken ct = default);
}
