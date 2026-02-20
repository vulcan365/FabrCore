using Microsoft.Extensions.DependencyInjection;

namespace Fabr.Sdk.Memory;

/// <summary>
/// Extension methods for registering Fabr memory services in DI.
/// </summary>
public static class MemoryServiceExtensions
{
    /// <summary>
    /// Adds Fabr memory services (IMemoryStore, IMemoryToolFactory) to the service collection.
    /// </summary>
    public static IServiceCollection AddFabrMemory(this IServiceCollection services, Action<MemoryOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IMemoryStore, SqlServerMemoryStore>();
        services.AddSingleton<IMemoryToolFactory, MemoryToolFactory>();
        return services;
    }
}
