using Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using VendorSure.Domain.Ai;
using VendorSure.Infrastructure.Ai;
using VendorSure.Infrastructure.Tests.Documents; // FakeSettingsRepository
using VendorSure.Services.Ai;

namespace VendorSure.Infrastructure.Tests.Ai;

/// <summary>
/// Unit tests for <see cref="AnthropicAiService"/>. Exercise only the paths
/// that don't reach the SDK:
///   - AI.Disabled = 1   -> AiDisabledException, no ai_usage row.
///   - missing pricing   -> ModelPricingMissingException, no ai_usage row.
///   - cost computation  -> pure-function check against
///                          <see cref="AnthropicAiService.ComputeCost"/>.
///
/// SDK-touching paths (success, error mapping, timeout mapping) are
/// covered manually via the throwaway /test/ai page since they require a
/// live API key. When the validation runner lands in 6B.2 the runner's
/// own integration tests will exercise those paths end-to-end.
/// </summary>
public sealed class AnthropicAiServiceTests
{
    private static AnthropicClient DummyClient()
        => new AnthropicClient { ApiKey = "test-dummy-not-used" };

    [Fact]
    public async Task CompleteAsync_throws_AiDisabledException_when_setting_is_1()
    {
        var settings = new FakeSettingsRepository();
        settings.Set("AI.Disabled", "1");
        var usage = new FakeAiUsageRepository();
        var pricing = new FakeModelPricingRepository();

        var service = new AnthropicAiService(
            DummyClient(),
            settings,
            pricing,
            usage,
            NullLogger<AnthropicAiService>.Instance);

        await Assert.ThrowsAsync<AiDisabledException>(
            () => service.CompleteAsync("hello", new AiCallContext()));

        Assert.Empty(usage.Inserted);
    }

    [Fact]
    public async Task CompleteAsync_proceeds_past_disabled_check_when_setting_is_0()
    {
        var settings = new FakeSettingsRepository();
        settings.Set("AI.Disabled", "0");
        settings.Set("AI.Model", "claude-haiku-4-5");
        // No pricing seeded -> falls into ModelPricingMissingException, which
        // is the proof that we cleared the disabled gate.
        var usage = new FakeAiUsageRepository();
        var pricing = new FakeModelPricingRepository();

        var service = new AnthropicAiService(
            DummyClient(),
            settings,
            pricing,
            usage,
            NullLogger<AnthropicAiService>.Instance);

        await Assert.ThrowsAsync<ModelPricingMissingException>(
            () => service.CompleteAsync("hello", new AiCallContext()));
    }

    [Fact]
    public async Task CompleteAsync_throws_ModelPricingMissing_and_does_not_write_usage_row()
    {
        var settings = new FakeSettingsRepository();
        settings.Set("AI.Disabled", "0");
        settings.Set("AI.Model", "claude-haiku-4-5");
        var usage = new FakeAiUsageRepository();
        var pricing = new FakeModelPricingRepository(); // empty

        var service = new AnthropicAiService(
            DummyClient(),
            settings,
            pricing,
            usage,
            NullLogger<AnthropicAiService>.Instance);

        var ex = await Assert.ThrowsAsync<ModelPricingMissingException>(
            () => service.CompleteAsync("hello", new AiCallContext()));

        Assert.Equal("claude-haiku-4-5", ex.Model);
        Assert.Empty(usage.Inserted);
    }

    [Fact]
    public async Task CompleteAsync_throws_when_AI_Model_setting_is_missing()
    {
        var settings = new FakeSettingsRepository();
        settings.Set("AI.Disabled", "0");
        // AI.Model intentionally not set.
        var usage = new FakeAiUsageRepository();
        var pricing = new FakeModelPricingRepository();

        var service = new AnthropicAiService(
            DummyClient(),
            settings,
            pricing,
            usage,
            NullLogger<AnthropicAiService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteAsync("hello", new AiCallContext()));
    }

    // ---------- ComputeCost ----------

    [Fact]
    public void ComputeCost_typical_call_rounds_to_six_decimals()
    {
        var pricing = new ModelPricing
        {
            InputPerMillionUsd = 1.0000m,
            OutputPerMillionUsd = 5.0000m,
        };
        // 100 input + 200 output at $1/$5 per million:
        // (100 * 1 + 200 * 5) / 1_000_000 = 1100 / 1_000_000 = 0.0011
        var cost = AnthropicAiService.ComputeCost(100, 200, pricing);
        Assert.Equal(0.001100m, cost);
    }

    [Fact]
    public void ComputeCost_zero_tokens_returns_zero()
    {
        var pricing = new ModelPricing
        {
            InputPerMillionUsd = 1.0m,
            OutputPerMillionUsd = 5.0m,
        };
        Assert.Equal(0m, AnthropicAiService.ComputeCost(0, 0, pricing));
    }

    [Fact]
    public void ComputeCost_at_million_token_boundary()
    {
        var pricing = new ModelPricing
        {
            InputPerMillionUsd = 3.00m,
            OutputPerMillionUsd = 15.00m,
        };
        // 1_000_000 input + 1_000_000 output -> $3 + $15 = $18
        Assert.Equal(18m, AnthropicAiService.ComputeCost(1_000_000, 1_000_000, pricing));
    }

    [Fact]
    public void ComputeCost_high_precision_rounds_consistently()
    {
        // Pricing with 4 decimals * tokens shouldn't overshoot the column's
        // 6 decimal scale. The repo would otherwise reject a value that
        // can't fit in decimal(10,6).
        var pricing = new ModelPricing
        {
            InputPerMillionUsd = 0.1234m,
            OutputPerMillionUsd = 0.5678m,
        };
        var cost = AnthropicAiService.ComputeCost(7, 11, pricing);
        // (7 * 0.1234 + 11 * 0.5678) / 1_000_000
        //   = (0.8638 + 6.2458) / 1_000_000
        //   = 7.1096 / 1_000_000
        //   = 0.0000071096 -> rounds to 0.000007
        Assert.Equal(0.000007m, cost);
    }

    // ---------- Fakes ----------

    private sealed class FakeAiUsageRepository : IAiUsageRepository
    {
        public List<AiUsage> Inserted { get; } = new();
        private int _next = 1;

        public Task<int> InsertAsync(AiUsage row, CancellationToken ct = default)
        {
            Inserted.Add(row);
            return Task.FromResult(_next++);
        }
    }

    private sealed class FakeModelPricingRepository : IModelPricingRepository
    {
        private readonly Dictionary<string, ModelPricing> _rows = new(StringComparer.Ordinal);

        public void Add(ModelPricing row) => _rows[row.ModelName] = row;

        public Task<ModelPricing?> GetCurrentForModelAsync(
            string modelName, DateOnly asOf, CancellationToken ct = default)
        {
            _rows.TryGetValue(modelName, out var row);
            return Task.FromResult<ModelPricing?>(row);
        }
    }
}
