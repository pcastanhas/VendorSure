using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Workflows;
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
            // Two active rows — one Process (node_type_id=2) with
            // System actor, one Decision (3) with Human actor. The
            // mismatch between node type and actor type is intentional:
            // the two fields are independent (a Process block can be
            // System or Human or AI; same for Decision).
            var processId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_proc_n", description: $"{prefix}_proc",
                className: "VendorSure.Test.ProcBlock", isActive: true, color: "#abcdef",
                actorType: BlockCatalogActorType.System);
            insertedIds.Add(processId);

            var decisionId = await InsertBlockAsync(
                nodeTypeId: 3, name: $"{prefix}_dec_n", description: $"{prefix}_dec",
                className: "VendorSure.Test.DecBlock", isActive: true, color: null,
                path1Decision: "True", path2Decision: "False",
                actorType: BlockCatalogActorType.Human);
            insertedIds.Add(decisionId);

            var all = await _catalog.ListActiveAsync();
            var mine = all.Where(b => b.Description.StartsWith(prefix)).ToList();
            Assert.Equal(2, mine.Count);

            var proc = mine.Single(b => b.Id == processId);
            Assert.Equal(2, proc.NodeTypeId);
            Assert.Equal($"{prefix}_proc_n", proc.Name);
            Assert.Equal($"{prefix}_proc", proc.Description);
            Assert.Equal("VendorSure.Test.ProcBlock", proc.ClassName);
            Assert.True(proc.IsActive);
            Assert.Equal("#abcdef", proc.Color);
            Assert.Equal(BlockCatalogActorType.System, proc.ActorType);
            // Process blocks must have NULL path decisions per
            // CK_block_catalog_decision_labels.
            Assert.Null(proc.Path1Decision);
            Assert.Null(proc.Path2Decision);

            var dec = mine.Single(b => b.Id == decisionId);
            Assert.Equal(3, dec.NodeTypeId);
            Assert.Equal($"{prefix}_dec_n", dec.Name);
            Assert.Equal($"{prefix}_dec", dec.Description);
            Assert.Equal("VendorSure.Test.DecBlock", dec.ClassName);
            Assert.True(dec.IsActive);
            Assert.Null(dec.Color);
            Assert.Equal(BlockCatalogActorType.Human, dec.ActorType);
            // Decision blocks must have both path decisions populated.
            Assert.Equal("True", dec.Path1Decision);
            Assert.Equal("False", dec.Path2Decision);
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
                nodeTypeId: 2, name: $"{prefix}_active_n", description: $"{prefix}_active",
                className: "VendorSure.Test.Active", isActive: true, color: null);
            insertedIds.Add(activeId);

            var inactiveId = await InsertBlockAsync(
                nodeTypeId: 2, name: $"{prefix}_inactive_n", description: $"{prefix}_inactive",
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
    public async Task ListActiveAsync_orders_by_node_type_then_name()
    {
        var prefix = "_test_bc_" + Guid.NewGuid().ToString("N");
        var insertedIds = new List<int>();
        try
        {
            // Insert four rows: 2 Process (zeta, alpha) and 2 Decision (gamma,
            // beta), in a deliberately wrong order so we can verify the SQL
            // ORDER BY clause actually sorts them. Names drive the sort
            // post-Chunk-10-cleanup; descriptions are unrelated to ordering.
            var procZ = await InsertBlockAsync(
                2, $"{prefix}_proc_zeta", "desc z", "X", true, null);
            insertedIds.Add(procZ);

            var decG = await InsertBlockAsync(
                3, $"{prefix}_dec_gamma", "desc g", "X", true, null, "T", "F");
            insertedIds.Add(decG);

            var procA = await InsertBlockAsync(
                2, $"{prefix}_proc_alpha", "desc a", "X", true, null);
            insertedIds.Add(procA);

            var decB = await InsertBlockAsync(
                3, $"{prefix}_dec_beta", "desc b", "X", true, null, "T", "F");
            insertedIds.Add(decB);

            var all = await _catalog.ListActiveAsync();
            var mine = all.Where(b => b.Name.StartsWith(prefix)).ToList();

            // Expected order: all Process (alpha, zeta), then all Decision
            // (beta, gamma). node_type_id ASC, name ASC.
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
        int nodeTypeId, string name, string description, string className, bool isActive, string? color,
        string? path1Decision = null, string? path2Decision = null,
        BlockCatalogActorType actorType = BlockCatalogActorType.System)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(@"
            INSERT INTO dbo.block_catalog
                (node_type_id, name, description, class_name, is_active, color,
                 path1_decision, path2_decision, actor_type)
            VALUES (@nodeTypeId, @name, @description, @className, @isActive, @color,
                    @path1Decision, @path2Decision, @actorType);
            SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { nodeTypeId, name, description, className, isActive, color,
                  path1Decision, path2Decision, actorType = (int)actorType });
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
