using Dapper;
using VendorSure.Domain.Workflows;
using VendorSure.Services.Data;
using VendorSure.Services.Workflows;

namespace VendorSure.Infrastructure.Workflows;

internal sealed class BlockCatalogRepository : IBlockCatalogRepository
{
    private const string SelectColumns = @"
        id              AS Id,
        node_type_id    AS NodeTypeId,
        description     AS Description,
        class_name      AS ClassName,
        is_active       AS IsActive,
        color           AS Color";

    private readonly IDbConnectionFactory _connectionFactory;

    public BlockCatalogRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<BlockCatalog>> ListActiveAsync(
        CancellationToken ct = default)
    {
        // Order matches how the palette renders: group by node type so
        // Process blocks cluster together and Decision blocks cluster
        // together, then alphabetic within each group so the palette
        // is stable across sessions.
        const string sql = @"
            SELECT " + SelectColumns + @"
            FROM dbo.block_catalog
            WHERE is_active = 1
            ORDER BY node_type_id ASC, description ASC;";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(sql, cancellationToken: ct);
        var rows = await connection.QueryAsync<BlockCatalog>(command);
        return rows.ToList();
    }
}
