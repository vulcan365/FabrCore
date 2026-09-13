using System.Collections.Concurrent;
using System.Diagnostics;
using FabrCore.Core;
using FabrCore.Services.GraphRag.Services;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.GraphRag.EvalConsole;

internal sealed record ChatCallSample(string ModelAlias, long ElapsedMs, long? InputTokens,
    long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens, string? ErrorType)
{
    public string? ResponseSchemaName { get; init; }
    public int? InvalidSourceReferences { get; set; }
    public string? ExtractionResponseJson { get; init; }
    public string? RawProviderResponseJson { get; init; }
    public EvidenceValidation? EvidenceValidation { get; set; }
}

/// <summary>Observes provider usage, with optional eval-only format adapters beneath the observer.</summary>
internal sealed class MeasuredChatClientService(IFabrCoreChatClientService inner, bool captureResponses = false,
    bool useJsonObjectResponses = false, bool guideJsonObjectResponses = false,
    bool normalizeJsonArrays = false) : IFabrCoreChatClientService
{
    private readonly ConcurrentQueue<ChatCallSample> _calls = new();
    private int active, peak;
    public int PeakConcurrency => Volatile.Read(ref peak);
    public void ResetPeakConcurrency() => Interlocked.Exchange(ref peak, 0);
    private void Started()
    {
        var count = Interlocked.Increment(ref active);
        int observed;
        do { observed = Volatile.Read(ref peak); }
        while (count > observed && Interlocked.CompareExchange(ref peak, count, observed) != observed);
    }


    public List<ChatCallSample> Drain()
    {
        var result = new List<ChatCallSample>();
        while (_calls.TryDequeue(out var sample)) result.Add(sample);
        return result;
    }

    public async Task<IChatClient> GetChatClient(string name, int networkTimeoutSeconds = 100)
    {
        var client = await inner.GetChatClient(name, networkTimeoutSeconds);
        if (useJsonObjectResponses) client = new JsonObjectChatClient(client, guideJsonObjectResponses, normalizeJsonArrays);
        return new MeasuredChatClient(client, name, _calls, captureResponses, this);
    }

    public Task<ModelConfiguration> GetModelConfigurationAsync(string name) => inner.GetModelConfigurationAsync(name);
    public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingsClient(string name) => inner.GetEmbeddingsClient(name);
#pragma warning disable MEAI001
    public Task<ISpeechToTextClient> GetAudioClient(string name, int networkTimeoutSeconds = 100)
        => inner.GetAudioClient(name, networkTimeoutSeconds);
#pragma warning restore MEAI001

    private sealed class MeasuredChatClient(IChatClient inner, string model, ConcurrentQueue<ChatCallSample> samples, bool captureResponses, MeasuredChatClientService owner)
        : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var target = DocumentMeasurements.Current?.ChatCalls ?? samples;
            owner.Started();
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await base.GetResponseAsync(messages, options, cancellationToken);
                var usage = response.Usage;
                target.Enqueue(new(model, sw.ElapsedMilliseconds, usage?.InputTokenCount, usage?.OutputTokenCount,
                    usage?.CachedInputTokenCount, usage?.ReasoningTokenCount, null)
                { ExtractionResponseJson = captureResponses ? response.Text : null,
                    RawProviderResponseJson = captureResponses && response.AdditionalProperties?.TryGetValue(JsonObjectChatClient.RawResponseKey, out var raw) == true ? raw as string : null,
                    ResponseSchemaName = (options?.ResponseFormat as ChatResponseFormatJson)?.SchemaName });
                return response;
            }
            catch (Exception ex)
            {
                target.Enqueue(new(model, sw.ElapsedMilliseconds, null, null, null, null, ex.GetType().Name));
                throw;
            }
            finally { Interlocked.Decrement(ref owner.active); }
        }
    }
}
