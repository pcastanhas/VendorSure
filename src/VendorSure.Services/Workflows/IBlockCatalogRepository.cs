using VendorSure.Domain.Workflows;

namespace VendorSure.Services.Workflows;

/// <summary>
/// Read-only access to the <c>block_catalog</c> table — the IT-authored
/// list of Process/Decision blocks the designer can drop onto a workflow
/// canvas.
/// </summary>
/// <remarks>
/// No mutations exposed: catalog rows are seeded outside the application
/// (manual SQL on the dev DB, deployment scripts in production) per
/// the design conversation. The application reads them and renders the
/// palette; it never authors them.
///
/// If a runtime ever needs to look up a catalog row by id (e.g. for
/// resolving <see cref="BlockCatalog.ClassName"/> during workflow
/// execution in Phase 6+), add a <c>GetByIdAsync</c> here. Phase 5 only
/// needs the list for the palette.
/// </remarks>
public interface IBlockCatalogRepository
{
    /// <summary>
    /// Returns all active catalog entries, ordered by node type ascending
    /// then description ascending. Inactive rows are excluded — they're
    /// retained in the table for FK integrity (existing workflow_nodes
    /// may still reference them) but should never appear in the palette.
    /// </summary>
    Task<IReadOnlyList<BlockCatalog>> ListActiveAsync(CancellationToken ct = default);
}
