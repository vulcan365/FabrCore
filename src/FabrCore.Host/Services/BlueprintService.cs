using FabrCore.Core;
using FabrCore.Core.Blueprints;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Services;

public sealed class FabrCoreBlueprintApplyResult
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public int TotalRequested { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public List<AgentHealthStatus> Results { get; set; } = [];
}

public interface IFabrCoreBlueprintService
{
    Task<FabrCoreBlueprintApplyResult> ApplyAsync(
        string principalId,
        FabrCoreBlueprint blueprint,
        HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
        CancellationToken cancellationToken = default);

    Task<FabrCoreBlueprint?> GetAsync(
        string principalId,
        string name,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListAsync(
        string principalId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string principalId,
        FabrCoreBlueprint blueprint,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string principalId,
        string name,
        CancellationToken cancellationToken = default);
}

internal sealed class FabrCoreBlueprintService(
    IFabrCoreAgentService agentService,
    IEnumerable<IBlueprintExpander> expanders,
    IUserScopedFabrCoreStorageProvider storage,
    ILogger<FabrCoreBlueprintService> logger) : IFabrCoreBlueprintService
{
    private const string Container = "fabrcore.blueprints";
    private const string IndexKey = "_index";
    private readonly IReadOnlyDictionary<string, IBlueprintExpander> expanders =
        expanders.ToDictionary(item => item.ExtensionKey, StringComparer.OrdinalIgnoreCase);

    public async Task<FabrCoreBlueprintApplyResult> ApplyAsync(
        string principalId,
        FabrCoreBlueprint blueprint,
        HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principalId);
        ArgumentNullException.ThrowIfNull(blueprint);

        var configurations = blueprint.Agents?.ToList() ?? [];
        foreach (var (key, extension) in blueprint.Extensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expanders.TryGetValue(key, out var expander))
            {
                throw new ArgumentException(
                    $"Blueprint extension '{key}' is not registered on this host.",
                    nameof(blueprint));
            }

            var expansion = await expander.ExpandAsync(
                new BlueprintExpansionContext
                {
                    PrincipalId = principalId,
                    Blueprint = blueprint
                },
                extension,
                cancellationToken);
            configurations.AddRange(expansion.Agents);
        }

        if (configurations.Count == 0)
        {
            throw new ArgumentException(
                "Blueprint must contain at least one agent or registered extension.",
                nameof(blueprint));
        }

        var results = await agentService.EnsureAgentsAsync(principalId, configurations, detailLevel);
        return new FabrCoreBlueprintApplyResult
        {
            Name = blueprint.Name,
            Version = blueprint.Version,
            TotalRequested = configurations.Count,
            SuccessCount = results.Count(result => result.State == HealthState.Healthy),
            FailureCount = results.Count(result => result.State != HealthState.Healthy),
            Results = results
        };
    }

    public Task<FabrCoreBlueprint?> GetAsync(
        string principalId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principalId);
        return storage.GetAsync<FabrCoreBlueprint>(
            principalId, Container, NormalizeName(name), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principalId);
        return await storage.GetAsync<List<string>>(
                   principalId, Container, IndexKey, cancellationToken)
               ?? [];
    }

    public async Task SaveAsync(
        string principalId,
        FabrCoreBlueprint blueprint,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principalId);
        ArgumentNullException.ThrowIfNull(blueprint);
        var name = NormalizeName(blueprint.Name);
        blueprint.Name = name;

        await storage.UpsertAsync(principalId, Container, name, blueprint, cancellationToken);
        var index = (await storage.GetAsync<List<string>>(
                         principalId, Container, IndexKey, cancellationToken)
                     ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (index.Add(name))
        {
            await storage.UpsertAsync(
                principalId, Container, IndexKey, index.Order().ToList(), cancellationToken);
        }

        logger.LogInformation(
            "Saved FabrCore blueprint {Blueprint} for principal {Principal}.",
            name,
            principalId);
    }

    public async Task<bool> DeleteAsync(
        string principalId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principalId);
        var normalized = NormalizeName(name);
        var deleted = await storage.DeleteAsync(
            principalId, Container, normalized, cancellationToken);
        if (!deleted)
        {
            return false;
        }

        var index = await storage.GetAsync<List<string>>(
            principalId, Container, IndexKey, cancellationToken) ?? [];
        if (index.RemoveAll(item =>
                string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await storage.UpsertAsync(
                principalId, Container, IndexKey, index, cancellationToken);
        }

        return true;
    }

    private static void ValidatePrincipal(string principalId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

    private static string NormalizeName(string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > 128
            || normalized.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Blueprint name must be 1-128 letters, digits, '.', '-', or '_'.",
                nameof(name));
        }

        return normalized;
    }
}
