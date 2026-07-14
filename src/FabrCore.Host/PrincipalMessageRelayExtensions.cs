using FabrCore.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FabrCore.Host;

/// <summary>Registration helpers for external principal delivery providers.</summary>
public static class PrincipalMessageRelayExtensions
{
    /// <summary>
    /// Registers a relay as a discoverable singleton. Provider packages should call
    /// this from their own service-registration extension.
    /// </summary>
    public static IServiceCollection AddPrincipalMessageRelay<TRelay>(
        this IServiceCollection services)
        where TRelay : class, IPrincipalMessageRelay
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPrincipalMessageRelay, TRelay>());
        return services;
    }
}
