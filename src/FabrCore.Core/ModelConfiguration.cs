namespace FabrCore.Core
{
    public class ModelConfiguration
    {
        public required string Name { get; set; }
        public required string Provider { get; set; }
        public required string Uri { get; set; }
        public required string Model { get; set; }
        public required string ApiKeyAlias { get; set; }

        /// <summary>
        /// Network timeout in seconds. Default is 60 seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Maximum number of tokens in the response. Default is null (no limit).
        /// Setting this can improve response time by limiting output length.
        /// </summary>
        /// <remarks>
        /// Also the output reserve for context compaction (layer 1). Together with
        /// <see cref="ContextWindowTokens"/> it defines the input budget; if either is null, context
        /// compaction cannot be composed and agents run with no in-run context bound.
        /// </remarks>
        public int? MaxOutputTokens { get; set; }

        /// <summary>
        /// Default reasoning effort for chat requests. Supported values are none, low,
        /// medium, high, and xhigh (or ExtraHigh). Null uses the provider default.
        /// </summary>
        public string? ReasoningEffort { get; set; }

        /// <summary>
        /// Total context window size in tokens for this model. Default is null (unknown).
        /// </summary>
        /// <remarks>
        /// This is the anchor for the whole compaction ladder — every other threshold is a fraction of it.
        /// Setting this and <see cref="MaxOutputTokens"/> is all most deployments ever need to configure.
        /// </remarks>
        public int? ContextWindowTokens { get; set; }

        // ── Layer 1: context compaction (in-run, free, reversible) ──

        /// <summary>
        /// Enable/disable in-run context compaction for agents using this model.
        /// Default is null (uses system default: true).
        /// </summary>
        /// <remarks>
        /// Context compaction bounds what a single LLM call sees. It never touches persisted history.
        /// Requires both <see cref="ContextWindowTokens"/> and <see cref="MaxOutputTokens"/>.
        /// </remarks>
        public bool? ContextCompactionEnabled { get; set; }

        /// <summary>
        /// Fraction of the input budget (window minus output reserve) at which old tool-call results
        /// collapse into one-line summaries. Default is null (uses system default: 0.5).
        /// </summary>
        public double? ContextEvictThreshold { get; set; }

        /// <summary>
        /// Fraction of the input budget at which the oldest message groups are dropped from the request.
        /// Must be greater than or equal to <see cref="ContextEvictThreshold"/>.
        /// Default is null (uses system default: 0.8).
        /// </summary>
        public double? ContextTruncateThreshold { get; set; }

        // ── Layer 2: history compaction (between turns, one LLM call, permanent) ──

        /// <summary>
        /// Enable/disable automatic history compaction for agents using this model.
        /// Default is null (uses system default: true).
        /// </summary>
        /// <remarks>
        /// History compaction summarizes and rewrites the persisted thread, which is what keeps the
        /// Orleans state blob bounded. Turning it off while context compaction is on keeps the model
        /// happy but lets stored history grow without limit.
        /// </remarks>
        public bool? CompactionEnabled { get; set; }

        /// <summary>
        /// Number of recent messages to keep during history compaction. Default is null (uses system default: 20).
        /// </summary>
        public int? CompactionKeepLastN { get; set; }

        /// <summary>
        /// Trigger history compaction when estimated stored tokens exceed this fraction of
        /// <see cref="ContextWindowTokens"/>.
        /// </summary>
        /// <remarks>
        /// Default is null, which resolves to 0.87 when context compaction is active — deliberately above
        /// layer 1's truncation point so the free reversible rung always fires first and history
        /// compaction acts as the between-turns consolidator. Falls back to 0.75 when context compaction
        /// is not configured, since history compaction is then the first responder.
        /// </remarks>
        public double? CompactionThreshold { get; set; }

        /// <summary>
        /// When a thread has been dormant for at least this many minutes AND stored tokens
        /// exceed the history compaction threshold, compaction runs before the next OnMessage call
        /// (preflight compaction). Default is null (uses system default: 60). Set to 0 to disable.
        /// </summary>
        public int? CompactionStaleAfterMinutes { get; set; }

        // ── Run safety: the budget stop, always on ──

        /// <summary>
        /// Maximum cumulative input tokens allowed during a single agent turn. Null or 0 disables the guard.
        /// </summary>
        public int? PerTurnMaxInputTokens { get; set; }

        /// <summary>
        /// Maximum estimated prompt tokens allowed for a single LLM call. Null or 0 disables the guard.
        /// </summary>
        public int? MaxPromptInputTokens { get; set; }

        /// <summary>
        /// No longer used. Mid-turn history compaction was retired in favour of context compaction
        /// (<see cref="ContextCompactionEnabled"/>), which bounds every call in the tool loop without
        /// rewriting the persisted thread mid-run.
        /// </summary>
        /// <remarks>
        /// Retained so existing <c>fabrcore.json</c> files keep deserializing. The value is ignored.
        /// </remarks>
        [Obsolete("Mid-turn history compaction was retired. Use ContextCompactionEnabled — layer 1 bounds every call in the tool loop. This value is ignored.")]
        public bool? MidTurnCompactionEnabled { get; set; }

        /// <summary>
        /// Behavior when a runaway prompt or turn budget is exceeded. Default is StopWithDiagnostic.
        /// </summary>
        public string? RunawayBudgetBehavior { get; set; }
    }

    public class ApiKeyConfiguration
    {
        public required string Alias { get; set; }
        public required string Value { get; set; }
    }

    public class FabrCoreConfiguration
    {
        public List<ModelConfiguration> ModelConfigurations { get; set; } = new();
        public List<ApiKeyConfiguration> ApiKeys { get; set; } = new();
    }
}
