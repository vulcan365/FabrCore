namespace FabrCore.Core.Monitoring
{
    /// <summary>
    /// A captured snapshot of a single LLM request/response pair made by an agent.
    /// Stored in a separate FIFO buffer from messages and events so consumers can
    /// toggle visibility independently.
    /// </summary>
    public class MonitoredLlmCall
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        // ── Identity / correlation ──

        /// <summary>The agent handle that made this LLM call.</summary>
        public string? AgentHandle { get; set; }

        /// <summary>Distributed trace id inherited from the parent scope, if any.</summary>
        public string? TraceId { get; set; }

        /// <summary>
        /// Id of the <see cref="MonitoredMessage"/> that triggered this call, when the call
        /// originated from an <c>OnMessage</c> flow. Null for timer/event/background calls.
        /// </summary>
        public string? ParentMessageId { get; set; }

        /// <summary>
        /// Where the call originated. Examples:
        /// <c>OnMessage:&lt;id&gt;</c>, <c>OnEvent:&lt;type&gt;</c>, <c>Timer:&lt;name&gt;</c>,
        /// <c>Compaction</c>, <c>Background</c>.
        /// </summary>
        public string OriginContext { get; set; } = "";

        // ── Metadata (always captured) ──

        public string? Model { get; set; }
        public long DurationMs { get; set; }
        public bool Streaming { get; set; }
        public string? FinishReason { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ReasoningTokens { get; set; }
        public long CachedInputTokens { get; set; }

        /// <summary>Populated when the underlying LLM call threw.</summary>
        public string? ErrorMessage { get; set; }

        // ── Payloads (only populated when LlmCaptureOptions.CapturePayloads is true) ──

        public List<LlmMessageSnapshot>? RequestMessages { get; set; }
        public List<LlmMessageSnapshot>? ResponseMessages { get; set; }
        public string? ResponseText { get; set; }
        public List<LlmToolCallSnapshot>? ToolCalls { get; set; }
    }

    /// <summary>Snapshot of a single chat message sent to or returned from the LLM.</summary>
    public class LlmMessageSnapshot
    {
        /// <summary>Role: system, user, assistant, tool, etc.</summary>
        public string Role { get; set; } = "";

        /// <summary>Concatenated text content, truncated and redacted per capture options.</summary>
        public string? Text { get; set; }

        /// <summary>Total number of content parts on the original message (text, image, tool calls, ...).</summary>
        public int ContentCount { get; set; }

        /// <summary>True when the text was truncated by the size cap.</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>Snapshot of a tool/function call emitted by the LLM in a response message.</summary>
    public class LlmToolCallSnapshot
    {
        public string? CallId { get; set; }
        public string Name { get; set; } = "";

        /// <summary>Serialized JSON arguments, truncated and redacted per capture options.</summary>
        public string? Arguments { get; set; }

        /// <summary>True when the arguments were truncated by the size cap.</summary>
        public bool Truncated { get; set; }
    }
}
