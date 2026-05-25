using VendorSure.Domain.Configuration;
using VendorSure.Services.Configuration;

namespace VendorSure.Infrastructure.Tests.Documents;

/// <summary>
/// Minimal in-memory <see cref="ISettingsRepository"/> for storage tests. Only
/// the read surface (<see cref="GetByKeyAsync"/>) is needed —
/// <see cref="LocalDiskDocumentStorage"/> never writes settings. The other
/// two interface methods (<c>GetAllAsync</c>, <c>UpdateValueAsync</c>) throw
/// so a future caller that starts using them gets a clear failure rather
/// than silent surprise.
/// </summary>
internal sealed class FakeSettingsRepository : ISettingsRepository
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public void Set(string key, string? value) => _values[key] = value;

    public Task<IReadOnlyList<Setting>> GetAllAsync(CancellationToken ct = default)
        => throw new NotSupportedException("FakeSettingsRepository only supports GetByKeyAsync.");

    public Task<Setting?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        if (!_values.TryGetValue(key, out var value))
        {
            return Task.FromResult<Setting?>(null);
        }

        var setting = new Setting
        {
            Id = 0,
            Key = key,
            Description = string.Empty,
            Required = true,
            Sensitive = false,
            Value = value,
        };
        return Task.FromResult<Setting?>(setting);
    }

    public Task<bool> UpdateValueAsync(string key, string? value, CancellationToken ct = default)
        => throw new NotSupportedException("FakeSettingsRepository only supports GetByKeyAsync.");
}
