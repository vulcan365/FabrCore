using FabrCore.Core.Monitoring;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Default in-memory implementation of <see cref="IAgentMessageMonitor"/>.
    /// Stores messages in a bounded FIFO buffer and tracks accumulated LLM token usage per agent.
    /// </summary>
    public class InMemoryAgentMessageMonitor : IAgentMessageMonitor
    {
        private readonly ConcurrentQueue<MonitoredMessage> _messages = new();
        private readonly ConcurrentQueue<MonitoredEvent> _events = new();
        private readonly ConcurrentQueue<MonitoredLlmCall> _llmCalls = new();
        private readonly ConcurrentDictionary<string, AgentTokenSummary> _tokenSummaries = new();
        private readonly ILogger<InMemoryAgentMessageMonitor> _logger;
        private readonly int _maxMessages;
        private int _count;
        private int _eventCount;
        private int _llmCallCount;

        public event Action<MonitoredMessage>? OnMessageRecorded;
        public event Action<MonitoredEvent>? OnEventRecorded;
        public event Action<MonitoredLlmCall>? OnLlmCallRecorded;

        /// <inheritdoc />
        public LlmCaptureOptions LlmCaptureOptions { get; }

        public InMemoryAgentMessageMonitor(ILogger<InMemoryAgentMessageMonitor> logger, int maxMessages = 5000)
            : this(logger, new LlmCaptureOptions(), maxMessages)
        {
        }

        public InMemoryAgentMessageMonitor(
            ILogger<InMemoryAgentMessageMonitor> logger,
            LlmCaptureOptions llmCaptureOptions,
            int maxMessages = 5000)
        {
            _logger = logger;
            _maxMessages = maxMessages;
            LlmCaptureOptions = llmCaptureOptions ?? new LlmCaptureOptions();
        }

        public Task RecordMessageAsync(MonitoredMessage message)
        {
            _messages.Enqueue(message);
            var currentCount = Interlocked.Increment(ref _count);

            // FIFO eviction
            while (currentCount > _maxMessages && _messages.TryDequeue(out _))
            {
                currentCount = Interlocked.Decrement(ref _count);
            }

            // Accumulate token usage per agent
            if (message.LlmUsage is { } usage && !string.IsNullOrEmpty(message.AgentHandle))
            {
                _tokenSummaries.AddOrUpdate(
                    message.AgentHandle,
                    _ => new AgentTokenSummary
                    {
                        AgentHandle = message.AgentHandle,
                        TotalInputTokens = usage.InputTokens,
                        TotalOutputTokens = usage.OutputTokens,
                        TotalReasoningTokens = usage.ReasoningTokens,
                        TotalCachedInputTokens = usage.CachedInputTokens,
                        TotalLlmCalls = usage.LlmCalls,
                        TotalMessages = 1
                    },
                    (_, existing) =>
                    {
                        Interlocked.Add(ref existing.TotalInputTokens, usage.InputTokens);
                        Interlocked.Add(ref existing.TotalOutputTokens, usage.OutputTokens);
                        Interlocked.Add(ref existing.TotalReasoningTokens, usage.ReasoningTokens);
                        Interlocked.Add(ref existing.TotalCachedInputTokens, usage.CachedInputTokens);
                        Interlocked.Add(ref existing.TotalLlmCalls, usage.LlmCalls);
                        Interlocked.Increment(ref existing.TotalMessages);
                        return existing;
                    });
            }

            // Fire notification — one throwing subscriber must not prevent siblings from running.
            SafeInvoke(OnMessageRecorded, message, nameof(OnMessageRecorded));

            return Task.CompletedTask;
        }

        public Task<List<MonitoredMessage>> GetMessagesAsync(string? agentHandle = null, int? limit = null)
        {
            IEnumerable<MonitoredMessage> result = _messages.ToArray().Reverse();

            if (!string.IsNullOrEmpty(agentHandle))
                result = result.Where(m => m.AgentHandle == agentHandle);

            if (limit.HasValue)
                result = result.Take(limit.Value);

            return Task.FromResult(result.ToList());
        }

        public Task RecordEventAsync(MonitoredEvent evt)
        {
            _events.Enqueue(evt);
            var currentCount = Interlocked.Increment(ref _eventCount);

            // FIFO eviction — bounded independently from the message queue
            while (currentCount > _maxMessages && _events.TryDequeue(out _))
            {
                currentCount = Interlocked.Decrement(ref _eventCount);
            }

            // Fire notification — one throwing subscriber must not prevent siblings from running.
            SafeInvoke(OnEventRecorded, evt, nameof(OnEventRecorded));

            return Task.CompletedTask;
        }

        public Task<List<MonitoredEvent>> GetEventsAsync(string? agentHandle = null, int? limit = null)
        {
            IEnumerable<MonitoredEvent> result = _events.ToArray().Reverse();

            if (!string.IsNullOrEmpty(agentHandle))
                result = result.Where(e => e.AgentHandle == agentHandle);

            if (limit.HasValue)
                result = result.Take(limit.Value);

            return Task.FromResult(result.ToList());
        }

        public Task RecordLlmCallAsync(MonitoredLlmCall call)
        {
            if (!LlmCaptureOptions.Enabled)
            {
                return Task.CompletedTask;
            }

            _llmCalls.Enqueue(call);
            var currentCount = Interlocked.Increment(ref _llmCallCount);

            // FIFO eviction — bounded independently from messages and events.
            var max = LlmCaptureOptions.MaxBufferedCalls;
            while (currentCount > max && _llmCalls.TryDequeue(out _))
            {
                currentCount = Interlocked.Decrement(ref _llmCallCount);
            }

            // Fire notification — one throwing subscriber must not prevent siblings from running.
            SafeInvoke(OnLlmCallRecorded, call, nameof(OnLlmCallRecorded));

            return Task.CompletedTask;
        }

        public Task<List<MonitoredLlmCall>> GetLlmCallsAsync(string? agentHandle = null, int? limit = null)
        {
            IEnumerable<MonitoredLlmCall> result = _llmCalls.ToArray().Reverse();

            if (!string.IsNullOrEmpty(agentHandle))
                result = result.Where(c => c.AgentHandle == agentHandle);

            if (limit.HasValue)
                result = result.Take(limit.Value);

            return Task.FromResult(result.ToList());
        }

        public Task<AgentTokenSummary?> GetAgentTokenSummaryAsync(string agentHandle)
        {
            _tokenSummaries.TryGetValue(agentHandle, out var summary);
            return Task.FromResult(summary);
        }

        public Task<List<AgentTokenSummary>> GetAllAgentTokenSummariesAsync()
        {
            return Task.FromResult(_tokenSummaries.Values.ToList());
        }

        public Task ClearAsync()
        {
            while (_messages.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _count, 0);
            while (_events.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _eventCount, 0);
            while (_llmCalls.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _llmCallCount, 0);
            _tokenSummaries.Clear();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Invokes each subscriber in the multicast delegate individually, so one
        /// throwing subscriber can't prevent later ones from running.
        /// </summary>
        private void SafeInvoke<T>(Action<T>? handler, T arg, string eventName)
        {
            if (handler is null) return;
            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((Action<T>)d).Invoke(arg);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{EventName} subscriber threw an exception", eventName);
                }
            }
        }
    }
}
