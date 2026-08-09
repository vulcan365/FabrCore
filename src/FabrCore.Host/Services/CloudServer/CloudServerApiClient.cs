using FabrCore.Core.CloudServer;
using FabrCore.Host.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
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
    internal const int ConnectMaxAttempts = 3;
    internal static readonly TimeSpan ConnectTimeoutBuffer = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectRetryBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ConnectMaxRetryDelay = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly IHttpClientFactory httpClientFactory;
    private readonly CloudServerConnectClient connectClient;
    private readonly CloudServerOptions options;
    private readonly RemoteAdministrationOptions remoteAdministration;
    private readonly ILogger<CloudServerApiClient> logger;
    private readonly SemaphoreSlim connectPollGate = new(1, 1);

    public CloudServerApiClient(
        IHttpClientFactory httpClientFactory,
        CloudServerConnectClient connectClient,
        IOptions<CloudServerOptions> options,
        IOptions<RemoteAdministrationOptions> remoteAdministration,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<CloudServerApiClient> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.connectClient = connectClient;
        this.options = options.Value;
        this.remoteAdministration = remoteAdministration.Value;
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
        await connectPollGate.WaitAsync(cancellationToken);
        try
        {
            return await PollAdminCommandCoreAsync(hostInstanceId, cancellationToken);
        }
        finally
        {
            connectPollGate.Release();
        }
    }

    internal TimeSpan EffectiveConnectPollWait => TimeSpan.FromSeconds(
        Math.Clamp((int)remoteAdministration.PollWait.TotalSeconds, 1, 25));

    internal TimeSpan EffectiveConnectAttemptTimeout => EffectiveConnectPollWait + ConnectTimeoutBuffer;

    private async Task<CloudAdminCommand?> PollAdminCommandCoreAsync(
        string hostInstanceId,
        CancellationToken cancellationToken)
    {
        var pollWait = EffectiveConnectPollWait;
        var attemptTimeout = EffectiveConnectAttemptTimeout;
        var endpoint = BuildUri(
            $"{CloudServerProtocol.ConnectPath}?waitSeconds={(int)pollWait.TotalSeconds}" +
            $"&hostInstanceId={Uri.EscapeDataString(hostInstanceId)}");

        for (var attempt = 1; attempt <= ConnectMaxAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                ApplyHeaders(request);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(attemptTimeout);
                using var response = await SendConnectAsync(request, timeout.Token);

                if (IsTransientConnectStatus(response.StatusCode) && attempt < ConnectMaxAttempts)
                {
                    logger.LogWarning(
                        "Cloud Server connect poll retrying: endpoint {Endpoint}, configured poll duration {ConfiguredPollDuration}, " +
                        "effective poll duration {EffectivePollDuration}, effective attempt timeout {AttemptTimeout}, " +
                        "retry attempt {RetryAttempt}/{MaxAttempts}, attempt outcome {Outcome}, HTTP status {StatusCode}, " +
                        "elapsed {ElapsedMilliseconds} ms",
                        CloudServerProtocol.ConnectPath,
                        remoteAdministration.PollWait,
                        pollWait,
                        attemptTimeout,
                        attempt + 1,
                        ConnectMaxAttempts,
                        "transient-http-status",
                        (int)response.StatusCode,
                        stopwatch.ElapsedMilliseconds);
                    await DelayBeforeConnectRetryAsync(attempt, response, cancellationToken);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    logger.LogDebug(
                        "Cloud Server connect poll completed: endpoint {Endpoint}, configured poll duration {ConfiguredPollDuration}, " +
                        "effective poll duration {EffectivePollDuration}, effective attempt timeout {AttemptTimeout}, " +
                        "attempt {Attempt}/{MaxAttempts}, terminal outcome {Outcome}, elapsed {ElapsedMilliseconds} ms",
                        CloudServerProtocol.ConnectPath,
                        remoteAdministration.PollWait,
                        pollWait,
                        attemptTimeout,
                        attempt,
                        ConnectMaxAttempts,
                        "empty",
                        stopwatch.ElapsedMilliseconds);
                    return null;
                }

                response.EnsureSuccessStatusCode();
                var command = await response.Content.ReadFromJsonAsync<CloudAdminCommand>(JsonOptions, timeout.Token)
                    ?? throw new InvalidOperationException(
                        "Cloud server returned an empty connect-channel command.");
                logger.LogDebug(
                    "Cloud Server connect poll completed: endpoint {Endpoint}, configured poll duration {ConfiguredPollDuration}, " +
                    "effective poll duration {EffectivePollDuration}, effective attempt timeout {AttemptTimeout}, " +
                    "attempt {Attempt}/{MaxAttempts}, terminal outcome {Outcome}, command {CommandId}, " +
                    "elapsed {ElapsedMilliseconds} ms",
                    CloudServerProtocol.ConnectPath,
                    remoteAdministration.PollWait,
                    pollWait,
                    attemptTimeout,
                    attempt,
                    ConnectMaxAttempts,
                    "delivered",
                    command.CommandId,
                    stopwatch.ElapsedMilliseconds);
                return command;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    "Cloud Server connect poll completed: endpoint {Endpoint}, configured poll duration {ConfiguredPollDuration}, " +
                    "effective poll duration {EffectivePollDuration}, effective attempt timeout {AttemptTimeout}, " +
                    "attempt {Attempt}/{MaxAttempts}, terminal outcome {Outcome}, elapsed {ElapsedMilliseconds} ms",
                    CloudServerProtocol.ConnectPath,
                    remoteAdministration.PollWait,
                    pollWait,
                    attemptTimeout,
                    attempt,
                    ConnectMaxAttempts,
                    "cancelled",
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
            catch (OperationCanceledException) when (attempt < ConnectMaxAttempts)
            {
                LogConnectRetry("attempt-timeout", attempt, pollWait, attemptTimeout, stopwatch.ElapsedMilliseconds);
                await DelayBeforeConnectRetryAsync(attempt, response: null, cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException(
                    $"Cloud Server connect poll at {CloudServerProtocol.ConnectPath} exceeded its " +
                    $"{attemptTimeout} attempt timeout after {ConnectMaxAttempts} attempts.",
                    ex);
            }
            catch (Exception ex) when (IsTransientConnectException(ex) && attempt < ConnectMaxAttempts)
            {
                LogConnectRetry(ex.GetType().Name, attempt, pollWait, attemptTimeout, stopwatch.ElapsedMilliseconds);
                await DelayBeforeConnectRetryAsync(attempt, response: null, cancellationToken);
            }
        }

        throw new InvalidOperationException("Cloud Server connect retry loop exited without a terminal outcome.");
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

    private Task<HttpResponseMessage> SendConnectAsync(HttpRequestMessage request, CancellationToken token)
        => connectClient.SendAsync(request, token);

    private static bool IsTransientConnectStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static bool IsTransientConnectException(Exception exception) =>
        exception is HttpRequestException or IOException;

    private void LogConnectRetry(
        string outcome,
        int attempt,
        TimeSpan pollWait,
        TimeSpan attemptTimeout,
        long elapsedMilliseconds)
    {
        logger.LogWarning(
            "Cloud Server connect poll retrying: endpoint {Endpoint}, configured poll duration {ConfiguredPollDuration}, " +
            "effective poll duration {EffectivePollDuration}, effective attempt timeout {AttemptTimeout}, " +
            "retry attempt {RetryAttempt}/{MaxAttempts}, attempt outcome {Outcome}, elapsed {ElapsedMilliseconds} ms",
            CloudServerProtocol.ConnectPath,
            remoteAdministration.PollWait,
            pollWait,
            attemptTimeout,
            attempt + 1,
            ConnectMaxAttempts,
            outcome,
            elapsedMilliseconds);
    }

    private static async Task DelayBeforeConnectRetryAsync(
        int attempt,
        HttpResponseMessage? response,
        CancellationToken cancellationToken)
    {
        var retryAfter = response?.Headers.RetryAfter;
        var delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } retryDate)
        {
            delay = retryDate - DateTimeOffset.UtcNow;
        }

        if (delay is null || delay <= TimeSpan.Zero)
        {
            delay = ConnectRetryBaseDelay * Math.Pow(2, attempt - 1);
        }
        else if (delay > ConnectMaxRetryDelay)
        {
            delay = ConnectMaxRetryDelay;
        }

        await Task.Delay(delay.Value, cancellationToken);
    }
}
