using FabrCore.Core.Monitoring;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace FabrCore.Sdk
{
    /// <summary>
    /// A delegating chat client that intercepts LLM responses and:
    /// <list type="bullet">
    /// <item>records usage metrics into the current <see cref="LlmUsageScope"/>; and</item>
    /// <item>records a <see cref="MonitoredLlmCall"/> to the agent monitor (if one is
    /// configured), so each individual LLM request/response pair is observable
    /// independently from the agent message track.</item>
    /// </list>
    /// </summary>
    public class TokenTrackingChatClient : DelegatingChatClient
    {
        private readonly string? _agentHandle;
        private readonly IAgentMessageMonitor? _monitor;
        private readonly LlmCaptureOptions? _capture;
        private readonly ILogger? _logger;

        /// <summary>Back-compat constructor used by tests and code paths that don't have monitor wiring.</summary>
        public TokenTrackingChatClient(IChatClient innerClient) : base(innerClient) { }

        /// <summary>
        /// Creates a chat client wrapper that records LLM calls to the monitor and tags them with
        /// <paramref name="agentHandle"/> when no <see cref="LlmUsageScope"/> or
        /// <see cref="LlmCallContext"/> is active (e.g. background/timer work).
        /// </summary>
        public TokenTrackingChatClient(
            IChatClient innerClient,
            string? agentHandle,
            IAgentMessageMonitor? monitor,
            ILogger? logger = null) : base(innerClient)
        {
            _agentHandle = agentHandle;
            _monitor = monitor;
            _capture = monitor?.LlmCaptureOptions;
            _logger = logger;
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();

            // Materialize once if we'll need to snapshot the request.
            IReadOnlyList<ChatMessage>? materialized = null;
            IEnumerable<ChatMessage> toSend = messages;
            if (ShouldCapturePayloads())
            {
                materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
                toSend = materialized;
            }

            ChatResponse? response = null;
            Exception? error = null;
            try
            {
                response = await base.GetResponseAsync(toSend, options, cancellationToken);
                return response;
            }
            catch (Exception ex)
            {
                error = ex;
                throw;
            }
            finally
            {
                sw.Stop();

                if (response is not null)
                {
                    LlmUsageScope.Current?.Record(response, sw.ElapsedMilliseconds);
                }

                TryRecordLlmCall(materialized, response, sw.ElapsedMilliseconds, streaming: false, error);
            }
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

            // Materialize once if we'll need to snapshot the request.
            IReadOnlyList<ChatMessage>? materialized = null;
            IEnumerable<ChatMessage> toSend = messages;
            if (ShouldCapturePayloads())
            {
                materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
                toSend = materialized;
            }

            // Accumulate updates only when the monitor is interested in the full response.
            List<ChatResponseUpdate>? collectedUpdates = (_capture?.Enabled == true) ? new List<ChatResponseUpdate>() : null;

            await foreach (var update in base.GetStreamingResponseAsync(toSend, options, cancellationToken))
            {
                collectedUpdates?.Add(update);

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

            var aggregated = collectedUpdates is not null
                ? collectedUpdates.ToChatResponse()
                : new ChatResponse([]);
            aggregated.ModelId = modelId;
            aggregated.FinishReason = finishReason is not null ? new ChatFinishReason(finishReason) : null;
            aggregated.Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                ReasoningTokenCount = reasoningTokens,
                CachedInputTokenCount = cachedInputTokens
            };

            LlmUsageScope.Current?.Record(aggregated, sw.ElapsedMilliseconds);

            TryRecordLlmCall(materialized, aggregated, sw.ElapsedMilliseconds, streaming: true, error: null);
        }

        // ── LLM call capture ──

        private bool ShouldCapturePayloads() => _capture is { Enabled: true, CapturePayloads: true };

        private void TryRecordLlmCall(
            IReadOnlyList<ChatMessage>? requestMessages,
            ChatResponse? response,
            long elapsedMs,
            bool streaming,
            Exception? error)
        {
            if (_monitor is null || _capture is null || !_capture.Enabled)
            {
                return;
            }

            // Attribution: LlmUsageScope provides the OnMessage context (handle, parent id,
            // trace id, default origin). LlmCallContext — if present — overrides the origin
            // tag so nested work like compaction or timer-triggered calls can be distinguished
            // while still inheriting the parent message correlation when there is one.
            var scope = LlmUsageScope.Current;
            var ctx = LlmCallContext.Current;

            string? handle = scope?.AgentHandle ?? ctx?.AgentHandle ?? _agentHandle;
            string? parentId = scope?.ParentMessageId;
            string? traceId = scope?.TraceId ?? ctx?.TraceId;
            string origin =
                ctx?.OriginContext
                ?? scope?.OriginContext
                ?? (scope is not null ? "OnMessage" : "Background");

            var call = new MonitoredLlmCall
            {
                AgentHandle = handle,
                TraceId = traceId,
                ParentMessageId = parentId,
                OriginContext = origin,
                Streaming = streaming,
                DurationMs = elapsedMs,
                ErrorMessage = error?.Message,
                Model = response?.ModelId,
                FinishReason = response?.FinishReason?.Value,
                InputTokens = response?.Usage?.InputTokenCount ?? 0,
                OutputTokens = response?.Usage?.OutputTokenCount ?? 0,
                ReasoningTokens = response?.Usage?.ReasoningTokenCount ?? 0,
                CachedInputTokens = response?.Usage?.CachedInputTokenCount ?? 0,
            };

            if (_capture.CapturePayloads)
            {
                call.RequestMessages = SnapshotMessages(requestMessages, _capture);
                call.ResponseMessages = SnapshotMessages(response?.Messages, _capture);
                call.ResponseText = TruncateAndRedact(response?.Text, _capture.MaxPayloadChars, _capture.Redact, out _);
                call.ToolCalls = SnapshotToolCalls(response?.Messages, _capture);
            }

            // Fire-and-forget so LLM latency is never gated on monitor IO.
            try
            {
                _ = _monitor.RecordLlmCallAsync(call).ContinueWith(
                    t => _logger?.LogWarning(t.Exception, "RecordLlmCallAsync failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to dispatch RecordLlmCallAsync");
            }
        }

        private static List<LlmMessageSnapshot>? SnapshotMessages(
            IEnumerable<ChatMessage>? messages,
            LlmCaptureOptions capture)
        {
            if (messages is null) return null;

            var result = new List<LlmMessageSnapshot>();
            foreach (var m in messages)
            {
                var sb = new StringBuilder();
                int contentCount = 0;
                foreach (var c in m.Contents)
                {
                    contentCount++;
                    if (c is TextContent tc && tc.Text is { Length: > 0 })
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(tc.Text);
                    }
                }

                var text = TruncateAndRedact(sb.Length > 0 ? sb.ToString() : null, capture.MaxPayloadChars, capture.Redact, out var truncated);

                result.Add(new LlmMessageSnapshot
                {
                    Role = m.Role.Value ?? "",
                    Text = text,
                    ContentCount = contentCount,
                    Truncated = truncated
                });
            }
            return result;
        }

        private static List<LlmToolCallSnapshot>? SnapshotToolCalls(
            IEnumerable<ChatMessage>? messages,
            LlmCaptureOptions capture)
        {
            if (messages is null) return null;

            List<LlmToolCallSnapshot>? result = null;
            foreach (var m in messages)
            {
                foreach (var c in m.Contents)
                {
                    if (c is FunctionCallContent fcc)
                    {
                        string? args = null;
                        if (fcc.Arguments is { Count: > 0 })
                        {
                            try { args = JsonSerializer.Serialize(fcc.Arguments); }
                            catch { args = null; }
                        }

                        var truncatedArgs = TruncateAndRedact(args, capture.MaxToolArgsChars, capture.Redact, out var truncated);

                        (result ??= new List<LlmToolCallSnapshot>()).Add(new LlmToolCallSnapshot
                        {
                            CallId = fcc.CallId,
                            Name = fcc.Name,
                            Arguments = truncatedArgs,
                            Truncated = truncated
                        });
                    }
                }
            }
            return result;
        }

        private static string? TruncateAndRedact(string? value, int maxChars, Func<string, string>? redact, out bool truncated)
        {
            truncated = false;
            if (value is null) return null;

            if (redact is not null)
            {
                try { value = redact(value); }
                catch { /* redaction must never throw out of capture */ }
            }

            if (value.Length > maxChars)
            {
                value = value.Substring(0, maxChars);
                truncated = true;
            }
            return value;
        }
    }
}
