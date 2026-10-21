using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Commands;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Messaging;
using IndustrialIot.Edge.Monitoring;
using IndustrialIot.Edge.Polling;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge;

public static class EdgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything the Edge agent needs. Every options type validates on start, so a bad
    /// configuration stops the host before any PLC or broker I/O.
    /// </summary>
    public static IServiceCollection AddEdgeAgent(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var edgeSection = configuration.GetSection(EdgeOptions.SectionName);

        services.AddOptions<EdgeOptions>().Bind(edgeSection).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EdgeOptions>, EdgeOptionsValidator>());

        services.AddSiemensS7Driver(configuration);

        // Bound from "Edge" (not "Edge:Tags"): the array Edge:Tags maps to PlcTagCatalogOptions.Tags.
        services.AddOptions<PlcTagCatalogOptions>().Bind(edgeSection).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PlcTagCatalogOptions>, PlcTagCatalogOptionsValidator>());
        services.TryAddSingleton(provider => new PlcTagCatalog(provider.GetRequiredService<IOptions<PlcTagCatalogOptions>>().Value));

        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<BufferOptions>().Bind(configuration.GetSection(BufferOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BufferOptions>, BufferOptionsValidator>());
        services.TryAddSingleton<EdgeDatabase>();
        services.TryAddSingleton<TelemetryStore>();

        // Must stay the first hosted service: it opens the database before anything uses it.
        services.AddHostedService<EdgeDatabaseStarter>();

        services.TryAddSingleton<LiveTagSnapshot>();
        services.AddOptions<PollOptions>().Bind(configuration.GetSection(PollOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PollOptions>, PollOptionsValidator>());
        services.AddHostedService<TagPoller>();

        services.AddOptions<MqttOptions>().Bind(configuration.GetSection(MqttOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MqttOptions>, MqttOptionsValidator>());
        services.TryAddSingleton<MqttTopics>();
        services.TryAddSingleton<MqttConnection>();
        services.AddHostedService(provider => provider.GetRequiredService<MqttConnection>());
        services.AddHostedService<TelemetryPublisher>();

        // Off by default (read-only Edge); the listener checks it at start, so the same gate holds in every host.
        services.AddOptions<CommandsOptions>().Bind(configuration.GetSection(CommandsOptions.SectionName)).ValidateOnStart();
        services.TryAddSingleton<CommandLog>();
        services.TryAddSingleton<CommandExecutor>();
        services.AddHostedService<CommandListener>();

        services.AddOptions<MonitorOptions>().Bind(configuration.GetSection(MonitorOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MonitorOptions>, MonitorOptionsValidator>());
        services.AddHostedService<MonitorServer>();

        return services;
    }
}

/// <summary>Builds the catalog once at start so an unsafe catalog fails the host instead of the first write.</summary>
internal sealed class PlcTagCatalogOptionsValidator : IValidateOptions<PlcTagCatalogOptions>
{
    public ValidateOptionsResult Validate(string? name, PlcTagCatalogOptions options)
    {
        try
        {
            _ = new PlcTagCatalog(options);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail($"{EdgeOptions.SectionName}:Tags is invalid. {ex.Message}");
        }
    }
}
