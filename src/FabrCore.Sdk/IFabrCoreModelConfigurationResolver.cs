using FabrCore.Core;

namespace FabrCore.Sdk;

/// <summary>
/// Resolves model configuration and provider credentials for FabrCore chat clients.
/// Host processes register a configuration-store-backed implementation; standalone SDK
/// processes use the authenticated FabrCore Host API.
/// </summary>
public interface IFabrCoreModelConfigurationResolver
{
    /// <summary>Gets a named model configuration.</summary>
    Task<ModelConfiguration> GetModelConfigurationAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the provider API key associated with an alias.</summary>
    Task<string> GetApiKeyAsync(
        string alias,
        CancellationToken cancellationToken = default);
}
