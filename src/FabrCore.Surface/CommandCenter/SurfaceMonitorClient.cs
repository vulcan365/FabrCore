using FabrCore.Surface.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FabrCore.Surface.CommandCenter;

public sealed class SurfaceMonitorClient : ISurfaceMonitorClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true)
        }
    };

    private readonly HttpClient httpClient;
    private readonly SurfaceOptions options;
    private readonly ILogger<SurfaceMonitorClient> logger;

    public SurfaceMonitorClient(
        HttpClient httpClient,
        IOptions<SurfaceOptions> options,
        ILogger<SurfaceMonitorClient> logger)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.logger = logger;
    }

    public Task<SurfaceMonitorTokenResponse> GetTokenSummariesAsync(
        string principalId,
        CancellationToken cancellationToken = default)
        => GetAsync<SurfaceMonitorTokenResponse>("tokens", principalId, cancellationToken);

    public Task<SurfaceMonitorMessagesResponse> GetMessagesAsync(
        string principalId,
        string? agentHandle = null,
        int limit = 25,
        CancellationToken cancellationToken = default)
        => GetAsync<SurfaceMonitorMessagesResponse>(
            $"messages?limit={NormalizeLimit(limit)}{AgentQuery(agentHandle)}",
            principalId,
            cancellationToken);

    public Task<SurfaceMonitorEventsResponse> GetEventsAsync(
        string principalId,
        string? agentHandle = null,
        int limit = 25,
        CancellationToken cancellationToken = default)
        => GetAsync<SurfaceMonitorEventsResponse>(
            $"events?limit={NormalizeLimit(limit)}{AgentQuery(agentHandle)}",
            principalId,
            cancellationToken);

    public Task<SurfaceMonitorLlmCallsResponse> GetLlmCallsAsync(
        string principalId,
        string? agentHandle = null,
        int limit = 25,
        bool failedOnly = false,
        CancellationToken cancellationToken = default)
        => GetAsync<SurfaceMonitorLlmCallsResponse>(
            $"llm-calls?limit={NormalizeLimit(limit)}&failedOnly={failedOnly.ToString().ToLowerInvariant()}{AgentQuery(agentHandle)}",
            principalId,
            cancellationToken);

    public Task<SurfaceMonitorErrorsResponse> GetErrorsAsync(
        string principalId,
        string? agentHandle = null,
        int limit = 25,
        CancellationToken cancellationToken = default)
        => GetAsync<SurfaceMonitorErrorsResponse>(
            $"errors?limit={NormalizeLimit(limit)}{AgentQuery(agentHandle)}",
            principalId,
            cancellationToken);

    public Task<SurfaceMonitorConfigResponse> GetConfigAsync(
        string principalId,
        CancellationToken cancellationToken = default)
        => GetAsync<SurfaceMonitorConfigResponse>("config", principalId, cancellationToken);

    public Task<SurfaceMonitorConfigResponse> UpdateConfigAsync(
        string principalId,
        SurfaceMonitorConfigUpdate update,
        CancellationToken cancellationToken = default)
        => SendAsync<SurfaceMonitorConfigResponse>(
            HttpMethod.Post,
            "config",
            principalId,
            update,
            cancellationToken);

    private async Task<T> GetAsync<T>(string pathAndQuery, string principalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        if (string.IsNullOrWhiteSpace(options.FabrCoreHostUrl))
        {
            throw new InvalidOperationException(
                $"{nameof(SurfaceOptions.FabrCoreHostUrl)} must be configured before loading monitor data.");
        }

        var url = $"{options.FabrCoreHostUrl.TrimEnd('/')}/fabrcoreapi/Monitor/{pathAndQuery}";
        logger.LogDebug("Loading Surface monitor data from {Url}.", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddOwnerHeaders(request, principalId);

        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException($"Failed to deserialize monitor response for '{pathAndQuery}'.");
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string pathAndQuery,
        string principalId,
        object? body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        if (string.IsNullOrWhiteSpace(options.FabrCoreHostUrl))
        {
            throw new InvalidOperationException(
                $"{nameof(SurfaceOptions.FabrCoreHostUrl)} must be configured before updating monitor data.");
        }

        var url = $"{options.FabrCoreHostUrl.TrimEnd('/')}/fabrcoreapi/Monitor/{pathAndQuery}";
        logger.LogDebug("Sending Surface monitor request to {Url}.", url);

        using var request = new HttpRequestMessage(method, url);
        AddOwnerHeaders(request, principalId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException($"Failed to deserialize monitor response for '{pathAndQuery}'.");
    }

    private static void AddOwnerHeaders(HttpRequestMessage request, string principalId)
    {
        request.Headers.TryAddWithoutValidation("x-user", principalId);
        request.Headers.TryAddWithoutValidation("x-user-handle", principalId);
    }

    private static string AgentQuery(string? agentHandle)
        => string.IsNullOrWhiteSpace(agentHandle)
            ? string.Empty
            : $"&agentHandle={Uri.EscapeDataString(agentHandle)}";

    private static int NormalizeLimit(int limit)
        => Math.Clamp(limit, 1, 1000);
}
