using IndustrialIot.Cloud.Api;
using IndustrialIot.Cloud.Commands;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Health;
using IndustrialIot.Cloud.Ingestion;
using IndustrialIot.Cloud.Live;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Cloud.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud;

public static class CloudServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Cloud platform. Every options type validates on start, and the database is migrated
    /// before any other hosted service runs.
    /// </summary>
    public static IServiceCollection AddCloudPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName)).ValidateOnStart();
        services.AddOptions<EdgesOptions>().Bind(configuration.GetSection("Cloud")).ValidateOnStart();
        services.AddOptions<CommandOptions>().Bind(configuration.GetSection(CommandOptions.SectionName)).ValidateOnStart();
        services.AddOptions<RetentionOptions>().Bind(configuration.GetSection(RetentionOptions.SectionName)).ValidateOnStart();
        services.AddOptions<HealthOptions>().Bind(configuration.GetSection(HealthOptions.SectionName)).ValidateOnStart();
        services.AddOptions<ForwardedHeadersSettings>().Bind(configuration.GetSection(ForwardedHeadersSettings.SectionName)).ValidateOnStart();

        services.TryAddSingleton<CloudOptionsValidator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, CloudOptionsValidator>(p => p.GetRequiredService<CloudOptionsValidator>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EdgesOptions>, CloudOptionsValidator>(p => p.GetRequiredService<CloudOptionsValidator>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CommandOptions>, CloudOptionsValidator>(p => p.GetRequiredService<CloudOptionsValidator>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RetentionOptions>, CloudOptionsValidator>(p => p.GetRequiredService<CloudOptionsValidator>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HealthOptions>, CloudOptionsValidator>(p => p.GetRequiredService<CloudOptionsValidator>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ForwardedHeadersSettings>, CloudOptionsValidator>(p => p.GetRequiredService<CloudOptionsValidator>()));

        services.TryAddSingleton(TimeProvider.System);

        // Interceptors registered in DI (e.g. test fault injection) are attached to every context.
        services.AddDbContext<CloudDbContext>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString)
            .AddInterceptors(provider.GetServices<IInterceptor>()));

        // Must stay the first hosted service: nothing may use the database before it is migrated.
        services.AddHostedService<DatabaseMigrator>();

        services.AddOptions<CloudMqttOptions>().Bind(configuration.GetSection(CloudMqttOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CloudMqttOptions>, CloudMqttOptionsValidator>());

        // One instance serves as both the hosted connection loop and the API other services call.
        services.TryAddSingleton<CloudMqttConnection>();
        services.AddHostedService(p => p.GetRequiredService<CloudMqttConnection>());
        services.AddHostedService<TelemetryIngestor>();
        services.AddHostedService<RetentionJob>();

        services.TryAddSingleton<ReadinessState>();
        services.AddHostedService<ReadinessMonitor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpointModule, HealthEndpoints>());

        services.AddCloudSecurity(configuration);
        services.AddCloudProblemDetails();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpointModule, EdgesEndpoints>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpointModule, TelemetryEndpoints>());

        services.AddSignalR();
        services.TryAddSingleton<LiveNotifier>();

        services.AddScoped<CommandService>();
        services.AddHostedService<CommandResultIngestor>();
        services.AddHostedService<CommandTimeoutSweeper>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEndpointModule, CommandEndpoints>());

        return services;
    }
}
