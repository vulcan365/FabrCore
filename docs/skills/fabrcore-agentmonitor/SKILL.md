---
name: fabrcore-agentmonitor
description: >
  FabrCore agent message and event monitoring — IAgentMessageMonitor, InMemoryAgentMessageMonitor, message traffic observation,
  event stream observation at OnEvent, LLM token tracking, building custom monitor providers, subscribing to
  message/event notifications for UI updates.
  Triggers on: "agent monitor", "message monitor", "IAgentMessageMonitor", "InMemoryAgentMessageMonitor",
  "monitor messages", "monitor events", "OnEvent", "event stream monitor", "track tokens", "agent token usage",
  "message traffic", "monitor provider", "OnMessageRecorded", "OnEventRecorded", "MonitoredMessage",
  "MonitoredEvent", "AgentTokenSummary", "message observation".
  Do NOT use for: agent lifecycle — use fabrcore-agent.
  Do NOT use for: OpenTelemetry metrics — use fabrcore-server.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Agent Message Monitor

## Overview

The Agent Message Monitor provides a pluggable provider for observing message and event traffic flowing through the FabrCore agent system. It captures:

- Every message an agent receives (inbound requests)
- Every response an agent sends (outbound responses with LLM usage)
- Messages arriving at clients via streams
- Every event that reaches an agent's `OnEvent` handler (inbound, fire-and-forget)
- LLM token usage per response (input, output, reasoning, cached tokens, model, duration)
- Accumulated token totals per agent

Messages and events live in **separate buffers** with **separate query methods and notification events**, so a client output viewer can filter to messages only, events only, or merge both into a unified timeline.

## Architecture

| Component | Location | Purpose |
|-----------|----------|---------|
| `IAgentMessageMonitor` | `FabrCore.Core.Monitoring` | Provider interface |
| `InMemoryAgentMessageMonitor` | `FabrCore.Host.Services` | Default bounded FIFO buffer implementation |
| `MonitoredMessage` | `FabrCore.Core.Monitoring` | Captured message snapshot |
| `MonitoredEvent` | `FabrCore.Core.Monitoring` | Captured event snapshot (from `OnEvent`) |
| `LlmUsageInfo` | `FabrCore.Core.Monitoring` | LLM usage metrics per response |
| `AgentTokenSummary` | `FabrCore.Core.Monitoring` | Accumulated token totals per agent |
| `MessageDirection` | `FabrCore.Core.Monitoring` | `Inbound` / `Outbound` enum |

## Enabling Monitoring

Monitoring is **opt-in**. Enable it in `AddFabrCoreServer`:

```csharp
// Use the built-in in-memory monitor
builder.AddFabrCoreServer(options =>
{
    options.UseInMemoryAgentMessageMonitor();
});

// Or use a custom implementation
builder.AddFabrCoreServer(options =>
{
    options.UseAgentMessageMonitor<SqlAgentMessageMonitor>();
});
```

When not enabled, a no-op `NullAgentMessageMonitor` is registered so grains always have a valid dependency.

## Quick Start — Using the In-Memory Monitor

### Querying Messages

Inject `IAgentMessageMonitor` and query:

```csharp
public class MyService
{
    private readonly IAgentMessageMonitor _monitor;

    public MyService(IAgentMessageMonitor monitor)
    {
        _monitor = monitor;
    }

    public async Task ShowRecentMessages()
    {
        // Get all messages (most recent first)
        var all = await _monitor.GetMessagesAsync();

        // Get messages for a specific agent
        var agentMessages = await _monitor.GetMessagesAsync(agentHandle: "user1:my-agent");

        // Get last 10 messages
        var recent = await _monitor.GetMessagesAsync(limit: 10);

        // Combine filters
        var filtered = await _monitor.GetMessagesAsync(agentHandle: "user1:my-agent", limit: 50);
    }
}
```

### Querying Events

Events that reach an agent's `OnEvent` handler are captured separately from messages. The query shape mirrors `GetMessagesAsync`:

```csharp
// All events (most recent first)
var allEvents = await _monitor.GetEventsAsync();

// Events delivered to a specific agent
var agentEvents = await _monitor.GetEventsAsync(agentHandle: "user1:my-agent");

// Last 10 events
var recentEvents = await _monitor.GetEventsAsync(limit: 10);

// Combine filters
var filtered = await _monitor.GetEventsAsync(agentHandle: "user1:my-agent", limit: 50);
```

### Output Viewer — Messages / Events / Both Filter

Because messages and events live on separate endpoints, an output viewer can expose a filter toggle by choosing which query to call and which notification to subscribe to:

```csharp
public enum MonitorViewFilter { Messages, Events, Both }

public class AgentOutputViewer : IDisposable
{
    private readonly IAgentMessageMonitor _monitor;
    private MonitorViewFilter _filter = MonitorViewFilter.Both;

    public AgentOutputViewer(IAgentMessageMonitor monitor)
    {
        _monitor = monitor;
        _monitor.OnMessageRecorded += HandleMessage;
        _monitor.OnEventRecorded += HandleEvent;
    }

    public void SetFilter(MonitorViewFilter filter) => _filter = filter;

    public async Task<IEnumerable<object>> LoadAsync(string? agentHandle = null, int? limit = null)
    {
        var items = new List<object>();

        if (_filter != MonitorViewFilter.Events)
            items.AddRange(await _monitor.GetMessagesAsync(agentHandle, limit));

        if (_filter != MonitorViewFilter.Messages)
            items.AddRange(await _monitor.GetEventsAsync(agentHandle, limit));

        // Merge into a unified timeline by timestamp
        return items.OrderByDescending(i => i is MonitoredMessage m ? m.Timestamp : ((MonitoredEvent)i).Timestamp);
    }

    private void HandleMessage(MonitoredMessage message)
    {
        if (_filter == MonitorViewFilter.Events) return;
        // push to UI
    }

    private void HandleEvent(MonitoredEvent evt)
    {
        if (_filter == MonitorViewFilter.Messages) return;
        // push to UI
    }

    public void Dispose()
    {
        _monitor.OnMessageRecorded -= HandleMessage;
        _monitor.OnEventRecorded -= HandleEvent;
    }
}
```

A viewer that only cares about messages simply never subscribes to `OnEventRecorded` (and never calls `GetEventsAsync`), and vice versa. Each buffer is bounded independently, so a chatty event stream cannot evict messages and vice versa.

### Querying Token Usage

```csharp
// Get token summary for one agent
var summary = await _monitor.GetAgentTokenSummaryAsync("user1:my-agent");
if (summary != null)
{
    Console.WriteLine($"Total input tokens: {summary.TotalInputTokens}");
    Console.WriteLine($"Total output tokens: {summary.TotalOutputTokens}");
    Console.WriteLine($"Total LLM calls: {summary.TotalLlmCalls}");
}

// Get summaries for all agents
var allSummaries = await _monitor.GetAllAgentTokenSummariesAsync();
```

### Subscribing to Real-Time Notifications

Use the `OnMessageRecorded` event to push updates to a UI or external system:

```csharp
public class MonitorDashboard : IDisposable
{
    private readonly IAgentMessageMonitor _monitor;

    public MonitorDashboard(IAgentMessageMonitor monitor)
    {
        _monitor = monitor;
        _monitor.OnMessageRecorded += HandleNewMessage;
    }

    private void HandleNewMessage(MonitoredMessage message)
    {
        // This fires on every recorded message — update UI, send to SignalR hub, etc.
        Console.WriteLine($"[{message.Direction}] {message.FromHandle} -> {message.ToHandle}: {message.MessageType}");

        if (message.LlmUsage is { } usage)
        {
            Console.WriteLine($"  LLM: {usage.LlmCalls} calls, {usage.InputTokens}in/{usage.OutputTokens}out tokens, model={usage.Model}");
        }
    }

    public void Dispose()
    {
        _monitor.OnMessageRecorded -= HandleNewMessage;
    }
}
```

### In-Memory Buffer Configuration

The default buffer holds 5000 messages. Older messages are evicted when the limit is reached (FIFO). The buffer size is set in the constructor. To customize, register your own instance:

```csharp
builder.Services.AddSingleton<IAgentMessageMonitor>(sp =>
    new InMemoryAgentMessageMonitor(
        sp.GetRequiredService<ILogger<InMemoryAgentMessageMonitor>>(),
        maxMessages: 10000));
```

### Clearing the Monitor

```csharp
await _monitor.ClearAsync(); // Clears all messages, events, and token summaries
```

## MonitoredMessage Properties

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `string` | Unique message ID |
| `AgentHandle` | `string?` | The agent/client that recorded this entry |
| `FromHandle` | `string?` | Message sender handle |
| `ToHandle` | `string?` | Message recipient handle |
| `Message` | `string?` | Message content |
| `MessageType` | `string?` | Message type identifier |
| `Kind` | `MessageKind` | `Request`, `OneWay`, or `Response` |
| `Direction` | `MessageDirection` | `Inbound` or `Outbound` |
| `Timestamp` | `DateTimeOffset` | UTC timestamp |
| `TraceId` | `string?` | Distributed trace correlation ID |
| `LlmUsage` | `LlmUsageInfo?` | LLM metrics (null if no LLM was invoked) |

## MonitoredEvent Properties

Events captured at `AgentGrain.ReceivedEventMessage` (the `OnEvent` stream handler). The `Data` and `BinaryData` payloads are **intentionally excluded** to keep the monitor buffer readable, matching the policy used for `MonitoredMessage`.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `string` | Unique monitor record id |
| `EventId` | `string?` | The originating `EventMessage.Id` |
| `AgentHandle` | `string?` | The agent handle that received the event |
| `Type` | `string?` | Event type descriptor (e.g. `"order.created"`) |
| `Source` | `string?` | Handle of the event producer |
| `Subject` | `string?` | Optional topic / subject |
| `Namespace` | `string?` | Stream namespace used for routing |
| `Channel` | `string?` | Channel within the namespace |
| `DataContentType` | `string?` | MIME type of the (excluded) payload |
| `Args` | `Dictionary<string, string>?` | Optional key-value args |
| `EventTime` | `DateTimeOffset` | Producer-stamped event time |
| `Timestamp` | `DateTimeOffset` | UTC time the monitor recorded the event |
| `TraceId` | `string?` | Distributed trace correlation id |
| `Direction` | `MessageDirection` | Always `Inbound` today |

## LlmUsageInfo Properties

| Property | Type | Description |
|----------|------|-------------|
| `InputTokens` | `long` | Input tokens consumed |
| `OutputTokens` | `long` | Output tokens generated |
| `ReasoningTokens` | `long` | Reasoning tokens (if applicable) |
| `CachedInputTokens` | `long` | Cached input tokens |
| `LlmCalls` | `long` | Number of LLM API calls |
| `LlmDurationMs` | `long` | Total LLM processing time in ms |
| `Model` | `string?` | Model identifier used |
| `FinishReason` | `string?` | LLM finish reason |

## Building a Custom Monitor Provider

Implement `IAgentMessageMonitor` and register it via `FabrCoreServerOptions`:

```csharp
public class SqlAgentMessageMonitor : IAgentMessageMonitor
{
    private readonly IDbConnection _db;

    public event Action<MonitoredMessage>? OnMessageRecorded;
    public event Action<MonitoredEvent>? OnEventRecorded;

    public SqlAgentMessageMonitor(IDbConnection db)
    {
        _db = db;
    }

    public async Task RecordMessageAsync(MonitoredMessage message)
    {
        // Persist to database
        await _db.ExecuteAsync("INSERT INTO MonitoredMessages ...", message);

        // Fire notification — always wrap in try/catch
        try { OnMessageRecorded?.Invoke(message); }
        catch { /* never let subscriber exceptions propagate */ }
    }

    public async Task<List<MonitoredMessage>> GetMessagesAsync(string? agentHandle = null, int? limit = null)
    {
        var sql = "SELECT * FROM MonitoredMessages";
        if (agentHandle != null) sql += " WHERE AgentHandle = @agentHandle";
        sql += " ORDER BY Timestamp DESC";
        if (limit.HasValue) sql += " LIMIT @limit";

        return (await _db.QueryAsync<MonitoredMessage>(sql, new { agentHandle, limit })).ToList();
    }

    public async Task<AgentTokenSummary?> GetAgentTokenSummaryAsync(string agentHandle)
    {
        return await _db.QuerySingleOrDefaultAsync<AgentTokenSummary>(
            "SELECT * FROM AgentTokenSummaries WHERE AgentHandle = @agentHandle",
            new { agentHandle });
    }

    public async Task<List<AgentTokenSummary>> GetAllAgentTokenSummariesAsync()
    {
        return (await _db.QueryAsync<AgentTokenSummary>("SELECT * FROM AgentTokenSummaries")).ToList();
    }

    public async Task RecordEventAsync(MonitoredEvent evt)
    {
        await _db.ExecuteAsync("INSERT INTO MonitoredEvents ...", evt);
        try { OnEventRecorded?.Invoke(evt); }
        catch { /* never let subscriber exceptions propagate */ }
    }

    public async Task<List<MonitoredEvent>> GetEventsAsync(string? agentHandle = null, int? limit = null)
    {
        var sql = "SELECT * FROM MonitoredEvents";
        if (agentHandle != null) sql += " WHERE AgentHandle = @agentHandle";
        sql += " ORDER BY Timestamp DESC";
        if (limit.HasValue) sql += " LIMIT @limit";

        return (await _db.QueryAsync<MonitoredEvent>(sql, new { agentHandle, limit })).ToList();
    }

    public async Task ClearAsync()
    {
        await _db.ExecuteAsync("DELETE FROM MonitoredMessages");
        await _db.ExecuteAsync("DELETE FROM MonitoredEvents");
        await _db.ExecuteAsync("DELETE FROM AgentTokenSummaries");
    }
}
```

### Registering a Custom Provider

```csharp
builder.AddFabrCoreServer(options =>
{
    options.UseAgentMessageMonitor<SqlAgentMessageMonitor>();
});
```

## What Gets Captured

| Source | Record Type | Direction | LLM Usage | Notes |
|--------|-------------|-----------|-----------|-------|
| Agent receives a message (via `AgentGrain.OnMessage`) | `MonitoredMessage` | Inbound | No | Every request hitting an agent |
| Agent sends a response (via `AgentGrain.OnMessage`) | `MonitoredMessage` | Outbound | Yes | Response after LLM processing |
| Client receives a stream message | `MonitoredMessage` | Inbound | If present | Responses flowing back to clients |
| Plugin sends via `IFabrCoreAgentHost` | `MonitoredMessage` | — | — | Captured at the receiving agent's `OnMessage` |
| Agent-to-agent via `SendAndReceiveMessage` | `MonitoredMessage` | — | — | Captured at the target agent's `OnMessage` |
| Agent receives an event (via `AgentGrain.ReceivedEventMessage` → `OnEvent`) | `MonitoredEvent` | Inbound | No | Captured inbound-only; events do not flow through `ClientGrain` |

System messages (`_status` heartbeats, `_error` messages) are captured like any other message. Filter by `MessageType` if needed:

```csharp
var chatMessages = messages.Where(m => !SystemMessageTypes.IsSystemMessage(m.MessageType));
```
