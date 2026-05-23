using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VendorSure.Infrastructure.Configuration;
using VendorSure.Infrastructure.Data;
using VendorSure.Infrastructure.Documents;
using VendorSure.Infrastructure.Identity;
using VendorSure.Infrastructure.RequestTypes;
using VendorSure.Services.Configuration;
using VendorSure.Services.Data;
using VendorSure.Services.Documents;
using VendorSure.Services.Identity;
using VendorSure.Services.RequestTypes;

namespace VendorSure.Infrastructure;

/// <summary>
/// Single entry point the host app uses to wire up Infrastructure-layer
/// services. Keeps the UI / Workers ignorant of concrete implementations.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVendorSureInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(
            configuration.GetSection(DatabaseOptions.SectionName));

        services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
        services.AddHostedService<DatabaseReachabilityCheck>();

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserGroupRepository, UserGroupRepository>();
        services.AddScoped<IDocumentTypeRepository, DocumentTypeRepository>();
        services.AddScoped<IRequestTypeRepository, RequestTypeRepository>();
        services.AddScoped<IRequestTypeVersionRepository, RequestTypeVersionRepository>();
        services.AddScoped<IRequestTypeRequiredDocumentRepository, RequestTypeRequiredDocumentRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();

        return services;
    }
}
