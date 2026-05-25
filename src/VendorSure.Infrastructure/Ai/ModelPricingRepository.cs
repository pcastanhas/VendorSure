using Dapper;
using VendorSure.Domain.Ai;
using VendorSure.Services.Ai;
using VendorSure.Services.Data;

namespace VendorSure.Infrastructure.Ai;

internal sealed class ModelPricingRepository : IModelPricingRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ModelPricingRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<ModelPricing?> GetCurrentForModelAsync(
        string modelName,
        DateOnly asOf,
        CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                id                          AS Id,
                model_name                  AS ModelName,
                effective_from              AS EffectiveFrom,
                effective_to                AS EffectiveTo,
                input_per_million_usd       AS InputPerMillionUsd,
                output_per_million_usd      AS OutputPerMillionUsd
            FROM dbo.model_pricing
            WHERE model_name = @modelName
              AND effective_from <= @asOf
              AND (effective_to IS NULL OR effective_to > @asOf);";

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var command = new CommandDefinition(
            sql,
            new { modelName, asOf },
            cancellationToken: ct);
        return await connection.QuerySingleOrDefaultAsync<ModelPricing>(command);
    }
}
