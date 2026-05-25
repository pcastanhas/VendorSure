using Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VendorSure.Infrastructure.Ai;
using VendorSure.Infrastructure.Configuration;
using VendorSure.Infrastructure.Data;
using VendorSure.Infrastructure.Documents;
using VendorSure.Infrastructure.Identity;
using VendorSure.Infrastructure.RequestTypes;
using VendorSure.Infrastructure.Workflows;
using VendorSure.Services.Ai;
using VendorSure.Services.Configuration;
using VendorSure.Services.Data;
using VendorSure.Services.Documents;
using VendorSure.Services.Identity;
using VendorSure.Services.RequestTypes;
using VendorSure.Services.Workflows;

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

        services.Configure<AnthropicOptions>(
            configuration.GetSection(AnthropicOptions.SectionName));

        services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
        services.AddHostedService<DatabaseReachabilityCheck>();

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserGroupRepository, UserGroupRepository>();
        services.AddScoped<IDocumentTypeRepository, DocumentTypeRepository>();
        services.AddScoped<IRequestTypeRepository, RequestTypeRepository>();
        services.AddScoped<IRequestTypeVersionRepository, RequestTypeVersionRepository>();
        services.AddScoped<IRequestTypeRequiredDocumentRepository, RequestTypeRequiredDocumentRepository>();
        services.AddScoped<IRequestTypeValidationRepository, RequestTypeValidationRepository>();
        services.AddScoped<IRequestTypeValidationDocumentRepository, RequestTypeValidationDocumentRepository>();
        services.AddScoped<IWorkflowDefinitionRepository, WorkflowDefinitionRepository>();
        services.AddScoped<IWorkflowNodeRepository, WorkflowNodeRepository>();
        services.AddScoped<IBlockCatalogRepository, BlockCatalogRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<IDocumentStorage, LocalDiskDocumentStorage>();

        // AI services.
        services.AddScoped<IAiUsageRepository, AiUsageRepository>();
        services.AddScoped<IModelPricingRepository, ModelPricingRepository>();

        // The Anthropic SDK's AnthropicClient manages its own HttpClient and
        // connection pool. A singleton is appropriate; it reads the API key
        // from AnthropicOptions at construction time. If the key is missing
        // (e.g. dev box without secrets set), construction is deferred until
        // first injection — at which point the InvalidOperationException
        // gives a clear "set Anthropic:ApiKey in user secrets" error rather
        // than a startup crash that prevents the rest of the app from running.
        services.AddSingleton<AnthropicClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
            if (string.IsNullOrWhiteSpace(opts.ApiKey))
            {
                throw new InvalidOperationException(
                    "Anthropic:ApiKey is not configured. Set it via " +
                    "'dotnet user-secrets set Anthropic:ApiKey sk-ant-...' in dev, " +
                    "or via the Anthropic__ApiKey environment variable / secret store in deployed environments.");
            }
            return new AnthropicClient { ApiKey = opts.ApiKey };
        });

        services.AddScoped<IAiService, AnthropicAiService>();

        return services;
    }
}
