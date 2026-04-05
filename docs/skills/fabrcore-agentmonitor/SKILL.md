---
name: fabrcore-agentmonitor
description: >
  FabrCore agent message monitoring — IAgentMessageMonitor, InMemoryAgentMessageMonitor, message traffic observation,
  LLM token tracking, building custom monitor providers, subscribing to message events for UI updates.
  Triggers on: "agent monitor", "message monitor", "IAgentMessageMonitor", "InMemoryAgentMessageMonitor",
  "monitor messages", "track tokens", "agent token usage", "message traffic", "monitor provider",
  "OnMessageRecorded", "MonitoredMessage", "AgentTokenSummary", "message observation".
  Do NOT use for: agent lifecycle — use fabrcore-agent.
  Do NOT use for: OpenTelemetry metrics — use fabrcore-server.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Agent Message Monitor

## Overview

The Agent Message Monitor provides a pluggable provider for observing all message traffic flowing through the FabrCore agent system. It captures:

- Every message an agent receives (inbound requests)
- Every response an agent sends (outbound responses with LLM usage)
- Messages arriving at clients via streams
- LLM token usage per response (input, output, reasoning, cached tokens, model, duration)
- Accumulated token totals per agent

## Architecture

| Component | Location | Purpose |
|-----------|----------|---------|
| `IAgentMessageMonitor` | `FabrCore.Core.Monitoring` | Provider interface |
| `InMemoryAgentMessageMonitor` | `FabrCore.Host.Services` | Default bounded FIFO buffer implementation |
| `MonitoredMessage` | `FabrCore.Core.Monitoring` | Captured message snapshot |
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
await _monitor.ClearAsync(); // Clears all messages and token summaries
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

    public async Task ClearAsync()
    {
        await _db.ExecuteAsync("DELETE FROM MonitoredMessages");
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

| Source | Direction | LLM Usage | Notes |
|--------|-----------|-----------|-------|
| Agent receives a message (via `AgentGrain.OnMessage`) | Inbound | No | Every request hitting an agent |
| Agent sends a response (via `AgentGrain.OnMessage`) | Outbound | Yes | Response after LLM processing |
| Client receives a stream message | Inbound | If present | Responses flowing back to clients |
| Plugin sends via `IFabrCoreAgentHost` | — | — | Captured at the receiving agent's `OnMessage` |
| Agent-to-agent via `SendAndReceiveMessage` | — | — | Captured at the target agent's `OnMessage` |

System messages (`_status` heartbeats, `_error` messages) are captured like any other message. Filter by `MessageType` if needed:

```csharp
var chatMessages = messages.Where(m => !SystemMessageTypes.IsSystemMessage(m.MessageType));
```
