using FabrCore.Core;
using FabrCore.Sdk;
using System.Text.Json;

namespace FabrCore.Tests.Infrastructure;

/// <summary>
/// In-memory implementation of IFabrCoreAgentHost for testing FabrCoreAgentProxy
/// without requiring an Orleans silo. All state is stored in memory.
/// </summary>
public class TestFabrCoreAgentHost : IFabrCoreAgentHost
{
    private readonly string _handle;
    private readonly Dictionary<string, List<StoredChatMessage>> _threads = new();
    private readonly Dictionary<string, JsonElement> _customState = new();
    private readonly List<FabrCoreChatHistoryProvider> _trackedProviders = new();

    /// <summary>Messages sent via SendMessage or SendAndReceiveMessage, for test assertions.</summary>
    public List<AgentMessage> SentMessages { get; } = new();

    /// <summary>Events sent via SendEvent, for test assertions.</summary>
    public List<EventMessage> SentEvents { get; } = new();

    /// <summary>Registered timer names, for test assertions.</summary>
    public List<string> RegisteredTimers { get; } = new();

    /// <summary>Registered reminder names, for test assertions.</summary>
    public List<string> RegisteredReminders { get; } = new();

    public TestFabrCoreAgentHost(string handle = "test-agent")
    {
        _handle = handle;
    }

    public string GetHandle() => _handle;

    public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
    {
        SentMessages.Add(request);
        var response = new AgentMessage
        {
            FromHandle = request.ToHandle,
            ToHandle = request.FromHandle,
            Message = $"[TestHost] Echo: {request.Message}",
            Kind = MessageKind.Response
        };
        return Task.FromResult(response);
    }

    public Task SendMessage(AgentMessage request)
    {
        SentMessages.Add(request);
        return Task.CompletedTask;
    }

    public Task SendEvent(EventMessage request, string? streamName = null)
    {
        SentEvents.Add(request);
        return Task.CompletedTask;
    }

    public Task<AgentHealthStatus> GetAgentHealth(string? handle = null, HealthDetailLevel detailLevel = HealthDetailLevel.Detailed)
    {
        return Task.FromResult(new AgentHealthStatus
        {
            Handle = handle ?? _handle,
            State = HealthState.Healthy,
            Timestamp = DateTime.UtcNow,
            IsConfigured = true,
            Message = "Test agent is healthy"
        });
    }

    public void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
    {
        RegisteredTimers.Add(timerName);
    }

    public void UnregisterTimer(string timerName)
    {
        RegisteredTimers.Remove(timerName);
    }

    public Task RegisterReminder(string reminderName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
    {
        RegisteredReminders.Add(reminderName);
        return Task.CompletedTask;
    }

    public Task UnregisterReminder(string reminderName)
    {
        RegisteredReminders.Remove(reminderName);
        return Task.CompletedTask;
    }

    public FabrCoreChatHistoryProvider GetChatHistoryProvider(string threadId)
    {
        var provider = new FabrCoreChatHistoryProvider(this, threadId);
        _trackedProviders.Add(provider);
        return provider;
    }

    public void TrackChatHistoryProvider(FabrCoreChatHistoryProvider provider)
    {
        if (!_trackedProviders.Contains(provider))
            _trackedProviders.Add(provider);
    }

    public Task<List<StoredChatMessage>> GetThreadMessagesAsync(string threadId)
    {
        if (_threads.TryGetValue(threadId, out var messages))
            return Task.FromResult(new List<StoredChatMessage>(messages));
        return Task.FromResult(new List<StoredChatMessage>());
    }

    public Task AddThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
    {
        if (!_threads.ContainsKey(threadId))
            _threads[threadId] = new List<StoredChatMessage>();
        _threads[threadId].AddRange(messages);
        return Task.CompletedTask;
    }

    public Task ClearThreadAsync(string threadId)
    {
        _threads.Remove(threadId);
        return Task.CompletedTask;
    }

    public Task ReplaceThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
    {
        _threads[threadId] = new List<StoredChatMessage>(messages);
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, JsonElement>> GetCustomStateAsync()
    {
        return Task.FromResult(new Dictionary<string, JsonElement>(_customState));
    }

    public Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
    {
        foreach (var key in deletes)
            _customState.Remove(key);
        foreach (var (key, value) in changes)
            _customState[key] = value;
        return Task.CompletedTask;
    }
}
