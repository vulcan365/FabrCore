namespace FabrCore.Sdk
{
    /// <summary>
    /// An AsyncLocal context used to attribute LLM calls that happen outside an
    /// <see cref="LlmUsageScope"/> (timers, <c>OnEvent</c> handlers, background work).
    /// Wrap the relevant code with <c>using (LlmCallContext.Begin(handle, "Timer:foo")) { ... }</c>
    /// so <see cref="TokenTrackingChatClient"/> can tag the captured LLM call correctly.
    /// </summary>
    public sealed class LlmCallContext : IDisposable
    {
        private static readonly AsyncLocal<LlmCallContext?> _current = new();

        /// <summary>Gets the current active context, or null if none.</summary>
        public static LlmCallContext? Current => _current.Value;

        public string? AgentHandle { get; init; }
        public string OriginContext { get; init; } = "Background";
        public string? TraceId { get; init; }

        /// <summary>Starts a new background LLM call context.</summary>
        public static LlmCallContext Begin(string agentHandle, string originContext, string? traceId = null)
        {
            var ctx = new LlmCallContext
            {
                AgentHandle = agentHandle,
                OriginContext = originContext,
                TraceId = traceId
            };
            _current.Value = ctx;
            return ctx;
        }

        public void Dispose() => _current.Value = null;
    }
}
