using FabrCore.Core.CloudServer;
using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FabrCore.Host.Services.CloudServer;

internal enum CloudConfigurationFetchStatus
{
    Success,
    NotModified,
    Failed
}

internal sealed record CloudConfigurationFetchResult(
    CloudConfigurationFetchStatus Status,
    CloudConfigurationEnvelope? Envelope,
    string? Error)
{
    public static CloudConfigurationFetchResult Success(CloudConfigurationEnvelope envelope) =>
        new(CloudConfigurationFetchStatus.Success, envelope, null);

    public static CloudConfigurationFetchResult NotModified() =>
        new(CloudConfigurationFetchStatus.NotModified, null, null);

    public static CloudConfigurationFetchResult Failed(string error) =>
        new(CloudConfigurationFetchStatus.Failed, null, error);
}

/// <summary>
/// Typed HTTP client for the cloud server protocol (see docs/cloud-server-protocol.md).
/// All wire JSON uses camelCase (<see cref="JsonSerializerOptions.Web"/>).
/// </summary>
internal sealed class CloudServerApiClient
{
    public const string HttpClientName = "FabrCore.CloudServer";

    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly IHttpClientFactory httpClientFactory;
    private readonly CloudServerOptions options;
    private readonly ILogger<CloudServerApiClient> logger;

    public CloudServerApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<CloudServerOptions> options,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<CloudServerApiClient> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options.Value;
        this.logger = logger;

        var orleans = configuration.GetSection(OrleansClusterOptions.SectionName).Get<OrleansClusterOptions>()
            ?? new OrleansClusterOptions();
        EffectiveClusterId = string.IsNullOrWhiteSpace(this.options.ClusterId)
            ? orleans.ClusterId
            : this.options.ClusterId;
        EffectiveEnvironment = string.IsNullOrWhiteSpace(this.options.Environment)
            ? environment.EnvironmentName
            : this.options.Environment;
        ServiceId = orleans.ServiceId;
    }

    /// <summary>The cluster id sent to the cloud server (explicit option or Orleans ClusterId).</summary>
    public string EffectiveClusterId { get; }

    /// <summary>The environment name sent to the cloud server (explicit option or host environment).</summary>
    public string EffectiveEnvironment { get; }

    /// <summary>The Orleans service id, reported in heartbeats.</summary>
    public string ServiceId { get; }

    public async Task<CloudConfigurationFetchResult> FetchConfigurationAsync(
        string? currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, BuildUri(CloudServerProtocol.ConfigurationPath));
            ApplyHeaders(request);
            if (!string.IsNullOrEmpty(currentVersion))
            {
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{currentVersion}\""));
            }

            using var timeout = CreateTimeout(cancellationToken, out var token);
            using var response = await SendAsync(request, token);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return CloudConfigurationFetchResult.NotModified();
            }

            if (!response.IsSuccessStatusCode)
            {
                return CloudConfigurationFetchResult.Failed(
                    $"Cloud server returned {(int)response.StatusCode} ({response.StatusCode}) from {CloudServerProtocol.ConfigurationPath}.");
            }

            var envelope = await response.Content.ReadFromJsonAsync<CloudConfigurationEnvelope>(JsonOptions, token);
            if (envelope is null)
            {
                return CloudConfigurationFetchResult.Failed("Cloud server returned an empty configuration envelope.");
            }

            if (envelope.SchemaVersion > CloudServerProtocol.CurrentSchemaVersion)
            {
                return CloudConfigurationFetchResult.Failed(
                    $"Cloud server envelope schemaVersion {envelope.SchemaVersion} is newer than the supported version " +
                    $"{CloudServerProtocol.CurrentSchemaVersion}. Update the FabrCore host or configure a compatible server.");
            }

            return CloudConfigurationFetchResult.Success(envelope);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CloudConfigurationFetchResult.Failed(
                $"Cloud server configuration request timed out after {options.RequestTimeout}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CloudConfigurationFetchResult.Failed($"Cloud server configuration request failed: {ex.Message}");
        }
    }

    public async Task<CloudHeartbeatResponse?> SendHeartbeatAsync(
        CloudHeartbeatRequest heartbeat, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(CloudServerProtocol.HeartbeatPath))
            {
                Content = JsonContent.Create(heartbeat, options: JsonOptions)
            };
            ApplyHeaders(request);

            using var timeout = CreateTimeout(cancellationToken, out var token);
            using var response = await SendAsync(request, token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Cloud server heartbeat returned {StatusCode} from {Path}",
                    (int)response.StatusCode, CloudServerProtocol.HeartbeatPath);
                return null;
            }

            if (response.Content.Headers.ContentLength is 0)
            {
                return new CloudHeartbeatResponse();
            }

            return await response.Content.ReadFromJsonAsync<CloudHeartbeatResponse>(JsonOptions, token)
                ?? new CloudHeartbeatResponse();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Cloud server heartbeat failed");
            return null;
        }
    }

    public async Task<CloudAdminCommand?> PollAdminCommandAsync(
        string hostInstanceId,
        CancellationToken cancellationToken = default)
    {
        var waitSeconds = Math.Max(1, (int)options.Connect.PollWait.TotalSeconds);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(
                $"{CloudServerProtocol.ConnectPath}?waitSeconds={waitSeconds}" +
                $"&hostInstanceId={Uri.EscapeDataString(hostInstanceId)}"));
        ApplyHeaders(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Connect.PollWait + TimeSpan.FromSeconds(10));
        using var response = await SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var command = await response.Content.ReadFromJsonAsync<CloudAdminCommand>(JsonOptions, timeout.Token);
        return command ?? throw new InvalidOperationException(
            "Cloud server returned an empty connect-channel command.");
    }

    public async Task SendAdminCommandResponseAsync(
        CloudAdminCommandResponse commandResponse,
        string hostInstanceId = "(unknown)",
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(CloudServerProtocol.ConnectResponsePath(commandResponse.CommandId)))
        {
            Content = JsonContent.Create(commandResponse, options: JsonOptions)
        };
        ApplyHeaders(request);
        request.Headers.TryAddWithoutValidation("X-FabrCore-Host-Instance-Id", hostInstanceId);

        using var timeout = CreateTimeout(cancellationToken, out var token);
        using var response = await SendAsync(request, token);
        response.EnsureSuccessStatusCode();
    }

    private Uri BuildUri(string relativePath)
    {
        var baseUrl = options.Url.TrimEnd('/');
        return new Uri($"{baseUrl}{relativePath}", UriKind.Absolute);
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Headers.TryAddWithoutValidation(CloudServerProtocol.ClusterIdHeader, EffectiveClusterId);
        request.Headers.TryAddWithoutValidation(CloudServerProtocol.EnvironmentHeader, EffectiveEnvironment);
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken, out CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(options.RequestTimeout);
        token = source.Token;
        return source;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
}
