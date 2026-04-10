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
        private readonly ConcurrentDictionary<string, AgentTokenSummary> _tokenSummaries = new();
        private readonly ILogger<InMemoryAgentMessageMonitor> _logger;
        private readonly int _maxMessages;
        private int _count;
        private int _eventCount;

        public event Action<MonitoredMessage>? OnMessageRecorded;
        public event Action<MonitoredEvent>? OnEventRecorded;

        public InMemoryAgentMessageMonitor(ILogger<InMemoryAgentMessageMonitor> logger, int maxMessages = 5000)
        {
            _logger = logger;
            _maxMessages = maxMessages;
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

            // Fire notification — never let subscriber exceptions propagate
            try
            {
                OnMessageRecorded?.Invoke(message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnMessageRecorded subscriber threw an exception");
            }

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

            // Fire notification — never let subscriber exceptions propagate
            try
            {
                OnEventRecorded?.Invoke(evt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnEventRecorded subscriber threw an exception");
            }

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
            _tokenSummaries.Clear();
            return Task.CompletedTask;
        }
    }
}
