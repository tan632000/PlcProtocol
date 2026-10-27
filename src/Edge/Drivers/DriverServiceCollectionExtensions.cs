using IndustrialIot.Edge.Commands;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Slmp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Drivers;

public static class DriverServiceCollectionExtensions
{
    /// <summary>
    /// Registers the driver chosen by <c>Edge:Plc:Protocol</c>. An unknown or unavailable protocol registers no
    /// driver and throws here, at composition: the host resolves hosted services before startup validation, so a
    /// later failure would surface as a missing <see cref="IPlcDriver"/> instead of the named key.
    /// </summary>
    public static IServiceCollection AddPlcProtocol(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Raw reads: a malformed Edge:Commands:Enabled stays the CommandsOptions binder's failure at start.
        var protocolOptions = new PlcProtocolOptions();
        if (configuration[PlcProtocolOptions.KeyName] is { } rawProtocol)
        {
            protocolOptions.Protocol = rawProtocol;
        }

        var commandsOptions = new CommandsOptions
        {
            Enabled = bool.TryParse(configuration[$"{CommandsOptions.SectionName}:Enabled"], out var enabled) && enabled,
        };
        var verdict = new PlcProtocolOptionsValidator(Options.Create(commandsOptions)).Validate(null, protocolOptions);
        if (verdict.Failed)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(PlcProtocolOptions), verdict.Failures);
        }

        services.AddOptions<PlcProtocolOptions>()
            .Bind(configuration.GetSection(PlcProtocolOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PlcProtocolOptions>, PlcProtocolOptionsValidator>());

        // The check above accepted the value, so it parses.
        PlcProtocolOptions.TryParse(protocolOptions.Protocol, out var protocol);
        if (protocol == PlcProtocol.Slmp)
        {
            services.AddMitsubishiSlmpDriver(configuration);
        }
        else
        {
            services.TryAddSingleton<IPlcAddressSyntax, S7AddressSyntax>();
            services.AddSiemensS7Driver(configuration);
        }

        return services;
    }

    /// <summary>
    /// Registers <see cref="MitsubishiSlmpDriver"/> as the singleton <see cref="IPlcDriver"/> with its options,
    /// validator and address syntax. A singleton matters: the driver owns the only connection and the request lock.
    /// </summary>
    public static IServiceCollection AddMitsubishiSlmpDriver(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SlmpOptions>()
            .Bind(configuration.GetSection(SlmpOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SlmpOptions>, SlmpOptionsValidator>());

        services.TryAddSingleton<IPlcAddressSyntax, SlmpAddressSyntax>();
        services.TryAddSingleton<MitsubishiSlmpDriver>();
        services.TryAddSingleton<IPlcDriver>(provider => provider.GetRequiredService<MitsubishiSlmpDriver>());
        return services;
    }

    /// <summary>
    /// Registers <see cref="SiemensS7Driver"/> as the singleton <see cref="IPlcDriver"/>. A singleton matters:
    /// the driver owns the only connection and the lock that serializes PLC operations.
    /// </summary>
    public static IServiceCollection AddSiemensS7Driver(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SiemensS7Options>()
            .Bind(configuration.GetSection(SiemensS7Options.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SiemensS7Options>, SiemensS7OptionsValidator>());

        services.TryAddSingleton<SiemensS7Driver>();
        services.TryAddSingleton<IPlcDriver>(provider => provider.GetRequiredService<SiemensS7Driver>());
        return services;
    }
}
