// REMOVE-BEFORE-PROD: this entire file. AddDebugIdentity is the single
// registration point for the Entra-bypass shim. Delete the call site in
// Program.cs and this file when real Entra auth lands.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VendorSure.UI.Authentication.Debug;

internal static class DebugIdentityExtensions
{
    /// <summary>
    /// Wires up the debug identity shim. Reads its config from the
    /// <c>Debug:Identity</c> section. Refuses to register when the host
    /// environment is <c>Production</c> — a belt-and-suspenders guard so
    /// even a misconfigured deployment can't accidentally honour the shim.
    /// </summary>
    public static IServiceCollection AddDebugIdentity(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Debug identity shim cannot be loaded when " +
                "ASPNETCORE_ENVIRONMENT=Production. Remove the call to " +
                "AddDebugIdentity from Program.cs and replace it with the " +
                "real Entra authentication wiring.");
        }

        services.Configure<DebugIdentityOptions>(
            configuration.GetSection(DebugIdentityOptions.SectionName));

        services
            .AddAuthentication(DebugAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<DebugAuthenticationSchemeOptions, DebugAuthenticationHandler>(
                DebugAuthenticationDefaults.AuthenticationScheme,
                _ => { });

        services.AddAuthorization();
        services.AddCascadingAuthenticationState();

        return services;
    }
}
