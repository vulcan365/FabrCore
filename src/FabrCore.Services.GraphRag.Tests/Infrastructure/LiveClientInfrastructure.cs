using Azure.AI.OpenAI;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace FabrCore.Services.GraphRag.Tests.Infrastructure;

internal sealed class LiveChatClientService(FabrCoreConfiguration configuration) : IFabrCoreChatClientService
{
    public Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
    {
        var model = GetModel(name);
        var timeout = TimeSpan.FromSeconds(model.TimeoutSeconds > 0 ? model.TimeoutSeconds : networkTimeoutSeconds);
        IChatClient client = model.Provider.ToLowerInvariant() switch
        {
            "azure" => new AzureOpenAIClient(new Uri(model.Uri), new ApiKeyCredential(GetKey(model.ApiKeyAlias)),
                    new AzureOpenAIClientOptions { NetworkTimeout = timeout })
                .GetChatClient(model.Model).AsIChatClient(),
            "openai" => new OpenAIClient(new ApiKeyCredential(GetKey(model.ApiKeyAlias)),
                    new OpenAIClientOptions { NetworkTimeout = timeout })
                .GetChatClient(model.Model).AsIChatClient(),
#pragma warning disable OPENAI001
            "openrouter" or "grok" or "gemini" => new OpenAIClient(
                    new ApiKeyCredential(GetKey(model.ApiKeyAlias)),
                    new OpenAIClientOptions { Endpoint = new Uri(model.Uri), NetworkTimeout = timeout })
                .GetChatClient(model.Model).AsIChatClient(),
#pragma warning restore OPENAI001
            _ => throw new NotSupportedException($"Provider '{model.Provider}' is not supported by live tests.")
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
        IEmbeddingGenerator<string, Embedding<float>> generator = model.Provider.ToLowerInvariant() switch
        {
            "azure" => new AzureOpenAIClient(new Uri(model.Uri), new ApiKeyCredential(GetKey(model.ApiKeyAlias)))
                .GetEmbeddingClient(model.Model).AsIEmbeddingGenerator(),
            "openai" => new OpenAIClient(new ApiKeyCredential(GetKey(model.ApiKeyAlias)))
                .GetEmbeddingClient(model.Model).AsIEmbeddingGenerator(),
            _ => throw new NotSupportedException($"Embedding provider '{model.Provider}' is not supported by live tests.")
        };
        return Task.FromResult(generator);
    }

    public Task<ModelConfiguration> GetModelConfigurationAsync(string name) => Task.FromResult(GetModel(name));

    private ModelConfiguration GetModel(string name) => configuration.ModelConfigurations.FirstOrDefault(m =>
        m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Model configuration '{name}' was not found.");

    private string GetKey(string alias) => configuration.ApiKeys.FirstOrDefault(k =>
        k.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))?.Value
        ?? throw new InvalidOperationException($"API key alias '{alias}' was not found.");
}

internal sealed class EmbeddingsAdapter(IEmbeddingGenerator<string, Embedding<float>> generator) : IEmbeddings
{
    public async Task<Embedding<float>> GetEmbeddings(string text) => await generator.GenerateAsync(text);

    public async Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts) =>
        await generator.GenerateAsync(texts);
}
