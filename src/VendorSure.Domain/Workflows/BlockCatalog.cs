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
    /// Short label (a couple of words) shown in the picker dialog's
    /// primary line and rendered as the label on each node body on the
    /// canvas. Compare with <see cref="Description"/>, which is the
    /// longer prose shown as a secondary line in the picker and as a
    /// hover tooltip on the node.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Longer prose describing what the block does. Shown beneath the
    /// <see cref="Name"/> as a secondary line in the picker dialog and
    /// as a native SVG &lt;title&gt; hover tooltip on the node body.
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

    /// <summary>
    /// Label shown on the canvas at the Decision diamond's left
    /// (path1) outgoing vertex. Block-level semantics: "True",
    /// "Approved", "Clean", etc. — whatever the block's code emits
    /// for path1. Always populated for Decision blocks; always NULL
    /// for Process blocks. Enforced by
    /// <c>CK_block_catalog_decision_labels</c>.
    /// </summary>
    public string? Path1Decision { get; init; }

    /// <summary>
    /// Label shown on the canvas at the Decision diamond's right
    /// (path2) outgoing vertex. Same shape as
    /// <see cref="Path1Decision"/>; populated only for Decision
    /// blocks.
    /// </summary>
    public string? Path2Decision { get; init; }
}
