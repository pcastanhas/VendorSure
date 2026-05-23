using Microsoft.Extensions.DependencyInjection;
using VendorSure.Services.Configuration;

namespace VendorSure.Infrastructure.Tests.Configuration;

/// <summary>
/// Integration tests for the settings repository. Talks to the dev DB.
/// The tests pick a known-seeded key (<c>AI.Polling.IntervalMinutes</c>)
/// and exercise the read/update surface against it, restoring the original
/// value in a <c>try/finally</c> so the dev DB ends in the same state.
///
/// If a test crashes mid-flight the original value may be left modified —
/// re-running data-model.sql's settings INSERT section or manually setting
/// the value back to 5 recovers.
/// </summary>
public sealed class SettingsRepositoryTests : IClassFixture<InfrastructureTestFixture>
{
    private const string ProbeKey = "AI.Polling.IntervalMinutes";
    private const string OriginalSeedValue = "5";

    private readonly ISettingsRepository _repository;

    public SettingsRepositoryTests(InfrastructureTestFixture fixture)
    {
        _repository = fixture.ServiceProvider.GetRequiredService<ISettingsRepository>();
    }

    [Fact]
    public async Task GetAllAsync_returns_seeded_rows()
    {
        var all = await _repository.GetAllAsync();

        Assert.NotEmpty(all);
        // The §16 seed inserts 10 rows; this assertion is loose so adding
        // future settings doesn't break the test.
        Assert.True(all.Count >= 10, $"Expected at least 10 seeded settings; got {all.Count}.");
        Assert.Contains(all, s => s.Key == ProbeKey);
    }

    [Fact]
    public async Task GetByKeyAsync_returns_row_for_seeded_key()
    {
        var setting = await _repository.GetByKeyAsync(ProbeKey);

        Assert.NotNull(setting);
        Assert.Equal(ProbeKey, setting!.Key);
        Assert.True(setting.Required);
        Assert.False(setting.Sensitive);
    }

    [Fact]
    public async Task GetByKeyAsync_returns_null_for_unknown_key()
    {
        var setting = await _repository.GetByKeyAsync("NoSuchKey.IPromise");

        Assert.Null(setting);
    }

    [Fact]
    public async Task UpdateValueAsync_round_trips_a_value()
    {
        var original = await _repository.GetByKeyAsync(ProbeKey);
        Assert.NotNull(original);

        try
        {
            const string newValue = "7";
            var updated = await _repository.UpdateValueAsync(ProbeKey, newValue);
            Assert.True(updated);

            var after = await _repository.GetByKeyAsync(ProbeKey);
            Assert.NotNull(after);
            Assert.Equal(newValue, after!.Value);
        }
        finally
        {
            await _repository.UpdateValueAsync(ProbeKey, original!.Value);
        }
    }

    [Fact]
    public async Task UpdateValueAsync_returns_false_for_unknown_key()
    {
        var updated = await _repository.UpdateValueAsync("NoSuchKey.IPromise", "x");

        Assert.False(updated);
    }

    [Fact]
    public async Task UpdateValueAsync_accepts_null_value()
    {
        var original = await _repository.GetByKeyAsync(ProbeKey);
        Assert.NotNull(original);

        try
        {
            var updated = await _repository.UpdateValueAsync(ProbeKey, null);
            Assert.True(updated);

            var after = await _repository.GetByKeyAsync(ProbeKey);
            Assert.NotNull(after);
            Assert.Null(after!.Value);
        }
        finally
        {
            await _repository.UpdateValueAsync(ProbeKey, original!.Value);
        }
    }
}
