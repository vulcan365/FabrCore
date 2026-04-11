namespace FabrCore.Core.Monitoring
{
    /// <summary>
    /// Configuration for the LLM call capture track of <see cref="IAgentMessageMonitor"/>.
    /// By default only metadata (model, tokens, duration, finish reason) is captured.
    /// Payload capture is opt-in because prompts/responses can be large and may contain PII.
    /// </summary>
    public class LlmCaptureOptions
    {
        /// <summary>Master switch for the LLM call track. When false, no LLM calls are recorded.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// When true, captures full request/response payloads (prompts, response text, tool args).
        /// When false (default), only metadata is captured.
        /// </summary>
        public bool CapturePayloads { get; set; } = false;

        /// <summary>Per-field character cap applied to each captured message/response string.</summary>
        public int MaxPayloadChars { get; set; } = 8_000;

        /// <summary>Character cap applied to each captured tool/function call arguments string.</summary>
        public int MaxToolArgsChars { get; set; } = 4_000;

        /// <summary>
        /// Optional redaction function invoked on every captured string before storage.
        /// Use this to strip API keys, secrets, or PII.
        /// </summary>
        public Func<string, string>? Redact { get; set; }

        /// <summary>
        /// Maximum number of LLM calls retained in the in-memory buffer. Older entries are
        /// evicted FIFO. Set lower than the message buffer because payloads are larger.
        /// </summary>
        public int MaxBufferedCalls { get; set; } = 2_000;
    }
}
