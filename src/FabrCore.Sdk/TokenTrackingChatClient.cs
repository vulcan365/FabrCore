using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FabrCore.Sdk
{
    /// <summary>
    /// A delegating chat client that intercepts LLM responses and records usage metrics
    /// into the current <see cref="LlmUsageScope"/>.
    /// </summary>
    public class TokenTrackingChatClient : DelegatingChatClient
    {
        public TokenTrackingChatClient(IChatClient innerClient) : base(innerClient) { }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            sw.Stop();

            LlmUsageScope.Current?.Record(response, sw.ElapsedMilliseconds);

            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            long inputTokens = 0;
            long outputTokens = 0;
            long reasoningTokens = 0;
            long cachedInputTokens = 0;
            string? modelId = null;
            string? finishReason = null;

            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                // Check for UsageContent in the update's contents
                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usageContent && usageContent.Details is { } usage)
                    {
                        inputTokens += usage.InputTokenCount ?? 0;
                        outputTokens += usage.OutputTokenCount ?? 0;
                        reasoningTokens += usage.ReasoningTokenCount ?? 0;
                        cachedInputTokens += usage.CachedInputTokenCount ?? 0;
                    }
                }

                if (update.ModelId is not null) modelId = update.ModelId;
                if (update.FinishReason is { } fr) finishReason = fr.Value;

                yield return update;
            }

            sw.Stop();

            if (LlmUsageScope.Current is { } scope)
            {
                // Build a minimal ChatResponse to record aggregated streaming metrics
                var aggregated = new ChatResponse([])
                {
                    ModelId = modelId,
                    FinishReason = finishReason is not null ? new ChatFinishReason(finishReason) : null,
                    Usage = new UsageDetails
                    {
                        InputTokenCount = inputTokens,
                        OutputTokenCount = outputTokens,
                        ReasoningTokenCount = reasoningTokens,
                        CachedInputTokenCount = cachedInputTokens
                    }
                };
                scope.Record(aggregated, sw.ElapsedMilliseconds);
            }
        }
    }
}
