using System.Collections.Concurrent;
using FabrCore.Sdk;
using FabrCore.Surface.Abstractions;
using FabrCore.Surface.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Surface.Services;

internal sealed class SurfaceProvider : ISurfaceProvider
{
    private readonly ConcurrentDictionary<string, ISurfaceService> services = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISurfaceDefinitionProvider definitionProvider;
    private readonly SurfaceAiOptions aiOptions;
    private readonly SurfaceOptions surfaceOptions;
    private readonly ILoggerFactory loggerFactory;

    public SurfaceProvider(
        ISurfaceDefinitionProvider definitionProvider,
        SurfaceAiOptions aiOptions,
        IOptions<SurfaceOptions> surfaceOptions,
        ILoggerFactory loggerFactory)
    {
        this.definitionProvider = definitionProvider;
        this.aiOptions = aiOptions;
        this.surfaceOptions = surfaceOptions.Value;
        this.loggerFactory = loggerFactory;
    }

    public async Task<ISurfaceService> GetSurfaceServiceAsync(
        IFabrCoreAgentHost agentHost,
        string agentHandle,
        string? configName,
        AIAgent? planningAgent = null,
        SurfaceAgentFactory? agentFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentHandle);

        var definitionName = string.IsNullOrWhiteSpace(configName)
            ? aiOptions.DefaultSurfaceDefinitionName
            : configName!;

        var cacheKey = $"{agentHandle}|{definitionName}";
        if (services.TryGetValue(cacheKey, out var existing))
        {
            return existing;
        }

        var definition = await definitionProvider.GetByNameAsync(definitionName, cancellationToken)
                         ?? new SurfaceDefinition { Name = definitionName };

        var service = new SurfaceService(
            agentHandle,
            agentHost,
            planningAgent,
            agentFactory,
            definition,
            aiOptions,
            surfaceOptions,
            loggerFactory.CreateLogger<SurfaceService>());

        return services.GetOrAdd(cacheKey, service);
    }
}
