using FabrCore.Core;
using FabrCore.Surface.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceBlueprintClient : ISurfaceBlueprintClient
{
    private readonly HttpClient httpClient;
    private readonly SurfaceOptions options;
    private readonly ILogger<SurfaceBlueprintClient> logger;

    public SurfaceBlueprintClient(
        HttpClient httpClient,
        IOptions<SurfaceOptions> options,
        ILogger<SurfaceBlueprintClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<SurfaceBlueprintDocument?> GetAsync(
        string principalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var listUrl = BuildBlueprintResourceUrl();
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, listUrl);
        AddOwnerHeaders(listRequest, principalId);
        using var listResponse = await httpClient.SendAsync(listRequest, cancellationToken);
        listResponse.EnsureSuccessStatusCode();
        var names = await listResponse.Content.ReadFromJsonAsync<List<string>>(
            SurfaceJson.Options,
            cancellationToken) ?? [];
        var name = names.FirstOrDefault();
        if (name is null)
        {
            return null;
        }

        var url = $"{BuildBlueprintResourceUrl()}/{Uri.EscapeDataString(name)}";
        logger.LogDebug("Loading FabrCore blueprint from {Url}.", url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddOwnerHeaders(request, principalId);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SurfaceBlueprintDocument>(SurfaceJson.Options, cancellationToken);
    }

    public async Task SaveAsync(
        string principalId,
        SurfaceBlueprintDocument blueprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentNullException.ThrowIfNull(blueprint);

        var name = string.IsNullOrWhiteSpace(blueprint.Name) ? "default" : blueprint.Name.Trim();
        var url = $"{BuildBlueprintResourceUrl()}/{Uri.EscapeDataString(name)}";
        logger.LogDebug("Saving FabrCore blueprint to {Url}.", url);

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        AddOwnerHeaders(request, principalId);
        request.Content = JsonContent.Create(blueprint, options: SurfaceJson.Options);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<SurfaceBlueprintApplyResult> ApplyAsync(
        string principalId,
        SurfaceBlueprintDocument blueprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentNullException.ThrowIfNull(blueprint);

        var url = BuildBlueprintUrl();
        logger.LogDebug("Applying Surface blueprint for {PrincipalId} at {Url}.", principalId, url);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddOwnerHeaders(request, principalId);
        request.Content = JsonContent.Create(blueprint, options: SurfaceJson.Options);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var agentResponse = await response.Content.ReadFromJsonAsync<SurfaceAgentBlueprintResponse>(
            SurfaceJson.Options,
            cancellationToken);

        var results = agentResponse?.Results.Select(ToAgentHealthStatus).ToList() ?? [];

        return new SurfaceBlueprintApplyResult
        {
            Name = agentResponse?.Name ?? blueprint.Name,
            Version = agentResponse?.Version ?? blueprint.Version,
            TotalRequested = agentResponse?.TotalRequested ?? blueprint.Agents.Count,
            SuccessCount = agentResponse?.SuccessCount ?? 0,
            FailureCount = agentResponse?.FailureCount ?? 0,
            Results = results,
            AgentConfigurationsRequested = agentResponse?.TotalRequested ?? blueprint.Agents.Count
        };
    }

    private string BuildBlueprintResourceUrl()
        => $"{BuildHostUrl()}/fabrcoreapi/Blueprint";

    private string BuildBlueprintUrl()
        => $"{BuildHostUrl()}/fabrcoreapi/Agent/blueprint";

    private string BuildHostUrl()
    {
        if (string.IsNullOrWhiteSpace(options.FabrCoreHostUrl))
        {
            throw new InvalidOperationException(
                $"{nameof(SurfaceOptions.FabrCoreHostUrl)} must be configured before using Surface blueprints.");
        }

        return options.FabrCoreHostUrl.TrimEnd('/');
    }

    private static void AddOwnerHeaders(HttpRequestMessage request, string principalId)
    {
        request.Headers.TryAddWithoutValidation("x-user", principalId);
        request.Headers.TryAddWithoutValidation("x-user-handle", principalId);
    }

    private static AgentHealthStatus ToAgentHealthStatus(SurfaceAgentBlueprintResult result)
        => new()
        {
            Handle = result.Handle ?? string.Empty,
            State = ParseHealthState(result.State),
            Timestamp = DateTime.UtcNow,
            IsConfigured = result.IsConfigured,
            Message = result.Message,
            AgentType = result.AgentType
        };

    private static HealthState ParseHealthState(JsonElement state)
    {
        if (state.ValueKind == JsonValueKind.Number && state.TryGetInt32(out var numericState))
        {
            return Enum.IsDefined(typeof(HealthState), numericState)
                ? (HealthState)numericState
                : HealthState.NotConfigured;
        }

        if (state.ValueKind != JsonValueKind.String)
        {
            return HealthState.NotConfigured;
        }

        var value = state.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return HealthState.NotConfigured;
        }

        if (Enum.TryParse<HealthState>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return NormalizeStateToken(value) switch
        {
            "ready" or "configured" or "ok" or "success" or "succeeded" => HealthState.Healthy,
            "warning" or "needsattention" => HealthState.Degraded,
            "failed" or "failure" or "error" or "unavailable" => HealthState.Unhealthy,
            "starting" or "pending" or "unknown" => HealthState.NotConfigured,
            _ => HealthState.NotConfigured
        };
    }

    private static string NormalizeStateToken(string value)
        => value
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
}
