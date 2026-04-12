---
name: fabrcore-agentmonitor
description: >
  FabrCore agent message, event, and LLM call monitoring — IAgentMessageMonitor, InMemoryAgentMessageMonitor,
  message traffic observation, event stream observation at OnEvent, internal LLM request/response capture,
  LLM token tracking, building custom monitor providers, subscribing to message/event/LLM-call notifications for UI updates.
  Triggers on: "agent monitor", "message monitor", "IAgentMessageMonitor", "InMemoryAgentMessageMonitor",
  "monitor messages", "monitor events", "OnEvent", "event stream monitor", "track tokens", "agent token usage",
  "message traffic", "monitor provider", "OnMessageRecorded", "OnEventRecorded", "OnLlmCallRecorded",
  "MonitoredMessage", "MonitoredEvent", "MonitoredLlmCall", "LlmCaptureOptions", "LlmCallContext",
  "monitor LLM calls", "capture LLM prompts", "capture LLM responses", "AgentTokenSummary", "message observation".
  Do NOT use for: agent lifecycle — use fabrcore-agent.
  Do NOT use for: OpenTelemetry metrics — use fabrcore-server.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Agent Message Monitor

## Overview

The Agent Message Monitor provides a pluggable provider for observing message, event, and internal LLM call traffic flowing through the FabrCore agent system. It captures:

- Every message an agent receives (inbound requests)
- Every response an agent sends (outbound responses with LLM usage)
- Messages arriving at clients via streams
- Every event that reaches an agent's `OnEvent` handler (inbound, fire-and-forget)
- **Every internal LLM request/response call** an agent makes through Microsoft Agent Framework (`IChatClient.GetResponseAsync` / streaming), whether from `OnMessage`, `OnEvent`, timers, reminders, compaction, or background work
- LLM token usage per response (input, output, reasoning, cached tokens, model, duration)
- Accumulated token totals per agent

Messages, events, and LLM calls live in **three separate buffers** with **separate query methods and notification events**, so a client output viewer can toggle any combination — messages only, events only, LLM calls only, or any merge — with independent FIFO eviction so a chatty stream in one track cannot evict entries from another.

> **See also:** [references/llm-call-capture.md](references/llm-call-capture.md) for a deep dive on the LLM call track — attribution rules, payload redaction, streaming aggregation, and the `LlmCallContext` / `LlmUsageScope` precedence model.

## Architecture

| Component | Location | Purpose |
|-----------|----------|---------|
| `IAgentMessageMonitor` | `FabrCore.Core.Monitoring` | Provider interface |
| `InMemoryAgentMessageMonitor` | `FabrCore.Host.Services` | Default bounded FIFO buffer implementation (messages, events, LLM calls) |
| `MonitoredMessage` | `FabrCore.Core.Monitoring` | Captured message snapshot |
| `MonitoredEvent` | `FabrCore.Core.Monitoring` | Captured event snapshot (from `OnEvent`) |
| `MonitoredLlmCall` | `FabrCore.Core.Monitoring` | Captured snapshot of a single LLM request/response pair |
| `LlmMessageSnapshot` | `FabrCore.Core.Monitoring` | Per-chat-message payload snapshot inside a `MonitoredLlmCall` |
| `LlmToolCallSnapshot` | `FabrCore.Core.Monitoring` | Per-tool-call snapshot inside a `MonitoredLlmCall` |
| `LlmCaptureOptions` | `FabrCore.Core.Monitoring` | Capture configuration (enabled / payloads / size caps / redaction / buffer size) |
| `LlmUsageInfo` | `FabrCore.Core.Monitoring` | Aggregated LLM usage metrics stamped on a response `MonitoredMessage` |
| `AgentTokenSummary` | `FabrCore.Core.Monitoring` | Accumulated token totals per agent |
| `MessageDirection` | `FabrCore.Core.Monitoring` | `Inbound` / `Outbound` enum |
| `LlmUsageScope` | `FabrCore.Sdk` | AsyncLocal scope set by `OnMessage` carrying agent handle, parent message id, trace id, and origin |
| `LlmCallContext` | `FabrCore.Sdk` | AsyncLocal context used to tag LLM calls that happen outside an `OnMessage` scope (timers, `OnEvent`, background) |
| `TokenTrackingChatClient` | `FabrCore.Sdk` | `DelegatingChatClient` that records each LLM call to the monitor and accumulates usage into the active scope |

## Enabling Monitoring

Monitoring is **opt-in**. Enable it in `AddFabrCoreServer`:

```csharp
// Use the built-in in-memory monitor — metadata-only LLM capture by default.
builder.AddFabrCoreServer(options =>
{
    options.UseInMemoryAgentMessageMonitor();
});

// Enable full LLM payload capture (prompts, responses, tool args) with redaction and size caps.
builder.AddFabrCoreServer(options =>
{
    options.UseInMemoryAgentMessageMonitor(capture =>
    {
        capture.CapturePayloads = true;           // capture prompts/responses/tool args
        capture.MaxPayloadChars = 4_000;          // per-field char cap
        capture.MaxToolArgsChars = 2_000;         // separate cap for tool args
        capture.MaxBufferedCalls = 1_000;         // lower than the default 2000 since payloads are larger
        capture.Redact = s =>
            System.Text.RegularExpressions.Regex.Replace(s, "sk-[A-Za-z0-9]+", "***");
    });
});

// Or use a custom implementation
builder.AddFabrCoreServer(options =>
{
    options.UseAgentMessageMonitor<SqlAgentMessageMonitor>();
});
```

When not enabled, a no-op `NullAgentMessageMonitor` is registered so grains always have a valid dependency. Its `LlmCaptureOptions.Enabled` is `false`, so `TokenTrackingChatClient` short-circuits the capture path with zero allocation cost.

> **Security note:** When `CapturePayloads = true` the monitor stores the actual prompts and responses sent to the LLM. These can contain PII, secrets, or customer data. Always configure `Redact` and verify your downstream storage (memory buffer, database, log sink) is trusted. The default is metadata-only for this reason.

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

### Querying LLM Calls

Individual LLM request/response calls are stored in their own buffer with their own query shape — mirroring `GetMessagesAsync` / `GetEventsAsync`:

```csharp
// All LLM calls (most recent first)
var allCalls = await _monitor.GetLlmCallsAsync();

// LLM calls made by a specific agent
var agentCalls = await _monitor.GetLlmCallsAsync(agentHandle: "user1:my-agent");

// Last 20 LLM calls
var recentCalls = await _monitor.GetLlmCallsAsync(limit: 20);

foreach (var call in recentCalls)
{
    Console.WriteLine($"[{call.Timestamp:HH:mm:ss}] {call.AgentHandle} {call.OriginContext} " +
                      $"model={call.Model} tokens={call.InputTokens}in/{call.OutputTokens}out " +
                      $"dur={call.DurationMs}ms streaming={call.Streaming}");

    // Payloads are null unless CapturePayloads == true
    if (call.RequestMessages is not null)
    {
        foreach (var m in call.RequestMessages)
            Console.WriteLine($"  [{m.Role}] {m.Text}{(m.Truncated ? " …(truncated)" : "")}");
    }
}
```

**`OriginContext`** distinguishes where the call came from:
- `OnMessage:<message-id>` — inside a normal `OnMessage` flow (also sets `ParentMessageId`)
- `OnEvent:<type>` — inside an `OnEvent` handler
- `Timer:<name>` / `Reminder:<name>` — inside a timer/reminder tick
- `Compaction` — inside `CompactionService` while summarizing chat history
- `Background` — outside any scope/context (e.g. raw `Task.Run` without wrapping in `LlmCallContext`)

### Output Viewer — Messages / Events / LLM Calls Filter

Because messages, events, and LLM calls live on separate endpoints, an output viewer can expose independent toggles by choosing which queries to call and which notifications to subscribe to:

```csharp
[Flags]
public enum MonitorViewFilter
{
    Messages = 1,
    Events   = 2,
    LlmCalls = 4,
    All      = Messages | Events | LlmCalls
}

public class AgentOutputViewer : IDisposable
{
    private readonly IAgentMessageMonitor _monitor;
    private MonitorViewFilter _filter = MonitorViewFilter.All;

    public AgentOutputViewer(IAgentMessageMonitor monitor)
    {
        _monitor = monitor;
        _monitor.OnMessageRecorded += HandleMessage;
        _monitor.OnEventRecorded   += HandleEvent;
        _monitor.OnLlmCallRecorded += HandleLlmCall;
    }

    public void SetFilter(MonitorViewFilter filter) => _filter = filter;

    public async Task<IEnumerable<object>> LoadAsync(string? agentHandle = null, int? limit = null)
    {
        var items = new List<object>();

        if (_filter.HasFlag(MonitorViewFilter.Messages))
            items.AddRange(await _monitor.GetMessagesAsync(agentHandle, limit));

        if (_filter.HasFlag(MonitorViewFilter.Events))
            items.AddRange(await _monitor.GetEventsAsync(agentHandle, limit));

        if (_filter.HasFlag(MonitorViewFilter.LlmCalls))
            items.AddRange(await _monitor.GetLlmCallsAsync(agentHandle, limit));

        // Merge into a unified timeline by timestamp
        return items.OrderByDescending(i => i switch
        {
            MonitoredMessage m => m.Timestamp,
            MonitoredEvent e   => e.Timestamp,
            MonitoredLlmCall c => c.Timestamp,
            _                  => DateTimeOffset.MinValue
        });
    }

    private void HandleMessage(MonitoredMessage message)
    {
        if (!_filter.HasFlag(MonitorViewFilter.Messages)) return;
        // push to UI
    }

    private void HandleEvent(MonitoredEvent evt)
    {
        if (!_filter.HasFlag(MonitorViewFilter.Events)) return;
        // push to UI
    }

    private void HandleLlmCall(MonitoredLlmCall call)
    {
        if (!_filter.HasFlag(MonitorViewFilter.LlmCalls)) return;
        // push to UI — correlate to a parent message via call.ParentMessageId when non-null
    }

    public void Dispose()
    {
        _monitor.OnMessageRecorded -= HandleMessage;
        _monitor.OnEventRecorded   -= HandleEvent;
        _monitor.OnLlmCallRecorded -= HandleLlmCall;
    }
}
```

A viewer that only cares about messages simply never subscribes to the other notifications (and never calls the other queries), and vice versa. Each buffer is bounded independently, so a chatty event stream cannot evict messages, a burst of LLM calls cannot evict events, and so on.

### Subscribing to LLM Call Notifications

```csharp
_monitor.OnLlmCallRecorded += call =>
{
    Console.WriteLine($"LLM call: {call.AgentHandle} {call.OriginContext} " +
                      $"{call.Model} {call.InputTokens}in/{call.OutputTokens}out " +
                      $"{call.DurationMs}ms");

    if (call.ErrorMessage is not null)
        Console.Error.WriteLine($"  LLM error: {call.ErrorMessage}");
};
```

Notifications fire **after** the LLM call completes (or errors) on the fire-and-forget record path, so a slow subscriber never blocks an agent's response.

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

The default message/event buffer holds 5000 entries; the LLM call buffer defaults to 2000 entries (lower because payloads can be larger). Older entries are evicted when each limit is reached (FIFO). To customize, register your own instance:

```csharp
builder.Services.AddSingleton<IAgentMessageMonitor>(sp =>
    new InMemoryAgentMessageMonitor(
        sp.GetRequiredService<ILogger<InMemoryAgentMessageMonitor>>(),
        llmCaptureOptions: new LlmCaptureOptions
        {
            Enabled = true,
            CapturePayloads = true,
            MaxPayloadChars = 4_000,
            MaxBufferedCalls = 5_000
        },
        maxMessages: 10_000));
```

Or, when using `UseInMemoryAgentMessageMonitor(configure)`, the same `LlmCaptureOptions` instance is registered as a DI singleton and flows through to the monitor and `TokenTrackingChatClient` automatically.

### Clearing the Monitor

```csharp
await _monitor.ClearAsync(); // Clears messages, events, LLM calls, and token summaries
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
| `BusyRouted` | `bool` | True if this message was routed through `OnMessageBusy` because the agent was already processing |

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

## MonitoredLlmCall Properties

A `MonitoredLlmCall` is recorded for every LLM round-trip made through `TokenTrackingChatClient` — the wrapper that `FabrCoreAgentProxy.GetChatClient` places around every chat client an agent uses.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `string` | Unique LLM call id |
| `Timestamp` | `DateTimeOffset` | UTC time the monitor recorded the call |
| `AgentHandle` | `string?` | Agent that made the call |
| `TraceId` | `string?` | Distributed trace id inherited from the parent scope |
| `ParentMessageId` | `string?` | `MonitoredMessage.Id` that triggered the call (null for timer/event/background) |
| `OriginContext` | `string` | `OnMessage:<id>`, `OnEvent:<type>`, `Timer:<name>`, `Reminder:<name>`, `Compaction`, `Background` |
| `Model` | `string?` | Model identifier returned by the provider |
| `DurationMs` | `long` | Wall-clock duration of the LLM round-trip |
| `Streaming` | `bool` | True if captured from `GetStreamingResponseAsync` |
| `FinishReason` | `string?` | LLM finish reason |
| `InputTokens` | `long` | Input tokens consumed by this call |
| `OutputTokens` | `long` | Output tokens generated by this call |
| `ReasoningTokens` | `long` | Reasoning tokens (if applicable) |
| `CachedInputTokens` | `long` | Cached input tokens |
| `ErrorMessage` | `string?` | Populated if the call threw (partial streams are still recorded) |
| `RequestMessages` | `List<LlmMessageSnapshot>?` | Request chat messages (null unless `CapturePayloads == true`) |
| `ResponseMessages` | `List<LlmMessageSnapshot>?` | Response chat messages (null unless `CapturePayloads == true`) |
| `ResponseText` | `string?` | Concatenated response text (null unless `CapturePayloads == true`) |
| `ToolCalls` | `List<LlmToolCallSnapshot>?` | Function/tool calls emitted by the response (null unless `CapturePayloads == true`) |

### LlmMessageSnapshot

| Property | Type | Description |
|----------|------|-------------|
| `Role` | `string` | `system`, `user`, `assistant`, `tool`, etc. |
| `Text` | `string?` | Concatenated text content, redacted and size-capped |
| `ContentCount` | `int` | Number of original content parts (text / image / tool / ...) |
| `Truncated` | `bool` | True when the cap was hit |

### LlmToolCallSnapshot

| Property | Type | Description |
|----------|------|-------------|
| `CallId` | `string?` | Tool call id from the provider |
| `Name` | `string` | Function/tool name |
| `Arguments` | `string?` | Serialized JSON arguments, redacted and size-capped |
| `Truncated` | `bool` | True when the cap was hit |

## LlmCaptureOptions

| Property | Default | Description |
|----------|---------|-------------|
| `Enabled` | `true` | Master switch for the LLM call track. When false, no LLM calls are recorded. |
| `CapturePayloads` | `false` | When true, captures prompts, responses, and tool args. Off by default for privacy/memory reasons. |
| `MaxPayloadChars` | `8000` | Per-field character cap on captured message text |
| `MaxToolArgsChars` | `4000` | Character cap on captured tool call arguments |
| `Redact` | `null` | Optional `Func<string,string>` applied to every captured string (use for secret/PII scrubbing) |
| `MaxBufferedCalls` | `2000` | FIFO cap on the in-memory LLM call buffer |

## Attribution: How LLM Calls Are Tagged

`TokenTrackingChatClient` resolves the agent handle and origin for each recorded call using a three-tier fallback:

1. **`LlmUsageScope`** (set by `FabrCoreAgentProxy.InternalOnMessage`) — provides handle, `ParentMessageId`, `TraceId`, and origin `OnMessage:<id>`. All calls made inside a normal `OnMessage` flow land here.
2. **`LlmCallContext`** (AsyncLocal, set by the harness for non-OnMessage paths) — overrides the origin tag for nested/background work. `AgentGrain` automatically wraps `OnEvent` dispatch as `OnEvent:<type>` and timer/reminder dispatch as `Timer:<name>` / `Reminder:<name>`. `CompactionService` wraps itself as `Compaction`.
3. **Constructor-captured handle** (passed to `TokenTrackingChatClient` by `FabrCoreAgentProxy.GetChatClient`) — guarantees every call has a correct `AgentHandle` even when neither of the above is set. Tagged `Background`.

### Custom Background LLM Calls

For agent-authored background work that isn't already wrapped by the harness (e.g. a custom `Task.Run`, an `IHostedService`, or a direct callback from an external library), wrap the LLM-calling code yourself so the capture is attributed:

```csharp
public class ReportingAgent : FabrCoreAgentProxy
{
    private async Task GenerateDailyReport()
    {
        // No OnMessage scope here — this was triggered from a background worker.
        using (LlmCallContext.Begin(
            agentHandle: fabrcoreAgentHost.GetHandle(),
            originContext: "Background:DailyReport"))
        {
            var chatClient = await GetChatClient("OpenAIProd");
            var response = await chatClient.GetResponseAsync(new[]
            {
                new ChatMessage(ChatRole.System, "Summarize yesterday's activity."),
            });
            // captured MonitoredLlmCall will carry OriginContext = "Background:DailyReport"
        }
    }
}
```

> See [references/llm-call-capture.md](references/llm-call-capture.md) for the full precedence rules, streaming aggregation details, and error-path behavior.

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
    public event Action<MonitoredLlmCall>? OnLlmCallRecorded;

    public LlmCaptureOptions LlmCaptureOptions { get; }

    public SqlAgentMessageMonitor(IDbConnection db, LlmCaptureOptions? llmCaptureOptions = null)
    {
        _db = db;
        LlmCaptureOptions = llmCaptureOptions ?? new LlmCaptureOptions();
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

    public async Task RecordLlmCallAsync(MonitoredLlmCall call)
    {
        if (!LlmCaptureOptions.Enabled) return;

        // Serialize payloads to JSON columns for storage.
        await _db.ExecuteAsync("INSERT INTO MonitoredLlmCalls ...", call);
        try { OnLlmCallRecorded?.Invoke(call); }
        catch { /* never let subscriber exceptions propagate */ }
    }

    public async Task<List<MonitoredLlmCall>> GetLlmCallsAsync(string? agentHandle = null, int? limit = null)
    {
        var sql = "SELECT * FROM MonitoredLlmCalls";
        if (agentHandle != null) sql += " WHERE AgentHandle = @agentHandle";
        sql += " ORDER BY Timestamp DESC";
        if (limit.HasValue) sql += " LIMIT @limit";

        return (await _db.QueryAsync<MonitoredLlmCall>(sql, new { agentHandle, limit })).ToList();
    }

    public async Task ClearAsync()
    {
        await _db.ExecuteAsync("DELETE FROM MonitoredMessages");
        await _db.ExecuteAsync("DELETE FROM MonitoredEvents");
        await _db.ExecuteAsync("DELETE FROM MonitoredLlmCalls");
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
| Agent receives a message while busy (via `OnMessageBusy`) | `MonitoredMessage` | Inbound | No | `BusyRouted = true` — no heartbeat, compaction, or flush |
| Agent sends a busy response (via `OnMessageBusy`) | `MonitoredMessage` | Outbound | No | `BusyRouted = true` |
| Client receives a stream message | `MonitoredMessage` | Inbound | If present | Responses flowing back to clients |
| Plugin sends via `IFabrCoreAgentHost` | `MonitoredMessage` | — | — | Captured at the receiving agent's `OnMessage` |
| Agent-to-agent via `SendAndReceiveMessage` | `MonitoredMessage` | — | — | Captured at the target agent's `OnMessage` |
| Agent receives an event (via `AgentGrain.ReceivedEventMessage` → `OnEvent`) | `MonitoredEvent` | Inbound | No | Captured inbound-only; events do not flow through `ClientGrain` |
| Agent `IChatClient.GetResponseAsync` call (from `OnMessage`) | `MonitoredLlmCall` | — | Yes | `OriginContext = OnMessage:<id>`; `ParentMessageId` correlates to the outbound `MonitoredMessage` |
| Agent `IChatClient` call from `OnEvent` | `MonitoredLlmCall` | — | Yes | `OriginContext = OnEvent:<type>`; no `ParentMessageId` |
| Agent `IChatClient` call from a timer / reminder tick | `MonitoredLlmCall` | — | Yes | `OriginContext = Timer:<name>` or `Reminder:<name>` (the harness also sends a synthetic `AgentMessage` via `OnMessage`, so calls land inside that scope) |
| Streaming LLM call (`GetStreamingResponseAsync`) | `MonitoredLlmCall` | — | Yes | Aggregated at stream completion; `Streaming = true` |
| `CompactionService` summarization LLM call | `MonitoredLlmCall` | — | Yes | `OriginContext = Compaction`; inherits parent `AgentHandle` / `TraceId` from the surrounding `OnMessage` scope when present |
| Background LLM call wrapped in `LlmCallContext.Begin(...)` | `MonitoredLlmCall` | — | Yes | `OriginContext` is whatever the caller supplied; `AgentHandle` comes from the context or the chat client constructor fallback |

System messages (`_status` heartbeats, `_error` messages) are captured like any other message. Filter by `MessageType` if needed:

```csharp
var chatMessages = messages.Where(m => !SystemMessageTypes.IsSystemMessage(m.MessageType));

// Filter to only busy-routed messages (useful for monitoring concurrency)
var busyMessages = messages.Where(m => m.BusyRouted);
```
