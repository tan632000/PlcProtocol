namespace IndustrialIot.Cloud;

/// <summary>
/// A group of endpoints. Modules are registered in DI and mapped by <c>Program</c> after the
/// authentication/authorization middleware, so every module (including test-only ones) gets the same
/// security pipeline.
/// </summary>
public interface IEndpointModule
{
    void Map(IEndpointRouteBuilder endpoints);
}

public static class EndpointModuleExtensions
{
    public static IEndpointRouteBuilder MapEndpointModules(this IEndpointRouteBuilder endpoints)
    {
        foreach (var module in endpoints.ServiceProvider.GetServices<IEndpointModule>())
        {
            module.Map(endpoints);
        }

        return endpoints;
    }
}
