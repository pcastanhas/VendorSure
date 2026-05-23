using VendorSure.Domain.Configuration;

namespace VendorSure.Services.Configuration;

/// <summary>
/// Reads and updates the <c>settings</c> table. Rows are seeded once via
/// the schema script; this surface has no Insert / Delete.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// Returns all settings rows, ordered by key.
    /// </summary>
    Task<IReadOnlyList<Setting>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the setting with the given key, or <c>null</c> if no row
    /// has that key.
    /// </summary>
    Task<Setting?> GetByKeyAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Updates the <c>value</c> column of the row whose <c>key</c> matches.
    /// Returns <c>true</c> if a row was updated, <c>false</c> if no row
    /// with that key exists.
    /// </summary>
    /// <remarks>
    /// Does not insert a missing row. Callers that hit a <c>false</c>
    /// return are looking at an out-of-date code/schema combination — the
    /// app expects the seeded set of keys to be present.
    /// </remarks>
    Task<bool> UpdateValueAsync(string key, string? value, CancellationToken ct = default);
}
