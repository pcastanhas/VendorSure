using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Infrastructure;

namespace VendorSure.Infrastructure.Tests;

/// <summary>
/// Shared xUnit fixture that wires the same DI graph the running app uses,
/// pointed at the dev DB. Used as <c>IClassFixture&lt;InfrastructureTestFixture&gt;</c>
/// on each test class.
/// </summary>
/// <remarks>
/// Tests look for <c>appsettings.Test.json</c> in the test project's output
/// folder. Copy <c>appsettings.Test.example.json</c> to that name and fill
/// in the connection string before running.
///
/// If <c>appsettings.Test.json</c> is absent, the fixture throws on first
/// resolution — the test classes can then choose to skip via xUnit's
/// <c>[SkippableFact]</c> mechanism (not currently wired) or simply fail
/// loudly so the dev knows to set it up.
/// </remarks>
public sealed class InfrastructureTestFixture : IDisposable
{
    public IServiceProvider ServiceProvider { get; }

    public InfrastructureTestFixture()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Test.json");
        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException(
                $"Test configuration file not found at '{configPath}'. " +
                $"Copy appsettings.Test.example.json to appsettings.Test.json and " +
                $"fill in your dev DB connection string before running these tests.");
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Test.json", optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddVendorSureInfrastructure(configuration);

        ServiceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
