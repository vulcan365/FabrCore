using FabrCore.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;

namespace FabrCore.Client
{
    /// <summary>
    /// Response from model configuration endpoint.
    /// </summary>
    public class ModelConfigResponse
    {
        public string Name { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string Uri { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string ApiKeyAlias { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response from API key endpoint.
    /// </summary>
    public class ApiKeyResponse
    {
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response from agents list endpoint.
    /// </summary>
    public class AgentsListResponse
    {
        public int Count { get; set; }
        public List<AgentInfo> Agents { get; set; } = new();
    }

    /// <summary>
    /// Response from agent statistics endpoint.
    /// </summary>
    public class AgentStatisticsResponse : Dictionary<string, int>
    {
    }

    /// <summary>
    /// Response from purge agents endpoint.
    /// </summary>
    public class PurgeAgentsResponse
    {
        public int PurgedCount { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// File metadata response.
    /// </summary>
    public class FileMetadataResponse
    {
        public string FileId { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public long FileSize { get; set; }
        public string ContentType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request for generating embeddings.
    /// </summary>
    public class EmbeddingRequest
    {
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response from embeddings endpoint.
    /// </summary>
    public class EmbeddingResponse
    {
        public float[] Vector { get; set; } = [];
        public int Dimensions { get; set; }
    }

    /// <summary>
    /// Response from batch agent creation endpoint.
    /// </summary>
    public class CreateAgentsResponse
    {
        public int TotalRequested { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<AgentHealthStatus> Results { get; set; } = new();
    }

    /// <summary>
    /// Client interface for the FabrCore Host API.
    /// </summary>
    public interface IFabrCoreHostApiClient
    {
        /// <summary>
        /// Creates agents with the specified configurations.
        /// </summary>
        /// <param name="userId">The user ID.</param>
        /// <param name="configs">The agent configurations.</param>
        /// <param name="detailLevel">The health detail level to return.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Response containing health status for each created agent.</returns>
        Task<CreateAgentsResponse> CreateAgentsAsync(
            string userId,
            List<AgentConfiguration> configs,
            HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the health status of an agent.
        /// </summary>
        /// <param name="userId">The user ID.</param>
        /// <param name="handle">The agent handle.</param>
        /// <param name="detailLevel">The health detail level to return.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Health status of the agent.</returns>
        Task<AgentHealthStatus> GetAgentHealthAsync(
            string userId,
            string handle,
            HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a chat message to an agent and returns the response.
        /// </summary>
        Task<AgentMessage> ChatAsync(string userId, string handle, string message, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a fire-and-forget event to an agent's AgentEvent stream.
        /// If streamName is provided, publishes to the named event stream.
        /// </summary>
        Task SendEventAsync(string userId, string handle, AgentMessage message, string? streamName = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the model configuration by name.
        /// </summary>
        Task<ModelConfigResponse> GetModelConfigAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the API key by alias.
        /// </summary>
        Task<ApiKeyResponse> GetApiKeyAsync(string alias, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets all agents, optionally filtered by status.
        /// </summary>
        Task<AgentsListResponse> GetAgentsAsync(string? status = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a specific agent by key.
        /// </summary>
        Task<AgentInfo?> GetAgentAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets agent statistics.
        /// </summary>
        Task<AgentStatisticsResponse> GetAgentStatisticsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Purges deactivated agents older than the specified hours.
        /// </summary>
        Task<PurgeAgentsResponse> PurgeOldAgentsAsync(int olderThanHours = 24, CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads a file and returns the file ID.
        /// </summary>
        Task<string> UploadFileAsync(Stream fileStream, string fileName, int? ttlSeconds = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a file by ID.
        /// </summary>
        Task<Stream?> GetFileAsync(string fileId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets file metadata by ID.
        /// </summary>
        Task<FileMetadataResponse?> GetFileInfoAsync(string fileId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Generates embeddings for the given text.
        /// </summary>
        Task<EmbeddingResponse> GetEmbeddingsAsync(string text, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// HTTP client implementation for the FabrCore Host API.
    /// </summary>
    public class FabrCoreHostApiClient : IFabrCoreHostApiClient
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Client.FabrCoreHostApiClient");
        private static readonly Meter Meter = new("FabrCore.Client.FabrCoreHostApiClient");

        private static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>(
            "fabrcore.api_client.requests",
            description: "Number of API requests made");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.api_client.errors",
            description: "Number of API errors");

        private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
            "fabrcore.api_client.request.duration",
            unit: "ms",
            description: "Duration of API requests");

        private readonly HttpClient _httpClient;
        private readonly ILogger<FabrCoreHostApiClient> _logger;
        private readonly string _baseUrl;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public FabrCoreHostApiClient(HttpClient httpClient, IConfiguration configuration, ILogger<FabrCoreHostApiClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _baseUrl = configuration["FabrCoreHostUrl"] ?? "http://localhost:5000";

            _logger.LogDebug("FabrCoreApiClient initialized with base URL: {BaseUrl}", _baseUrl);
        }

        public async Task<CreateAgentsResponse> CreateAgentsAsync(
            string userId,
            List<AgentConfiguration> configs,
            HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
            CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("CreateAgents", ActivityKind.Client);
            activity?.SetTag("user.id", userId);
            activity?.SetTag("agents.count", configs.Count);
            activity?.SetTag("detail.level", detailLevel.ToString());

            var url = $"{_baseUrl}/fabrcoreapi/Agent/create?detailLevel={detailLevel}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("x-user", userId);
                request.Content = JsonContent.Create(configs, options: JsonOptions);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<CreateAgentsResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("Failed to deserialize create agents response");

                RecordSuccess(activity, startTime, "CreateAgents");
                _logger.LogInformation("Created {Success}/{Total} agents for user {UserId}",
                    result.SuccessCount, result.TotalRequested, userId);

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "CreateAgents", ex);
                _logger.LogError(ex, "Failed to create agents for user {UserId}", userId);
                throw;
            }
        }

        public async Task<AgentHealthStatus> GetAgentHealthAsync(
            string userId,
            string handle,
            HealthDetailLevel detailLevel = HealthDetailLevel.Basic,
            CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("GetAgentHealth", ActivityKind.Client);
            activity?.SetTag("user.id", userId);
            activity?.SetTag("agent.handle", handle);
            activity?.SetTag("detail.level", detailLevel.ToString());

            var url = $"{_baseUrl}/fabrcoreapi/Agent/health/{handle}?detailLevel={detailLevel}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-user", userId);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<AgentHealthStatus>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("Failed to deserialize agent health response");

                RecordSuccess(activity, startTime, "GetAgentHealth");
                _logger.LogDebug("Got health for agent {Handle}, State: {State}", handle, result.State);

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "GetAgentHealth", ex);
                _logger.LogError(ex, "Failed to get health for agent {Handle}", handle);
                throw;
            }
        }

        public async Task<AgentMessage> ChatAsync(string userId, string handle, string message, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("Chat", ActivityKind.Client);
            activity?.SetTag("user.id", userId);
            activity?.SetTag("agent.handle", handle);

            var url = $"{_baseUrl}/fabrcoreapi/Agent/chat/{handle}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("x-user", userId);
                request.Content = JsonContent.Create(message, options: JsonOptions);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<AgentMessage>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("Failed to deserialize chat response");

                RecordSuccess(activity, startTime, "Chat");
                _logger.LogDebug("Chat with agent {Handle} for user {UserId} completed", handle, userId);

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "Chat", ex);
                _logger.LogError(ex, "Failed to chat with agent {Handle} for user {UserId}", handle, userId);
                throw;
            }
        }

        public async Task SendEventAsync(string userId, string handle, AgentMessage message, string? streamName = null, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("SendEvent", ActivityKind.Client);
            activity?.SetTag("user.id", userId);
            activity?.SetTag("agent.handle", handle);

            var url = streamName != null
                ? $"{_baseUrl}/fabrcoreapi/Agent/event/{handle}?streamName={Uri.EscapeDataString(streamName)}"
                : $"{_baseUrl}/fabrcoreapi/Agent/event/{handle}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("x-user", userId);
                request.Content = JsonContent.Create(message, options: JsonOptions);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                RecordSuccess(activity, startTime, "SendEvent");
                _logger.LogDebug("Event sent to agent {Handle} for user {UserId}", handle, userId);
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "SendEvent", ex);
                _logger.LogError(ex, "Failed to send event to agent {Handle} for user {UserId}", handle, userId);
                throw;
            }
        }

        public async Task<ModelConfigResponse> GetModelConfigAsync(string name, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("GetModelConfig", ActivityKind.Client);
            activity?.SetTag("model.config.name", name);

            var url = $"{_baseUrl}/fabrcoreapi/ModelConfig/model/{name}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<ModelConfigResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException($"Failed to deserialize model config for '{name}'");

                RecordSuccess(activity, startTime, "GetModelConfig");
                _logger.LogDebug("Retrieved model config: {Name}", name);

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "GetModelConfig", ex);
                _logger.LogError(ex, "Failed to get model config: {Name}", name);
                throw;
            }
        }

        public async Task<ApiKeyResponse> GetApiKeyAsync(string alias, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("GetApiKey", ActivityKind.Client);
            activity?.SetTag("api_key.alias", alias);

            var url = $"{_baseUrl}/fabrcoreapi/ModelConfig/apikey/{alias}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<ApiKeyResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException($"Failed to deserialize API key for alias '{alias}'");

                RecordSuccess(activity, startTime, "GetApiKey");
                _logger.LogDebug("Retrieved API key for alias: {Alias}", alias);

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "GetApiKey", ex);
                _logger.LogError(ex, "Failed to get API key for alias: {Alias}", alias);
                throw;
            }
        }

        public async Task<AgentsListResponse> GetAgentsAsync(string? status = null, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("GetAgents", ActivityKind.Client);
            activity?.SetTag("status.filter", status ?? "all");

            var url = string.IsNullOrEmpty(status)
                ? $"{_baseUrl}/fabrcoreapi/Diagnostics/agents"
                : $"{_baseUrl}/fabrcoreapi/Diagnostics/agents?status={status}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<AgentsListResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("Failed to deserialize agents list");

                RecordSuccess(activity, startTime, "GetAgents");
                _logger.LogDebug("Retrieved {Count} agents with status filter: {Status}", result.Count, status ?? "all");

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "GetAgents", ex);
                _logger.LogError(ex, "Failed to get agents with status: {Status}", status);
                throw;
            }
        }

        public async Task<AgentInfo?> GetAgentAsync(string key, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("GetAgent", ActivityKind.Client);
            activity?.SetTag("agent.key", key);

            var url = $"{_baseUrl}/fabrcoreapi/Diagnostics/agents/{key}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    RecordSuccess(activity, startTime, "GetAgent");
                    _logger.LogDebug("Agent not found: {Key}", key);
                    return null;
                }

                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<AgentInfo>(JsonOptions, cancellationToken);

                RecordSuccess(activity, startTime, "GetAgent");
                _logger.LogDebug("Retrieved agent: {Key}", key);

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "GetAgent", ex);
                _logger.LogError(ex, "Failed to get agent: {Key}", key);
                throw;
            }
        }

        public async Task<AgentStatisticsResponse> GetAgentStatisticsAsync(CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("GetAgentStatistics", ActivityKind.Client);

            var url = $"{_baseUrl}/fabrcoreapi/Diagnostics/agents/statistics";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<AgentStatisticsResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("Failed to deserialize agent statistics");

                RecordSuccess(activity, startTime, "GetAgentStatistics");
                _logger.LogDebug("Retrieved agent statistics");

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "GetAgentStatistics", ex);
                _logger.LogError(ex, "Failed to get agent statistics");
                throw;
            }
        }

        public async Task<PurgeAgentsResponse> PurgeOldAgentsAsync(int olderThanHours = 24, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("PurgeOldAgents", ActivityKind.Client);
            activity?.SetTag("older_than_hours", olderThanHours);

            var url = $"{_baseUrl}/fabrcoreapi/Diagnostics/agents/purge?olderThanHours={olderThanHours}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var response = await _httpClient.PostAsync(url, null, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<PurgeAgentsResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("Failed to deserialize purge response");

                RecordSuccess(activity, startTime, "PurgeOldAgents");
                _logger.LogInformation("Purged {Count} agents older than {Hours} hours", result.PurgedCount, olderThanHours);

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "PurgeOldAgents", ex);
                _logger.LogError(ex, "Failed to purge old agents");
                throw;
            }
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, int? ttlSeconds = null, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("UploadFile", ActivityKind.Client);
            activity?.SetTag("file.name", fileName);
            activity?.SetTag("ttl.seconds", ttlSeconds);

            var url = ttlSeconds.HasValue
                ? $"{_baseUrl}/fabrcoreapi/File/upload?ttlSeconds={ttlSeconds}"
                : $"{_baseUrl}/fabrcoreapi/File/upload";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                using var content = new MultipartFormDataContent();
                using var streamContent = new StreamContent(fileStream);
                content.Add(streamContent, "file", fileName);

                var response = await _httpClient.PostAsync(url, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var fileId = await response.Content.ReadAsStringAsync(cancellationToken);

                RecordSuccess(activity, startTime, "UploadFile");
                _logger.LogInformation("Uploaded file: {FileName}, FileId: {FileId}", fileName, fileId);

                return fileId.Trim('"');
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "UploadFile", ex);
                _logger.LogError(ex, "Failed to upload file: {FileName}", fileName);
                throw;
            }
        }

        public async Task<Stream?> GetFileAsync(string fileId, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("GetFile", ActivityKind.Client);
            activity?.SetTag("file.id", fileId);

            var url = $"{_baseUrl}/fabrcoreapi/File/{fileId}";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    RecordSuccess(activity, startTime, "GetFile");
                    _logger.LogDebug("File not found: {FileId}", fileId);
                    return null;
                }

                response.EnsureSuccessStatusCode();

                RecordSuccess(activity, startTime, "GetFile");
                _logger.LogDebug("Retrieved file: {FileId}", fileId);

                return await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "GetFile", ex);
                _logger.LogError(ex, "Failed to get file: {FileId}", fileId);
                throw;
            }
        }

        public async Task<FileMetadataResponse?> GetFileInfoAsync(string fileId, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("GetFileInfo", ActivityKind.Client);
            activity?.SetTag("file.id", fileId);

            var url = $"{_baseUrl}/fabrcoreapi/File/{fileId}/info";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    RecordSuccess(activity, startTime, "GetFileInfo");
                    _logger.LogDebug("File metadata not found: {FileId}", fileId);
                    return null;
                }

                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<FileMetadataResponse>(JsonOptions, cancellationToken);

                RecordSuccess(activity, startTime, "GetFileInfo");
                _logger.LogDebug("Retrieved file metadata: {FileId}", fileId);

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "GetFileInfo", ex);
                _logger.LogError(ex, "Failed to get file metadata: {FileId}", fileId);
                throw;
            }
        }

        public async Task<EmbeddingResponse> GetEmbeddingsAsync(string text, CancellationToken cancellationToken = default)
        {
            using var activity = ActivitySource.StartActivity("GetEmbeddings", ActivityKind.Client);
            activity?.SetTag("text.length", text.Length);

            var url = $"{_baseUrl}/fabrcoreapi/Embeddings";
            var startTime = Stopwatch.GetTimestamp();

            try
            {
                var request = new EmbeddingRequest { Text = text };
                var response = await _httpClient.PostAsJsonAsync(url, request, JsonOptions, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("Failed to deserialize embeddings response");

                RecordSuccess(activity, startTime, "GetEmbeddings");
                _logger.LogDebug("Generated embeddings with {Dimensions} dimensions", result.Dimensions);

                return result;
            }
            catch (Exception ex)
            {
                RecordError(activity, startTime, "GetEmbeddings", ex);
                _logger.LogError(ex, "Failed to generate embeddings");
                throw;
            }
        }

        private void RecordSuccess(Activity? activity, long startTime, string operation)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
            RequestDuration.Record(elapsed, new KeyValuePair<string, object?>("operation", operation));
            RequestCounter.Add(1,
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("status", "success"));
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        private void RecordError(Activity? activity, long startTime, string operation, Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
            RequestDuration.Record(elapsed, new KeyValuePair<string, object?>("operation", operation));
            RequestCounter.Add(1,
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("status", "error"));
            ErrorCounter.Add(1,
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
        }
    }
}
