// REMOVE-BEFORE-PROD: this entire file. The handler authenticates every
// request as a hard-configured user, bypassing Entra completely. Delete
// before any production deployment.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VendorSure.Services.Identity;

namespace VendorSure.UI.Authentication.Debug;

/// <summary>
/// The scheme name registered against <c>AddAuthentication</c>. Anything
/// inside the app that needs to reference the scheme by name (rare) imports
/// this constant.
/// </summary>
internal static class DebugAuthenticationDefaults
{
    public const string AuthenticationScheme = "Debug";
}

/// <summary>
/// Empty options bag — the real configuration lives on
/// <see cref="DebugIdentityOptions"/>, which the handler reads via DI.
/// AuthenticationHandler{TOptions} requires its TOptions to derive from
/// AuthenticationSchemeOptions, hence this otherwise-trivial class.
/// </summary>
internal sealed class DebugAuthenticationSchemeOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// On every request, looks up the user whose <c>entraid</c> matches
/// <see cref="DebugIdentityOptions.Entraid"/> and builds a
/// <see cref="ClaimsPrincipal"/> for them. Cached per-user-per-app-lifetime
/// — restart to switch users (or use the multi-user picker once it exists).
/// </summary>
internal sealed class DebugAuthenticationHandler
    : AuthenticationHandler<DebugAuthenticationSchemeOptions>
{
    private readonly DebugIdentityOptions _debugIdentity;
    private readonly IUserRepository _users;

    // Lazy-loaded after the first successful lookup. Holds the principal so
    // we don't hit the DB on every request.
    private static ClaimsPrincipal? _cachedPrincipal;
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    public DebugAuthenticationHandler(
        IOptionsMonitor<DebugAuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IOptions<DebugIdentityOptions> debugIdentity,
        IUserRepository users)
        : base(options, loggerFactory, encoder)
    {
        _debugIdentity = debugIdentity.Value;
        _users = users;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_debugIdentity.Enabled)
        {
            return AuthenticateResult.NoResult();
        }

        if (string.IsNullOrWhiteSpace(_debugIdentity.Entraid))
        {
            Logger.LogWarning(
                "Debug identity shim is enabled but Debug:Identity:Entraid is empty.");
            return AuthenticateResult.Fail("Debug identity Entraid not configured.");
        }

        var principal = await GetOrLoadPrincipalAsync();
        if (principal is null)
        {
            return AuthenticateResult.Fail(
                $"No user found with entraid '{_debugIdentity.Entraid}'.");
        }

        var ticket = new AuthenticationTicket(
            principal,
            DebugAuthenticationDefaults.AuthenticationScheme);
        return AuthenticateResult.Success(ticket);
    }

    private async Task<ClaimsPrincipal?> GetOrLoadPrincipalAsync()
    {
        if (_cachedPrincipal is not null)
        {
            return _cachedPrincipal;
        }

        await _cacheLock.WaitAsync(Context.RequestAborted);
        try
        {
            if (_cachedPrincipal is not null)
            {
                return _cachedPrincipal;
            }

            var user = await _users.GetByEntraidAsync(
                _debugIdentity.Entraid,
                Context.RequestAborted);

            if (user is null)
            {
                Logger.LogError(
                    "Debug identity shim cannot authenticate: " +
                    "no user found with entraid '{Entraid}'. " +
                    "Insert a row into the users table and restart, " +
                    "or update Debug:Identity:Entraid in appsettings.json.",
                    _debugIdentity.Entraid);
                return null;
            }

            if (!user.IsActive)
            {
                Logger.LogError(
                    "Debug identity shim cannot authenticate: " +
                    "user '{Entraid}' is marked is_active = 0.",
                    _debugIdentity.Entraid);
                return null;
            }

            var identity = new ClaimsIdentity(
                claims: new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Entraid),
                    new Claim(ClaimTypes.Name, user.Name),
                    new Claim("user_id", user.Id.ToString()),
                    new Claim("group_id", user.GroupId.ToString()),
                    new Claim("is_admin", user.IsAdmin ? "true" : "false"),
                },
                authenticationType: DebugAuthenticationDefaults.AuthenticationScheme,
                nameType: ClaimTypes.Name,
                roleType: ClaimTypes.Role);

            _cachedPrincipal = new ClaimsPrincipal(identity);

            Logger.LogInformation(
                "Debug identity shim authenticated as '{Name}' (entraid={Entraid}, id={Id}). " +
                "REMOVE-BEFORE-PROD.",
                user.Name, user.Entraid, user.Id);

            return _cachedPrincipal;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
