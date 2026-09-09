using System.Collections.Concurrent;
using System.Diagnostics;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.EvalConsole;

internal sealed record Call(string Model, long Ms, long? InputTokens, long? OutputTokens, string? Error, string? Response)
{
    public long? CachedInputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public string? ResponseSchemaName { get; init; }
}
internal sealed class MeasuredClients(IFabrCoreChatClientService inner) : IFabrCoreChatClientService
{
    public ConcurrentQueue<Call> Calls { get; } = new();
    public async Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
        => new Client(await inner.GetChatClient(name, networkTimeoutSeconds), name, Calls);
    public Task<ModelConfiguration> GetModelConfigurationAsync(string name) => inner.GetModelConfigurationAsync(name);
    public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name) => inner.GetEmbeddingsClient(name);
#pragma warning disable MEAI001
    public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100) => inner.GetAudioClient(name, networkTimeoutSeconds);
#pragma warning restore MEAI001
    private sealed class Client(IChatClient inner, string name, ConcurrentQueue<Call> calls) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await base.GetResponseAsync(messages, options, cancellationToken);
                calls.Enqueue(new(name, sw.ElapsedMilliseconds, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, null, response.Text)
                { CachedInputTokens = response.Usage?.CachedInputTokenCount, ReasoningTokens = response.Usage?.ReasoningTokenCount,
                    ResponseSchemaName = (options?.ResponseFormat as ChatResponseFormatJson)?.SchemaName });
                return response;
            }
            catch (Exception ex) { calls.Enqueue(new(name, sw.ElapsedMilliseconds, null, null, ex.GetType().Name, null)); throw; }
        }
    }
}
