using FabrCore.Core;
using FabrCore.Sdk;

namespace FabrCore.Services.GraphRag.EvalConsole;

internal sealed class LocalModelResolver(FabrCoreConfiguration models) : IFabrCoreModelConfigurationResolver
{
    public Task<ModelConfiguration> GetModelConfigurationAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(models.ModelConfigurations.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{name}' is not configured."));

    public Task<string> GetApiKeyAsync(string alias, CancellationToken cancellationToken = default)
    {
        var value = models.ApiKeys.FirstOrDefault(k => k.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(value) || value.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"API key alias '{alias}' is not configured.");
        return Task.FromResult(value);
    }
}
