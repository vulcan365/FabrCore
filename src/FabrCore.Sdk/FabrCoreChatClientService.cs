using Azure.AI.OpenAI;
using Fabr.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenTelemetry.Trace;
using System.ClientModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Fabr.Sdk
{
    public interface IFabrChatClientService
    {
        Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100);
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100);
#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name);

        /// <summary>
        /// Gets the model configuration for creating ChatOptions with MaxOutputTokens.
        /// </summary>
        Task<ModelConfiguration> GetModelConfigurationAsync(string name);
    }

    public class FabrChatClientService : IFabrChatClientService
    {
        private static readonly ActivitySource ActivitySource = new("Fabr.Sdk.ChatClientService");
        private static readonly Meter Meter = new("Fabr.Sdk.ChatClientService");

        // Metrics
        private static readonly Counter<long> ChatClientsCreatedCounter = Meter.CreateCounter<long>(
            "fabr.chat_client_service.chat_clients.created",
            description: "Number of chat clients created");

        private static readonly Counter<long> ModelConfigFetchCounter = Meter.CreateCounter<long>(
            "fabr.chat_client_service.model_config.fetched",
            description: "Number of model configurations fetched");

        private static readonly Counter<long> ApiKeyFetchCounter = Meter.CreateCounter<long>(
            "fabr.chat_client_service.api_key.fetched",
            description: "Number of API keys fetched");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabr.chat_client_service.errors",
            description: "Number of errors encountered in chat client service");

        private readonly IConfiguration _configuration;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<FabrChatClientService> _logger;
        private static readonly HttpClient _httpClient = new HttpClient();

        public FabrChatClientService(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            _configuration = configuration;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<FabrChatClientService>();

            _logger.LogDebug("FabrChatClientService created");
        }

        public async Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
        {
            using var activity = ActivitySource.StartActivity("GetChatClient", ActivityKind.Client);
            activity?.SetTag("model.config.name", name);

            try
            {
                var modelConfig = await GetModelConfiguration(name);
                var apiKey = await GetApiKey(modelConfig.ApiKeyAlias);

                // Use config timeout if set, otherwise use parameter
                var timeoutSeconds = modelConfig.TimeoutSeconds > 0 ? modelConfig.TimeoutSeconds : networkTimeoutSeconds;

                _logger.LogDebug("Getting chat client - Config: {Name}, Timeout: {TimeoutSeconds}s, MaxTokens: {MaxTokens}",
                    name, timeoutSeconds, modelConfig.MaxOutputTokens?.ToString() ?? "unlimited");

                activity?.SetTag("model.provider", modelConfig.Provider);
                activity?.SetTag("model.name", modelConfig.Model);
                activity?.SetTag("timeout.seconds", timeoutSeconds);

                _logger.LogInformation("Creating chat client - Provider: {Provider}, Model: {Model}",
                    modelConfig.Provider, modelConfig.Model);

                OpenAIClient openAiClient;
                switch (modelConfig.Provider.ToLowerInvariant())
                {
                    case "openai":
                        openAiClient = new OpenAIClient(
                            new ApiKeyCredential(apiKey),
                            new OpenAIClientOptions
                            {
                                EnableDistributedTracing = true,
                                NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                                ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
                                {
                                    EnableLogging = false,
                                    EnableMessageContentLogging = false,
                                    LoggerFactory = _loggerFactory,
                                    EnableMessageLogging = false
                                }
                            }
                        );
                        break;

                    case "azure":
                        var azureClient = new AzureOpenAIClient(
                            new Uri(modelConfig.Uri),
                            new ApiKeyCredential(apiKey),
                            new AzureOpenAIClientOptions
                            {
                                EnableDistributedTracing = true,
                                NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                                ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
                                {
                                    EnableLogging = false,
                                    EnableMessageContentLogging = false,
                                    LoggerFactory = _loggerFactory,
                                    EnableMessageLogging = false
                                }
                            }
                        );

                        ChatClientsCreatedCounter.Add(1,
                            new KeyValuePair<string, object?>("provider", "azure"),
                            new KeyValuePair<string, object?>("model", modelConfig.Model));

                        _logger.LogInformation("Azure OpenAI chat client created successfully for model: {Model}", modelConfig.Model);
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        return azureClient.GetChatClient(modelConfig.Model).AsIChatClient();

                    case "openrouter":
                    case "grok":
                    case "gemini":
                        openAiClient = CreateOpenAICompatibleClient(apiKey, modelConfig.Uri, timeoutSeconds);
                        break;

                    default:
                        _logger.LogError("Unsupported provider: {Provider}", modelConfig.Provider);
                        activity?.SetStatus(ActivityStatusCode.Error, $"Unsupported provider: {modelConfig.Provider}");
                        ErrorCounter.Add(1,
                            new KeyValuePair<string, object?>("error.type", "unsupported_provider"),
                            new KeyValuePair<string, object?>("provider", modelConfig.Provider));
                        throw new NotSupportedException($"Provider '{modelConfig.Provider}' is not supported. Supported providers are: Azure, OpenAI, OpenRouter, Grok, Gemini.");
                }

                ChatClientsCreatedCounter.Add(1,
                    new KeyValuePair<string, object?>("provider", modelConfig.Provider.ToLowerInvariant()),
                    new KeyValuePair<string, object?>("model", modelConfig.Model));

                _logger.LogInformation("{Provider} chat client created successfully for model: {Model}", modelConfig.Provider, modelConfig.Model);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return openAiClient.GetChatClient(modelConfig.Model).AsIChatClient();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get chat client for configuration: {Name}", name);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "chat_client_creation_failed"),
                    new KeyValuePair<string, object?>("model.config.name", name));
                throw;
            }
        }

#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        public async Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
        {
            using var activity = ActivitySource.StartActivity("GetChatClient", ActivityKind.Client);
            activity?.SetTag("model.config.name", name);

            try
            {
                var modelConfig = await GetModelConfiguration(name);
                var apiKey = await GetApiKey(modelConfig.ApiKeyAlias);

                // Use config timeout if set, otherwise use parameter
                var timeoutSeconds = modelConfig.TimeoutSeconds > 0 ? modelConfig.TimeoutSeconds : networkTimeoutSeconds;

                _logger.LogDebug("Getting chat client - Config: {Name}, Timeout: {TimeoutSeconds}s, MaxTokens: {MaxTokens}",
                    name, timeoutSeconds, modelConfig.MaxOutputTokens?.ToString() ?? "unlimited");

                activity?.SetTag("model.provider", modelConfig.Provider);
                activity?.SetTag("model.name", modelConfig.Model);
                activity?.SetTag("timeout.seconds", timeoutSeconds);

                _logger.LogInformation("Creating chat client - Provider: {Provider}, Model: {Model}",
                    modelConfig.Provider, modelConfig.Model);

                switch (modelConfig.Provider.ToLowerInvariant())
                {
                    case "openai":
                    {
                        var client = new OpenAIClient(
                            new ApiKeyCredential(apiKey),
                            new OpenAIClientOptions
                            {
                                EnableDistributedTracing = true,
                                NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                                ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
                                {
                                    EnableLogging = false,
                                    EnableMessageContentLogging = false,
                                    LoggerFactory = _loggerFactory,
                                    EnableMessageLogging = false
                                }
                            }
                        );

                        ChatClientsCreatedCounter.Add(1,
                            new KeyValuePair<string, object?>("provider", "openai"),
                            new KeyValuePair<string, object?>("model", modelConfig.Model));

                        _logger.LogInformation("OpenAI audio client created successfully for model: {Model}", modelConfig.Model);
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        return client.GetAudioClient(modelConfig.Model).AsISpeechToTextClient();
                    }

                    case "azure":
                    {
                        var client = new AzureOpenAIClient(
                            new Uri(modelConfig.Uri),
                            new ApiKeyCredential(apiKey),
                            new AzureOpenAIClientOptions
                            {
                                EnableDistributedTracing = true,
                                NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                                ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
                                {
                                    EnableLogging = false,
                                    EnableMessageContentLogging = false,
                                    LoggerFactory = _loggerFactory,
                                    EnableMessageLogging = false
                                }
                            }
                        );

                        ChatClientsCreatedCounter.Add(1,
                            new KeyValuePair<string, object?>("provider", "azure"),
                            new KeyValuePair<string, object?>("model", modelConfig.Model));

                        _logger.LogInformation("Azure OpenAI audio client created successfully for model: {Model}", modelConfig.Model);
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        return client.GetAudioClient(modelConfig.Model).AsISpeechToTextClient();
                    }

                    case "openrouter":
                    case "grok":
                    case "gemini":
                        throw new NotSupportedException($"Provider '{modelConfig.Provider}' does not support audio/speech-to-text.");

                    default:
                        _logger.LogError("Unsupported provider: {Provider}", modelConfig.Provider);
                        activity?.SetStatus(ActivityStatusCode.Error, $"Unsupported provider: {modelConfig.Provider}");
                        ErrorCounter.Add(1,
                            new KeyValuePair<string, object?>("error.type", "unsupported_provider"),
                            new KeyValuePair<string, object?>("provider", modelConfig.Provider));
                        throw new NotSupportedException($"Provider '{modelConfig.Provider}' is not supported. Supported providers are: Azure, OpenAI, OpenRouter, Grok, Gemini.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get chat client for configuration: {Name}", name);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "chat_client_creation_failed"),
                    new KeyValuePair<string, object?>("model.config.name", name));
                throw;
            }
        }
#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.


        public async Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name)
        {
            using var activity = ActivitySource.StartActivity("GetChatClient", ActivityKind.Client);
            activity?.SetTag("model.config.name", name);

            _logger.LogDebug("Getting chat client for configuration: {Name}", name);

            try
            {
                var modelConfig = await GetModelConfiguration(name);
                var apiKey = await GetApiKey(modelConfig.ApiKeyAlias);


                switch (modelConfig.Provider.ToLowerInvariant())
                {
                    case "openai":
                        return new OpenAIClient(new ApiKeyCredential(apiKey))
                            .GetEmbeddingClient(modelConfig.Model)
                            .AsIEmbeddingGenerator();

                    case "azure":
                        return new AzureOpenAIClient(new Uri(modelConfig.Uri), new ApiKeyCredential(apiKey))
                            .GetEmbeddingClient(modelConfig.Model)
                            .AsIEmbeddingGenerator();

                    case "openrouter":
                    case "gemini":
                        return CreateOpenAICompatibleClient(apiKey, modelConfig.Uri, timeoutSeconds: 60)
                            .GetEmbeddingClient(modelConfig.Model)
                            .AsIEmbeddingGenerator();

                    case "grok":
                        throw new NotSupportedException("Grok (xAI) does not support embeddings. Use a different provider for your embeddings model.");

                    default:
                        _logger.LogError("Unsupported provider: {Provider}", modelConfig.Provider);
                        activity?.SetStatus(ActivityStatusCode.Error, $"Unsupported provider: {modelConfig.Provider}");
                        ErrorCounter.Add(1,
                            new KeyValuePair<string, object?>("error.type", "unsupported_provider"),
                            new KeyValuePair<string, object?>("provider", modelConfig.Provider));
                        throw new NotSupportedException($"Provider '{modelConfig.Provider}' is not supported. Supported providers are: Azure, OpenAI, OpenRouter, Grok, Gemini.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get embedding client for configuration: {Name}", name);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "chat_client_creation_failed"),
                    new KeyValuePair<string, object?>("model.config.name", name));
                throw;
            }
        }

        /// <inheritdoc />
        public Task<ModelConfiguration> GetModelConfigurationAsync(string name) => GetModelConfiguration(name);

#pragma warning disable OPENAI001 // OpenAIClientOptions.Endpoint is experimental
        private OpenAIClient CreateOpenAICompatibleClient(string apiKey, string endpointUri, int timeoutSeconds)
        {
            return new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(endpointUri),
                    EnableDistributedTracing = true,
                    NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                    ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
                    {
                        EnableLogging = false,
                        EnableMessageContentLogging = false,
                        LoggerFactory = _loggerFactory,
                        EnableMessageLogging = false
                    }
                });
        }
#pragma warning restore OPENAI001

        private async Task<ModelConfiguration> GetModelConfiguration(string name)
        {
            using var activity = ActivitySource.StartActivity("GetModelConfiguration", ActivityKind.Client);
            var baseUrl = _configuration["FabrHostUrl"] ?? "http://localhost:5000";
            var url = $"{baseUrl}/fabrapi/ModelConfig/model/{name}";

            activity?.SetTag("model.config.name", name);
            activity?.SetTag("http.url", url);

            _logger.LogDebug("Fetching model configuration: {Name} from {Url}", name, url);

            try
            {
                var response = await _httpClient.GetAsync(url);

                activity?.SetTag("http.status_code", (int)response.StatusCode);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to fetch model configuration - Name: {Name}, StatusCode: {StatusCode}",
                        name, response.StatusCode);
                    activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {response.StatusCode}");
                    ErrorCounter.Add(1,
                        new KeyValuePair<string, object?>("error.type", "model_config_fetch_failed"),
                        new KeyValuePair<string, object?>("http.status_code", (int)response.StatusCode));
                    throw new Exception($"Failed to get model configuration for '{name}': {response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ModelConfigResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (result == null)
                {
                    _logger.LogError("Failed to deserialize model configuration for: {Name}", name);
                    throw new Exception($"Failed to deserialize model configuration for '{name}'");
                }

                ModelConfigFetchCounter.Add(1,
                    new KeyValuePair<string, object?>("model.config.name", name),
                    new KeyValuePair<string, object?>("provider", result.Provider));

                _logger.LogInformation("Model configuration fetched successfully - Name: {Name}, Provider: {Provider}, Model: {Model}",
                    result.Name, result.Provider, result.Model);

                activity?.SetStatus(ActivityStatusCode.Ok);

                return new ModelConfiguration
                {
                    Name = result.Name,
                    Provider = result.Provider,
                    Uri = result.Uri,
                    Model = result.Model,
                    ApiKeyAlias = result.ApiKeyAlias,
                    TimeoutSeconds = result.TimeoutSeconds,
                    MaxOutputTokens = result.MaxOutputTokens,
                    ContextWindowTokens = result.ContextWindowTokens
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching model configuration: {Name}", name);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "model_config_fetch_exception"),
                    new KeyValuePair<string, object?>("model.config.name", name));
                throw;
            }
        }

        private async Task<string> GetApiKey(string alias)
        {
            using var activity = ActivitySource.StartActivity("GetApiKey", ActivityKind.Client);
            var baseUrl = _configuration["FabrHostUrl"] ?? "http://localhost:5000";
            var url = $"{baseUrl}/fabrapi/ModelConfig/apikey/{alias}";

            activity?.SetTag("api_key.alias", alias);
            activity?.SetTag("http.url", url);

            _logger.LogDebug("Fetching API key for alias: {Alias}", alias);

            try
            {
                var response = await _httpClient.GetAsync(url);

                activity?.SetTag("http.status_code", (int)response.StatusCode);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to fetch API key - Alias: {Alias}, StatusCode: {StatusCode}",
                        alias, response.StatusCode);
                    activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {response.StatusCode}");
                    ErrorCounter.Add(1,
                        new KeyValuePair<string, object?>("error.type", "api_key_fetch_failed"),
                        new KeyValuePair<string, object?>("http.status_code", (int)response.StatusCode));
                    throw new Exception($"Failed to get API key for alias '{alias}': {response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ApiKeyResponse>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (result == null)
                {
                    _logger.LogError("Failed to deserialize API key for alias: {Alias}", alias);
                    throw new Exception($"Failed to deserialize API key for alias '{alias}'");
                }

                ApiKeyFetchCounter.Add(1,
                    new KeyValuePair<string, object?>("api_key.alias", alias));

                _logger.LogInformation("API key fetched successfully for alias: {Alias}", alias);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return result.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching API key for alias: {Alias}", alias);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                ErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("error.type", "api_key_fetch_exception"),
                    new KeyValuePair<string, object?>("api_key.alias", alias));
                throw;
            }
        }

        private class ModelConfigResponse
        {
            public required string Name { get; set; }
            public required string Provider { get; set; }
            public required string Uri { get; set; }
            public required string Model { get; set; }
            public required string ApiKeyAlias { get; set; }
            public int TimeoutSeconds { get; set; }
            public int? MaxOutputTokens { get; set; }
            public int? ContextWindowTokens { get; set; }
        }

        private class ApiKeyResponse
        {
            public required string Value { get; set; }
        }
    }
}
