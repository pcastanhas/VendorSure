using MudBlazor.Services;
using Serilog;
using VendorSure.Infrastructure;
using VendorSure.UI.Authentication.Debug; // REMOVE-BEFORE-PROD
using VendorSure.UI.Components;

// Bootstrap logger: catches anything that explodes before host config is read.
// Replaced below once configuration is available.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("VendorSure UI starting up");

    var builder = WebApplication.CreateBuilder(args);

    // Serilog takes over from the default ASP.NET logger. Config comes from
    // appsettings.json under the "Serilog" section.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddMudServices();

    // Connection factory + startup reachability check.
    builder.Services.AddVendorSureInfrastructure(builder.Configuration);

    // REMOVE-BEFORE-PROD: debug identity shim that bypasses Entra by stamping
    // every request as the user whose entraid is configured in appsettings.
    // Hard-refuses to load in Production.
    builder.Services.AddDebugIdentity(builder.Configuration, builder.Environment);

    var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();

    app.UseSerilogRequestLogging();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    Log.Information("VendorSure UI ready — environment {Environment}", app.Environment.EnvironmentName);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "VendorSure UI terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
