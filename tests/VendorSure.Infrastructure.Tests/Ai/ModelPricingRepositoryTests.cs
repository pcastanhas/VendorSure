using Dapper;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Domain.Ai;
using VendorSure.Services.Ai;
using VendorSure.Services.Data;

namespace VendorSure.Infrastructure.Tests.Ai;

/// <summary>
/// Integration tests for the model_pricing repository. Talks to the dev DB.
/// Each test inserts its own _test_-prefixed model rows and cleans them up.
/// The schema's UNIQUE (model_name, effective_from) means tests must use
/// disambiguated model names to avoid clashes with each other or the seeded
/// claude-haiku-4-5 row.
/// </summary>
public sealed class ModelPricingRepositoryTests : IClassFixture<InfrastructureTestFixture>
{
    private readonly IModelPricingRepository _pricing;
    private readonly IDbConnectionFactory _connectionFactory;

    public ModelPricingRepositoryTests(InfrastructureTestFixture fixture)
    {
        _pricing = fixture.ServiceProvider.GetRequiredService<IModelPricingRepository>();
        _connectionFactory = fixture.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    }

    [Fact]
    public async Task GetCurrentForModelAsync_returns_null_when_no_row()
    {
        var result = await _pricing.GetCurrentForModelAsync(
            "no-such-model-promise", DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentForModelAsync_returns_currently_effective_row()
    {
        var modelName = NewTestModelName();
        int id = await InsertPricingAsync(
            modelName,
            effectiveFrom: new DateOnly(2025, 1, 1),
            effectiveTo: null,
            inputPerMillion: 1.23m,
            outputPerMillion: 4.56m);
        try
        {
            var result = await _pricing.GetCurrentForModelAsync(
                modelName, DateOnly.FromDateTime(DateTime.UtcNow));
            Assert.NotNull(result);
            Assert.Equal(modelName, result!.ModelName);
            Assert.Equal(new DateOnly(2025, 1, 1), result.EffectiveFrom);
            Assert.Null(result.EffectiveTo);
            Assert.Equal(1.23m, result.InputPerMillionUsd);
            Assert.Equal(4.56m, result.OutputPerMillionUsd);
        }
        finally
        {
            await DeletePricingAsync(id);
        }
    }

    [Fact]
    public async Task GetCurrentForModelAsync_returns_null_when_only_row_is_expired()
    {
        var modelName = NewTestModelName();
        var id = await InsertPricingAsync(
            modelName,
            effectiveFrom: new DateOnly(2020, 1, 1),
            effectiveTo: new DateOnly(2021, 1, 1),
            inputPerMillion: 1.0m,
            outputPerMillion: 5.0m);
        try
        {
            var result = await _pricing.GetCurrentForModelAsync(
                modelName, DateOnly.FromDateTime(DateTime.UtcNow));
            Assert.Null(result);
        }
        finally
        {
            await DeletePricingAsync(id);
        }
    }

    [Fact]
    public async Task GetCurrentForModelAsync_returns_open_ended_row_when_history_exists()
    {
        // Simulates the documented rate-change procedure: an old row stamped
        // with effective_to + a new row with effective_from = same date,
        // effective_to NULL.
        var modelName = NewTestModelName();
        var oldId = await InsertPricingAsync(
            modelName,
            effectiveFrom: new DateOnly(2024, 1, 1),
            effectiveTo: new DateOnly(2025, 6, 1),
            inputPerMillion: 2.0m,
            outputPerMillion: 8.0m);
        var newId = await InsertPricingAsync(
            modelName,
            effectiveFrom: new DateOnly(2025, 6, 1),
            effectiveTo: null,
            inputPerMillion: 3.0m,
            outputPerMillion: 9.0m);
        try
        {
            var result = await _pricing.GetCurrentForModelAsync(
                modelName, DateOnly.FromDateTime(DateTime.UtcNow));
            Assert.NotNull(result);
            // Should be the newer (open-ended) row.
            Assert.Equal(3.0m, result!.InputPerMillionUsd);
            Assert.Equal(9.0m, result.OutputPerMillionUsd);
            Assert.Null(result.EffectiveTo);
        }
        finally
        {
            await DeletePricingAsync(newId);
            await DeletePricingAsync(oldId);
        }
    }

    // ---------- helpers ----------

    private static string NewTestModelName()
        => "_test_model_" + Guid.NewGuid().ToString("N")[..8];

    private async Task<int> InsertPricingAsync(
        string modelName,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        decimal inputPerMillion,
        decimal outputPerMillion)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO dbo.model_pricing
                (model_name, effective_from, effective_to, input_per_million_usd, output_per_million_usd)
            OUTPUT INSERTED.id
            VALUES (@modelName, @effectiveFrom, @effectiveTo, @inputPerMillion, @outputPerMillion);",
            new { modelName, effectiveFrom, effectiveTo, inputPerMillion, outputPerMillion });
    }

    private async Task DeletePricingAsync(int id)
    {
        using var conn = await _connectionFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM dbo.model_pricing WHERE id = @id;", new { id });
    }
}
