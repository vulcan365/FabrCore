namespace FabrCore.Core.Monitoring
{
    /// <summary>
    /// Accumulated LLM token usage for a single agent.
    /// Fields are public to support thread-safe <see cref="System.Threading.Interlocked"/> operations.
    /// </summary>
    public class AgentTokenSummary
    {
        public string AgentHandle { get; set; } = string.Empty;
        public long TotalInputTokens;
        public long TotalOutputTokens;
        public long TotalReasoningTokens;
        public long TotalCachedInputTokens;
        public long TotalLlmCalls;
        public long TotalMessages;
    }
}
