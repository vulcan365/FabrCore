using FabrCore.Core.Monitoring;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// No-op implementation of <see cref="IAgentMessageMonitor"/> used when monitoring is not enabled.
    /// </summary>
    internal sealed class NullAgentMessageMonitor : IAgentMessageMonitor
    {
#pragma warning disable CS0067 // Events are intentionally unused in no-op implementation
        public event Action<MonitoredMessage>? OnMessageRecorded;
        public event Action<MonitoredEvent>? OnEventRecorded;
#pragma warning restore CS0067

        public Task RecordMessageAsync(MonitoredMessage message) => Task.CompletedTask;

        public Task<List<MonitoredMessage>> GetMessagesAsync(string? agentHandle = null, int? limit = null)
            => Task.FromResult(new List<MonitoredMessage>());

        public Task<AgentTokenSummary?> GetAgentTokenSummaryAsync(string agentHandle)
            => Task.FromResult<AgentTokenSummary?>(null);

        public Task<List<AgentTokenSummary>> GetAllAgentTokenSummariesAsync()
            => Task.FromResult(new List<AgentTokenSummary>());

        public Task RecordEventAsync(MonitoredEvent evt) => Task.CompletedTask;

        public Task<List<MonitoredEvent>> GetEventsAsync(string? agentHandle = null, int? limit = null)
            => Task.FromResult(new List<MonitoredEvent>());

        public Task ClearAsync() => Task.CompletedTask;
    }
}
