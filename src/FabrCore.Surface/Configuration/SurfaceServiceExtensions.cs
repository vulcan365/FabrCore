using FabrCore.Surface.Abstractions;
using FabrCore.Surface.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FabrCore.Surface.Configuration;

public static class SurfaceServiceExtensions
{
    /// <summary>
    /// Registers agent-side Surface planning/rendering services for hosts and proxy agents.
    /// </summary>
    public static IServiceCollection AddFabrCoreSurfaceServices(
        this IServiceCollection services,
        Action<SurfaceAiOptions>? configure = null)
    {
        var options = new SurfaceAiOptions();
        if (configure is not null)
        {
            configure(options);
            services.RemoveAll<SurfaceAiOptions>();
            services.AddSingleton(options);
        }
        else
        {
            services.TryAddSingleton(options);
        }

        services.AddOptions<SurfaceOptions>();
        services.TryAddSingleton<ISurfaceDefinitionProvider, FileSurfaceDefinitionProvider>();
        services.TryAddSingleton<ISurfaceProvider, SurfaceProvider>();

        return services;
    }
}
