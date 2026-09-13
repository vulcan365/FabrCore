using FabrCore.Services.Memory.Administration;
using FabrCore.Services.Memory.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace FabrCore.Services.Memory.Configuration;
public static class RemoteMemoryServiceExtensions
{
    /// <summary>
    /// Registers the HTTP Memory administration client. Register an
    /// <see cref="IMemoryAdminPrincipalAccessor"/> that resolves the current principal.
    /// </summary>
    public static IServiceCollection AddRemoteMemoryAdministration(
        this IServiceCollection services,
        Action<MemoryAdminClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<MemoryAdminClientOptions>().Configure(configure);
        services.AddHttpClient<RemoteMemoryAdminClient>();
        services.TryAddTransient<IMemoryAdminClient, MemoryAdminClientSelector>();
        return services;
    }
}
