using Azure.AI.OpenAI;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using System.Text.Json;

namespace FabrCore.Tests.Infrastructure;

/// <summary>
/// Test implementation of IFabrCoreChatClientService that supports two modes:
/// - Mock mode: returns a pre-configured FakeChatClient for deterministic testing
/// - Live mode: creates real chat clients directly from fabrcore.json (no Host API needed)
/// </summary>
public class TestChatClientService : IFabrCoreChatClientService
{
    private readonly IChatClient? _mockClient;
    private readonly FabrCoreConfiguration? _liveConfig;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Creates a mock-mode service that returns the given chat client.</summary>
    public TestChatClientService(IChatClient mockClient)
    {
        _mockClient = mockClient;
    }

    /// <summary>
    /// Creates a live-mode service that reads model configs and API keys
    /// directly from the parsed fabrcore.json configuration (no Host API required).
    /// </summary>
    public TestChatClientService(FabrCoreConfiguration liveConfig, ILoggerFactory loggerFactory)
    {
        _liveConfig = liveConfig;
        _loggerFactory = loggerFactory;
    }

    public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
    {
        if (_mockClient is not null)
            return Task.FromResult(_mockClient);

        var modelConfig = GetModelConfig(name);
        var apiKey = GetApiKeyValue(modelConfig.ApiKeyAlias);
        var timeoutSeconds = modelConfig.TimeoutSeconds > 0 ? modelConfig.TimeoutSeconds : networkTimeoutSeconds;

        IChatClient client = modelConfig.Provider.ToLowerInvariant() switch
        {
            "azure" => new AzureOpenAIClient(
                new Uri(modelConfig.Uri),
                new ApiKeyCredential(apiKey),
                new AzureOpenAIClientOptions
                {
                    NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds)
                }).GetChatClient(modelConfig.Model).AsIChatClient(),

            "openai" => new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions
                {
                    NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds)
                }).GetChatClient(modelConfig.Model).AsIChatClient(),

#pragma warning disable OPENAI001
            "openrouter" or "grok" or "gemini" => new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(modelConfig.Uri),
                    NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds)
                }).GetChatClient(modelConfig.Model).AsIChatClient(),
#pragma warning restore OPENAI001

            _ => throw new NotSupportedException(
                $"Provider '{modelConfig.Provider}' is not supported. " +
                "Supported: Azure, OpenAI, OpenRouter, Grok, Gemini.")
        };

        return Task.FromResult(client);
    }

#pragma warning disable MEAI001
    public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
    {
        throw new NotSupportedException("Audio client is not supported in test mode.");
    }
#pragma warning restore MEAI001

    public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name)
    {
        throw new NotSupportedException("Embeddings client is not supported in test mode.");
    }

    public Task<ModelConfiguration> GetModelConfigurationAsync(string name)
    {
        if (_liveConfig is not null)
            return Task.FromResult(GetModelConfig(name));

        return Task.FromResult(new ModelConfiguration
        {
            Name = name,
            Provider = "Test",
            Uri = "https://test.local",
            Model = "test-model",
            ApiKeyAlias = "test-key",
            TimeoutSeconds = 30,
            MaxOutputTokens = 4096,
            ContextWindowTokens = 128000
        });
    }

    private ModelConfiguration GetModelConfig(string name)
    {
        var config = _liveConfig?.ModelConfigurations
            .FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        return config ?? throw new InvalidOperationException(
            $"Model configuration '{name}' not found in fabrcore.json. " +
            $"Available: {string.Join(", ", _liveConfig?.ModelConfigurations.Select(m => m.Name) ?? [])}");
    }

    private string GetApiKeyValue(string alias)
    {
        var key = _liveConfig?.ApiKeys
            .FirstOrDefault(k => k.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

        return key?.Value ?? throw new InvalidOperationException(
            $"API key alias '{alias}' not found in fabrcore.json.");
    }
}
