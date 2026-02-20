using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Sdk.Memory;

/// <summary>
/// Extension methods for registering FabrCore memory services in DI.
/// </summary>
public static class MemoryServiceExtensions
{
    /// <summary>
    /// Adds FabrCore memory services (IMemoryStore, IMemoryToolFactory) to the service collection.
    /// </summary>
    public static IServiceCollection AddFabrCoreMemory(this IServiceCollection services, Action<MemoryOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IMemoryStore, SqlServerMemoryStore>();
        services.AddSingleton<IMemoryToolFactory, MemoryToolFactory>();
        return services;
    }
}
