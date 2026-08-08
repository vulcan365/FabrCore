using FabrCore.Core;
using FabrCore.Sdk;

namespace FabrCore.Host.Services;

/// <summary>
/// Resolves model configuration directly from the Host's active configuration store.
/// </summary>
internal sealed class ConfigurationStoreModelConfigurationResolver(
    IFabrCoreConfigurationStore configurationStore) : IFabrCoreModelConfigurationResolver
{
    public async Task<ModelConfiguration> GetModelConfigurationAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var configuration = await configurationStore.GetConfigurationAsync(cancellationToken);
        return configuration.ModelConfigurations.FirstOrDefault(model => model.Name == name)
            ?? throw new KeyNotFoundException(
                $"Model configuration '{name}' was not found in the active FabrCore configuration store.");
    }

    public async Task<string> GetApiKeyAsync(
        string alias,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var configuration = await configurationStore.GetConfigurationAsync(cancellationToken);
        return configuration.ApiKeys.FirstOrDefault(apiKey => apiKey.Alias == alias)?.Value
            ?? throw new KeyNotFoundException(
                $"API key alias '{alias}' was not found in the active FabrCore configuration store.");
    }
}
