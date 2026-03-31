using Microsoft.Extensions.AI;

namespace FabrCore.Sdk
{
    /// <summary>
    /// An AsyncLocal scope that accumulates LLM usage metrics across all calls within a single OnMessage invocation.
    /// </summary>
    public sealed class LlmUsageScope : IDisposable
    {
        private static readonly AsyncLocal<LlmUsageScope?> _current = new();

        private long _inputTokens;
        private long _outputTokens;
        private long _reasoningTokens;
        private long _cachedInputTokens;
        private long _callCount;
        private long _durationMs;
        private string? _modelId;
        private string? _finishReason;

        /// <summary>Gets the current active scope, or null if none.</summary>
        public static LlmUsageScope? Current => _current.Value;

        public long InputTokens => Interlocked.Read(ref _inputTokens);
        public long OutputTokens => Interlocked.Read(ref _outputTokens);
        public long ReasoningTokens => Interlocked.Read(ref _reasoningTokens);
        public long CachedInputTokens => Interlocked.Read(ref _cachedInputTokens);
        public long CallCount => Interlocked.Read(ref _callCount);
        public long DurationMs => Interlocked.Read(ref _durationMs);
        public string? ModelId => _modelId;
        public string? FinishReason => _finishReason;

        /// <summary>Starts a new LLM usage tracking scope.</summary>
        public static LlmUsageScope Begin()
        {
            var scope = new LlmUsageScope();
            _current.Value = scope;
            return scope;
        }

        /// <summary>Records metrics from a completed LLM call.</summary>
        public void Record(ChatResponse response, long elapsedMs)
        {
            Interlocked.Increment(ref _callCount);
            Interlocked.Add(ref _durationMs, elapsedMs);

            if (response.Usage is { } usage)
            {
                Interlocked.Add(ref _inputTokens, usage.InputTokenCount ?? 0);
                Interlocked.Add(ref _outputTokens, usage.OutputTokenCount ?? 0);
                Interlocked.Add(ref _reasoningTokens, usage.ReasoningTokenCount ?? 0);
                Interlocked.Add(ref _cachedInputTokens, usage.CachedInputTokenCount ?? 0);
            }

            if (response.ModelId is { } model) _modelId = model;
            if (response.FinishReason is { } reason) _finishReason = reason.Value;
        }

        /// <summary>Applies accumulated metrics to an AgentMessage Args dictionary.</summary>
        public void ApplyTo(Dictionary<string, string> args)
        {
            if (InputTokens > 0) args["_tokens_input"] = InputTokens.ToString();
            if (OutputTokens > 0) args["_tokens_output"] = OutputTokens.ToString();
            if (ReasoningTokens > 0) args["_tokens_reasoning"] = ReasoningTokens.ToString();
            if (CachedInputTokens > 0) args["_tokens_cached_input"] = CachedInputTokens.ToString();
            if (CallCount > 0) args["_llm_calls"] = CallCount.ToString();
            if (DurationMs > 0) args["_llm_duration_ms"] = DurationMs.ToString();
            if (ModelId is not null) args["_model"] = ModelId;
            if (FinishReason is not null) args["_finish_reason"] = FinishReason;
        }

        public void Dispose() => _current.Value = null;
    }
}
