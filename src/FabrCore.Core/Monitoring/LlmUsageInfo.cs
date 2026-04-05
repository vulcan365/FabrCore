namespace FabrCore.Core.Monitoring
{
    /// <summary>
    /// LLM usage metrics captured from an agent response.
    /// </summary>
    public class LlmUsageInfo
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long LlmCalls { get; set; }
        public long LlmDurationMs { get; set; }
        public string? Model { get; set; }
        public string? FinishReason { get; set; }

        /// <summary>
        /// Extracts LLM usage from an AgentMessage's Args dictionary.
        /// Returns null if no LLM calls were made (no <c>_llm_calls</c> key).
        /// </summary>
        public static LlmUsageInfo? FromArgs(Dictionary<string, string>? args)
        {
            if (args is null || !args.TryGetValue("_llm_calls", out var llmCalls))
                return null;

            args.TryGetValue("_tokens_input", out var tokIn);
            args.TryGetValue("_tokens_output", out var tokOut);
            args.TryGetValue("_tokens_reasoning", out var tokReasoning);
            args.TryGetValue("_tokens_cached_input", out var tokCached);
            args.TryGetValue("_llm_duration_ms", out var llmDuration);
            args.TryGetValue("_model", out var model);
            args.TryGetValue("_finish_reason", out var finishReason);

            return new LlmUsageInfo
            {
                InputTokens = long.TryParse(tokIn, out var i) ? i : 0,
                OutputTokens = long.TryParse(tokOut, out var o) ? o : 0,
                ReasoningTokens = long.TryParse(tokReasoning, out var r) ? r : 0,
                CachedInputTokens = long.TryParse(tokCached, out var c) ? c : 0,
                LlmCalls = long.TryParse(llmCalls, out var calls) ? calls : 0,
                LlmDurationMs = long.TryParse(llmDuration, out var dur) ? dur : 0,
                Model = model,
                FinishReason = finishReason
            };
        }
    }
}
