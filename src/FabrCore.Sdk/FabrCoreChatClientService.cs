using Azure.AI.OpenAI;
using FabrCore.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenTelemetry.Trace;
using System.ClientModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FabrCore.Sdk
{
    public interface IFabrCoreChatClientService
    {
        Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100);
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100);
#pragma warning restore MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name);

        /// <summary>
        /// Gets the model configuration and its inference, compaction, and safety defaults.
        /// </summary>
        Task<ModelConfiguration> GetModelConfigurationAsync(string name);
    }

    public class FabrCoreChatClientService : IFabrCoreChatClientService
    {
        private static readonly ActivitySource ActivitySource = new("FabrCore.Sdk.ChatClientService");
        private static readonly Meter Meter = new("FabrCore.Sdk.ChatClientService");

        // Metrics
        private static readonly Counter<long> ChatClientsCreatedCounter = Meter.CreateCounter<long>(
            "fabrcore.chat_client_service.chat_clients.created",
            description: "Number of chat clients created");

        private static readonly Counter<long> ModelConfigFetchCounter = Meter.CreateCounter<long>(
            "fabrcore.chat_client_service.model_config.fetched",
            description: "Number of model configurations fetched");

        private static readonly Counter<long> ApiKeyFetchCounter = Meter.CreateCounter<long>(
            "fabrcore.chat_client_service.api_key.fetched",
            description: "Number of API keys fetched");

        private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
            "fabrcore.chat_client_service.errors",
            description: "Number of errors encountered in chat client service");

        private readonly IConfiguration _configuration;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<FabrCoreChatClientService> _logger;
        private static readonly HttpClient SharedHttpClient = new(new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        private readonly IFabrCoreModelConfigurationResolver _modelConfigurationResolver;
        private readonly bool _emitAttributionHeaders;

        public FabrCoreChatClientService(IConfiguration configuration, ILoggerFactory loggerFactory)
            : this(configuration, loggerFactory, SharedHttpClient)
        {
        }

        /// <summary>
        /// Creates a chat client service using the supplied model configuration resolver.
        /// FabrCore Host registers a configuration-store-backed resolver so in-process agents
        /// do not call the Host API through the web authentication pipeline.
        /// </summary>
        public FabrCoreChatClientService(
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            IFabrCoreModelConfigurationResolver modelConfigurationResolver)
        {
            _configuration = configuration;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<FabrCoreChatClientService>();
            _modelConfigurationResolver = modelConfigurationResolver
                ?? throw new ArgumentNullException(nameof(modelConfigurationResolver));
            _emitAttributionHeaders = string.Equals(
                _configuration[FabrCoreConfigurationKeys.EmitAttributionHeaders], "true", StringComparison.OrdinalIgnoreCase);

            _logger.LogDebug(
                "FabrCoreChatClientService created with resolver {ResolverType}",
                modelConfigurationResolver.GetType().Name);
        }

        internal FabrCoreChatClientService(
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            HttpClient httpClient)
            : this(
                configuration,
                loggerFactory,
                new FabrCoreHostApiClient(
                    httpClient ?? throw new ArgumentNullException(nameof(httpClient)),
                    configuration,
                    loggerFactory.CreateLogger<FabrCoreHostApiClient>()))
        {
        }

        public async Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
        {
            using var activity = ActivitySource.StartActivity("GetChatClient", ActivityKind.Client);
            activity?.SetTag("model.config.name", name);

            try
            {
                var modelConfig = await GetModelConfiguration(name);
                ModelDefaultsChatClient.ValidateConfiguration(modelConfig);
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

                IChatClient chatClient;
                switch (modelConfig.Provider.ToLowerInvariant())
                {
                    case "openai":
                        var openAiClientOptions = new OpenAIClientOptions
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
                        };

#pragma warning disable OPENAI001 // OpenAIClientOptions.Endpoint is experimental
                        if (!string.IsNullOrWhiteSpace(modelConfig.Uri))
                        {
                            openAiClientOptions.Endpoint = new Uri(modelConfig.Uri);
                        }
#pragma warning restore OPENAI001
                        ApplyAttributionPolicy(openAiClientOptions);

                        chatClient = new OpenAIClient(
                                new ApiKeyCredential(apiKey),
                                openAiClientOptions)
                            .GetChatClient(modelConfig.Model)
                            .AsIChatClient();
                        break;

                    case "azure":
                        var azureClientOptions = new AzureOpenAIClientOptions
                        {
                            EnableDistributedTracing = true,
                            NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
                            {
                                EnableLogging = true,
                                EnableMessageContentLogging = true,
                                LoggerFactory = _loggerFactory,
                                EnableMessageLogging = true

                            }
                        };
                        ApplyAttributionPolicy(azureClientOptions);

                        var azureClient = new AzureOpenAIClient(
                            new Uri(modelConfig.Uri),
                            new ApiKeyCredential(apiKey),
                            azureClientOptions
                        );

                        chatClient = azureClient.GetChatClient(modelConfig.Model).AsIChatClient();
                        break;

                    case "openrouter":
                    case "grok":
                    case "gemini":
                        chatClient = CreateOpenAICompatibleClient(apiKey, modelConfig.Uri, timeoutSeconds)
                            .GetChatClient(modelConfig.Model)
                            .AsIChatClient();
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

                // Wrap with provider sanitizer for non-OpenAI providers that reject
                // the "name" field on non-user messages (e.g., Grok, Gemini)
                if (NeedsAuthorNameSanitization(modelConfig.Provider))
                {
                    chatClient = new ProviderSanitizingChatClient(chatClient);
                    _logger.LogDebug("Added AuthorName sanitization for provider: {Provider}", modelConfig.Provider);
                }

                return ModelDefaultsChatClient.Apply(chatClient, modelConfig);
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
                        var audioClientOptions = new OpenAIClientOptions
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
                        };

#pragma warning disable OPENAI001 // OpenAIClientOptions.Endpoint is experimental
                        if (!string.IsNullOrWhiteSpace(modelConfig.Uri))
                        {
                            audioClientOptions.Endpoint = new Uri(modelConfig.Uri);
                        }
#pragma warning restore OPENAI001
                        ApplyAttributionPolicy(audioClientOptions);

                        var client = new OpenAIClient(
                            new ApiKeyCredential(apiKey),
                            audioClientOptions
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
                        var azureAudioClientOptions = new AzureOpenAIClientOptions
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
                        };
                        ApplyAttributionPolicy(azureAudioClientOptions);

                        var client = new AzureOpenAIClient(
                            new Uri(modelConfig.Uri),
                            new ApiKeyCredential(apiKey),
                            azureAudioClientOptions
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
                    {
                        var embeddingClientOptions = new OpenAIClientOptions();

#pragma warning disable OPENAI001 // OpenAIClientOptions.Endpoint is experimental
                        if (!string.IsNullOrWhiteSpace(modelConfig.Uri))
                        {
                            embeddingClientOptions.Endpoint = new Uri(modelConfig.Uri);
                        }
#pragma warning restore OPENAI001
                        ApplyAttributionPolicy(embeddingClientOptions);

                        return new OpenAIClient(new ApiKeyCredential(apiKey), embeddingClientOptions)
                            .GetEmbeddingClient(modelConfig.Model)
                            .AsIEmbeddingGenerator();
                    }

                    case "azure":
                    {
                        var azureEmbeddingClientOptions = new AzureOpenAIClientOptions();
                        ApplyAttributionPolicy(azureEmbeddingClientOptions);

                        return new AzureOpenAIClient(new Uri(modelConfig.Uri), new ApiKeyCredential(apiKey), azureEmbeddingClientOptions)
                            .GetEmbeddingClient(modelConfig.Model)
                            .AsIEmbeddingGenerator();
                    }

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

        /// <summary>
        /// Returns true for providers that reject the "name" field on non-user messages.
        /// OpenAI and Azure OpenAI accept "name" on all roles; other OpenAI-compatible
        /// providers (Grok, Gemini, OpenRouter) may not.
        /// </summary>
        private static bool NeedsAuthorNameSanitization(string provider)
        {
            return provider.ToLowerInvariant() switch
            {
                "grok" => true,
                "gemini" => true,
                "openrouter" => true,
                _ => false
            };
        }

        /// <summary>
        /// Adds the opt-in agent attribution policy (see <see cref="AgentAttributionPipelinePolicy"/>)
        /// when <c>FabrCore:EmitAttributionHeaders</c> is enabled. No-op otherwise.
        /// </summary>
        private void ApplyAttributionPolicy(System.ClientModel.Primitives.ClientPipelineOptions options)
        {
            if (_emitAttributionHeaders)
            {
                options.AddPolicy(new AgentAttributionPipelinePolicy(), System.ClientModel.Primitives.PipelinePosition.PerCall);
            }
        }

#pragma warning disable OPENAI001 // OpenAIClientOptions.Endpoint is experimental
        private OpenAIClient CreateOpenAICompatibleClient(string apiKey, string endpointUri, int timeoutSeconds)
        {
            var options = new OpenAIClientOptions
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
            };
            ApplyAttributionPolicy(options);

            return new OpenAIClient(new ApiKeyCredential(apiKey), options);
        }
#pragma warning restore OPENAI001

        private async Task<ModelConfiguration> GetModelConfiguration(string name)
        {
            using var activity = ActivitySource.StartActivity("GetModelConfiguration", ActivityKind.Client);
            activity?.SetTag("model.config.name", name);
            activity?.SetTag("model.config.resolver", _modelConfigurationResolver.GetType().Name);

            _logger.LogDebug("Resolving model configuration: {Name}", name);

            try
            {
                var result = await _modelConfigurationResolver.GetModelConfigurationAsync(name);

                ModelConfigFetchCounter.Add(1,
                    new KeyValuePair<string, object?>("model.config.name", name),
                    new KeyValuePair<string, object?>("provider", result.Provider));

                _logger.LogInformation("Model configuration fetched successfully - Name: {Name}, Provider: {Provider}, Model: {Model}",
                    result.Name, result.Provider, result.Model);

                activity?.SetStatus(ActivityStatusCode.Ok);
                return result;
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
            activity?.SetTag("api_key.alias", alias);
            activity?.SetTag("model.config.resolver", _modelConfigurationResolver.GetType().Name);

            _logger.LogDebug("Resolving API key for alias: {Alias}", alias);

            try
            {
                var apiKey = await _modelConfigurationResolver.GetApiKeyAsync(alias);

                ApiKeyFetchCounter.Add(1,
                    new KeyValuePair<string, object?>("api_key.alias", alias));

                _logger.LogInformation("API key fetched successfully for alias: {Alias}", alias);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return apiKey;
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
    }
}
