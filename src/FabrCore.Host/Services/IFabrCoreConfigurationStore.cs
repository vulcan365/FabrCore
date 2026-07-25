using FabrCore.Core;

namespace FabrCore.Host.Services;

/// <summary>
/// Provides the host's model/API-key configuration (the fabrcore.json payload). The default
/// implementation reads the local fabrcore.json file; when the Cloud Server feature is
/// enabled the configuration is pulled from a remote server instead. Hosts can plug in a
/// custom source via <c>FabrCoreServerOptions.UseConfigurationStore&lt;T&gt;()</c>.
/// </summary>
public interface IFabrCoreConfigurationStore
{
    /// <summary>Returns the current configuration snapshot.</summary>
    Task<FabrCoreConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this store accepts writes. Read-only sources (for example cloud-delivered
    /// configuration) return false, and <see cref="SaveConfigurationAsync"/> throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    bool SupportsWrites { get; }

    /// <summary>Persists the given configuration when <see cref="SupportsWrites"/> is true.</summary>
    Task SaveConfigurationAsync(FabrCoreConfiguration configuration, CancellationToken cancellationToken = default);
}
