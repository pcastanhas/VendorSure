namespace VendorSure.Domain.Workflows;

/// <summary>
/// IT-authored catalog entry describing a block — the .NET class that
/// implements a Process or Decision node's runtime behavior. Each entry
/// pairs a node-type slot (Process / Decision per the
/// CK_block_catalog_node_type CHECK) with the class that implements it,
/// plus a short description and an optional color override.
/// </summary>
/// <remarks>
/// The catalog is read-only from the application's perspective: rows are
/// seeded manually on the dev DB (and via deployment scripts in production)
/// by the IT team. The designer pulls active rows to populate the palette;
/// dragging an entry onto the canvas creates a <c>workflow_nodes</c> row
/// pointing back at the catalog id.
///
/// Phase 5 / Chunk 6 introduces this entity for the palette. Phase 6+
/// will read <see cref="ClassName"/> to instantiate and execute the block
/// at runtime.
/// </remarks>
public sealed class BlockCatalog
{
    public int Id { get; init; }

    /// <summary>
    /// The node-type slot this block fills — Process (2) or Decision (3).
    /// Constrained by CK_block_catalog_node_type to those two values.
    /// </summary>
    public int NodeTypeId { get; init; }

    /// <summary>
    /// Short human-readable label shown on the palette and the canvas
    /// (eventually — Chunk 5 still shows the generic node-type label).
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Fully-qualified .NET class name implementing the block. Not used
    /// by the designer; reserved for Phase 6+ runtime execution.
    /// </summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>
    /// Whether this entry should appear in the palette. Inactive rows
    /// are retained for FK integrity (existing workflow_nodes may still
    /// reference them) but hidden from new authoring.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Optional 7-character hex color (e.g. <c>#3366cc</c>) overriding
    /// the node-type default fill. Null means "use the node-type default."
    /// </summary>
    public string? Color { get; init; }
}
