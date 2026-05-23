using System.Data;

namespace VendorSure.Services.Data;

/// <summary>
/// Creates open connections to the VendorSure database.
/// </summary>
/// <remarks>
/// Implementations live in <c>VendorSure.Infrastructure</c>. Callers use the
/// returned connection inside a <c>using</c> block; the factory does not
/// pool or track connections beyond what the underlying provider does.
/// </remarks>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Opens and returns a new connection to the configured database.
    /// </summary>
    /// <remarks>
    /// Throws whatever the underlying provider throws when the database is
    /// unreachable or the connection string is rejected (typically a
    /// provider-specific exception, e.g. <c>SqlException</c>).
    /// </remarks>
    Task<IDbConnection> CreateOpenConnectionAsync(CancellationToken ct = default);
}
