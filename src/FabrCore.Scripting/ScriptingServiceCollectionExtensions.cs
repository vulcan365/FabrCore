using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FabrCore.Scripting;

public static class ScriptingServiceCollectionExtensions
{
    public static IServiceCollection AddFabrCoreScripting(this IServiceCollection services,
        Action<ScriptingRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ScriptingRuntimeOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ScriptingRuntime>();
        return services;
    }
}
