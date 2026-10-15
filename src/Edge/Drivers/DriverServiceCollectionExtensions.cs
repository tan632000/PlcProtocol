using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Drivers;

public static class DriverServiceCollectionExtensions
{
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
