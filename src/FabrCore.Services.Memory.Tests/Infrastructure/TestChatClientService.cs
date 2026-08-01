using Azure.AI.OpenAI;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace FabrCore.Services.Memory.Tests.Infrastructure;

internal sealed class TestChatClientService : IFabrCoreChatClientService
{
    private readonly IChatClient? _mockClient;
    private readonly FabrCoreConfiguration? _liveConfig;

    public TestChatClientService(IChatClient mockClient)
    {
        _mockClient = mockClient;
    }

    public TestChatClientService(FabrCoreConfiguration liveConfig)
    {
        _liveConfig = liveConfig;
    }

    public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
    {
        if (_mockClient is not null)
            return Task.FromResult(_mockClient);

        var model = GetModel(name);
        var key = GetApiKey(model.ApiKeyAlias);
        var timeout = TimeSpan.FromSeconds(model.TimeoutSeconds > 0 ? model.TimeoutSeconds : networkTimeoutSeconds);

        IChatClient client = model.Provider.ToLowerInvariant() switch
        {
            "azure" => new AzureOpenAIClient(
                    new Uri(model.Uri), new ApiKeyCredential(key),
                    new AzureOpenAIClientOptions { NetworkTimeout = timeout })
                .GetChatClient(model.Model).AsIChatClient(),
            "openai" => new OpenAIClient(
                    new ApiKeyCredential(key), new OpenAIClientOptions { NetworkTimeout = timeout })
                .GetChatClient(model.Model).AsIChatClient(),
#pragma warning disable OPENAI001
            "openrouter" or "grok" or "gemini" => new OpenAIClient(
                    new ApiKeyCredential(key),
                    new OpenAIClientOptions { Endpoint = new Uri(model.Uri), NetworkTimeout = timeout })
                .GetChatClient(model.Model).AsIChatClient(),
#pragma warning restore OPENAI001
            _ => throw new NotSupportedException($"Provider '{model.Provider}' is not supported in live tests.")
        };

        return Task.FromResult(client);
    }

#pragma warning disable MEAI001
    public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100) =>
        throw new NotSupportedException();
#pragma warning restore MEAI001

    public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name)
    {
        var model = GetModel(name);
        var key = GetApiKey(model.ApiKeyAlias);

        IEmbeddingGenerator<string, Embedding<float>> generator = model.Provider.ToLowerInvariant() switch
        {
            "azure" => new AzureOpenAIClient(new Uri(model.Uri), new ApiKeyCredential(key))
                .GetEmbeddingClient(model.Model).AsIEmbeddingGenerator(),
            "openai" => new OpenAIClient(new ApiKeyCredential(key))
                .GetEmbeddingClient(model.Model).AsIEmbeddingGenerator(),
            _ => throw new NotSupportedException($"Embedding provider '{model.Provider}' is not supported in live tests.")
        };

        return Task.FromResult(generator);
    }

    public Task<ModelConfiguration> GetModelConfigurationAsync(string name) =>
        Task.FromResult(_liveConfig is null
            ? new ModelConfiguration
            {
                Name = name,
                Provider = "Test",
                Uri = "https://test.invalid",
                Model = "test-model",
                ApiKeyAlias = "test-key"
            }
            : GetModel(name));

    private ModelConfiguration GetModel(string name) =>
        _liveConfig?.ModelConfigurations.FirstOrDefault(m =>
            m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Model configuration '{name}' was not found.");

    private string GetApiKey(string alias) =>
        _liveConfig?.ApiKeys.FirstOrDefault(k =>
            k.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))?.Value
        ?? throw new InvalidOperationException($"API key alias '{alias}' was not found.");
}
