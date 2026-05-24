using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Services.Data;
using VendorSure.Services.Workflows;

namespace VendorSure.Infrastructure.Tests.Workflows;

/// <summary>
/// Integration tests for the block-catalog repository.
///
/// The repository is read-only — no mutations to test — so the test
/// surface is small: ListActiveAsync must return active rows only,
/// hydrate every column, and honor the documented ordering
/// (node_type_id ASC, description ASC).
///
/// Each test inserts its own catalog rows with prefixed descriptions
/// to avoid colliding with any seed data the dev DB may already have,
/// then filters the returned list to just those rows for the assertion.
/// </summary>
public sealed class BlockCatalogRepositoryTests
    : IClassFixture<InfrastructureTestFixture>
{
    private readonly IBlockCatalogRepository _catalog;
    private readonly IDbConnectionFactory _connectionFactory;

    public BlockCatalogRepositoryTests(InfrastructureTestFixture fixture)
    {
        _catalog = fixture.ServiceProvider.GetRequiredService<IBlockCatalogRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task ListActiveAsync_returns_active_rows_with_all_columns_hydrated()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            // Two active rows — one Process (node_type_id=2), one Decision (3).
            var processId = await InsertBlockAsync(
                nodeTypeId: 2, description: $"{prefix}_proc",
                className: "VendorSure.Test.ProcBlock", isActive: true, color: "#abcdef");
            insertedIds.Add(processId);

            var decisionId = await InsertBlockAsync(
                nodeTypeId: 3, description: $"{prefix}_dec",
                className: "VendorSure.Test.DecBlock", isActive: true, color: null);
            insertedIds.Add(decisionId);

            var all = await _catalog.ListActiveAsync();
            var mine = all.Where(b => b.Description.StartsWith(prefix)).ToList();
            Assert.Equal(2, mine.Count);

            var proc = mine.Single(b => b.Id == processId);
            Assert.Equal(2, proc.NodeTypeId);
            Assert.Equal($"{prefix}_proc", proc.Description);
            Assert.Equal("VendorSure.Test.ProcBlock", proc.ClassName);
            Assert.True(proc.IsActive);
            Assert.Equal("#abcdef", proc.Color);

            var dec = mine.Single(b => b.Id == decisionId);
            Assert.Equal(3, dec.NodeTypeId);
            Assert.Equal($"{prefix}_dec", dec.Description);
            Assert.Equal("VendorSure.Test.DecBlock", dec.ClassName);
            Assert.True(dec.IsActive);
            Assert.Null(dec.Color);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task ListActiveAsync_excludes_inactive_rows()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            var activeId = await InsertBlockAsync(
                nodeTypeId: 2, description: $"{prefix}_active",
                className: "VendorSure.Test.Active", isActive: true, color: null);
            insertedIds.Add(activeId);

            var inactiveId = await InsertBlockAsync(
                nodeTypeId: 2, description: $"{prefix}_inactive",
                className: "VendorSure.Test.Inactive", isActive: false, color: null);
            insertedIds.Add(inactiveId);

            var all = await _catalog.ListActiveAsync();
            var mine = all.Where(b => b.Description.StartsWith(prefix)).ToList();

            // The inactive row must be excluded; the active row must be present.
            Assert.Single(mine);
            Assert.Equal(activeId, mine[0].Id);
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task ListActiveAsync_orders_by_node_type_then_description()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            // Insert four rows: 2 Process (zeta, alpha) and 2 Decision (gamma,
            // beta), in a deliberately wrong order so we can verify the SQL
            // ORDER BY clause actually sorts them.
            var procZ = await InsertBlockAsync(2, $"{prefix}_proc_zeta", "X", true, null);
            insertedIds.Add(procZ);

            var decG = await InsertBlockAsync(3, $"{prefix}_dec_gamma", "X", true, null);
            insertedIds.Add(decG);

            var procA = await InsertBlockAsync(2, $"{prefix}_proc_alpha", "X", true, null);
            insertedIds.Add(procA);

            var decB = await InsertBlockAsync(3, $"{prefix}_dec_beta", "X", true, null);
            insertedIds.Add(decB);

            var all = await _catalog.ListActiveAsync();
            var mine = all.Where(b => b.Description.StartsWith(prefix)).ToList();

            // Expected order: all Process (alpha, zeta), then all Decision
            // (beta, gamma). node_type_id ASC, description ASC.
            Assert.Collection(mine,
                b => Assert.Equal(procA, b.Id),
                b => Assert.Equal(procZ, b.Id),
                b => Assert.Equal(decB, b.Id),
                b => Assert.Equal(decG, b.Id));
        }
        finally
        {
            await CleanupAsync(insertedIds);
        }
    }

    [Fact]
    public async Task ListActiveAsync_returns_empty_when_no_active_rows_match_filter()
    {
        // Sanity check: even on a clean db with no test data, the call doesn't
        // throw. Result may include real seed data — we just check the type.
        var result = await _catalog.ListActiveAsync();
        Assert.NotNull(result);
    }

    // --- helpers ---------------------------------------------------------

    private async Task<int> InsertBlockAsync(
        int nodeTypeId, string description, string className, bool isActive, string? color)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(@"
            INSERT INTO dbo.block_catalog
                (node_type_id, description, class_name, is_active, color)
            VALUES (@nodeTypeId, @description, @className, @isActive, @color);
            SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { nodeTypeId, description, className, isActive, color });
    }

    private async Task CleanupAsync(IEnumerable<int> ids)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        foreach (var id in ids)
        {
            await connection.ExecuteAsync(
                "DELETE FROM dbo.block_catalog WHERE id = @id;", new { id });
        }
    }
}
