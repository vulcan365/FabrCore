---
name: fabrcore-agent
description: >
  Build FabrCoreAgentProxy agents and implement lifecycle, state, compaction, timers, reminders,
  health, telemetry, storage, and configuration. Covers the two-layer compaction ladder: context
  compaction, history compaction, ContextCompaction, ContextCompactionEnabled, CompactionLadder,
  the projection fuse, and run-safety budgets. Use for FabrCoreAgentProxy, AgentAlias,
  OnInitialize, OnMessage, OnMessageBusy, OnEvent, OnCompaction, CreateChatClientAgent,
  SetStatusMessage, SendToUserAsync, proactive/out-of-turn notifications, AgentConfiguration,
  GetStateAsync, TryGetStateAsync, FlushStateAsync, RegisterTimer, RegisterReminder,
  SystemMessageTypes, and verifiable execution. Use fabrcore-agentframework for AIAgent or
  AgentSession internals; fabrcore-harness for todo lists, iteration loops, or background
  delegation (CreateFabrCoreHarnessAgent); fabrcore-plugins-tools/fabrcore-mcp for tools or MCP;
  and fabrcore-principal-delivery for durable outbox internals and relay-provider authoring.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Agent Development

Build agents by extending `FabrCoreAgentProxy` — the base class that connects your business logic to Orleans grains, LLM clients, tools, and inter-agent messaging.

## Agent Structure

Every agent extends `FabrCoreAgentProxy` and is decorated with `[AgentAlias]`:

```csharp
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

[AgentAlias("my-agent")]
[Description("Customer support agent for order inquiries and returns")]
[FabrCoreCapabilities("Handles customer inquiries — lookup orders, check status, process returns.")]
[FabrCoreNote("Requires an order ID in context before most tools will work.")]
[FabrCoreNote("Do not use for payment processing — use the billing-agent instead.")]
public class MyAgent : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public MyAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost) { }

    public override async Task OnInitialize() { /* ... */ }
    public override async Task<AgentMessage> OnMessage(AgentMessage message) { /* ... */ }
    public override Task OnEvent(EventMessage eventMessage) { /* ... */ }
}
```

### Constructor Pattern

The constructor always takes exactly three parameters — do not add additional constructor parameters. Use `IServiceProvider` to resolve any services you need in `OnInitialize()`.

### Naming Convention

- **Agent alias:** `kebab-case` (e.g., `"my-agent"`) — used in `[AgentAlias]` and `AgentConfiguration.AgentType`
- **Class name:** `PascalCase` (e.g., `MyAgent`)

### Registry Metadata Attributes

Decorate agents with `[FabrCoreCapabilities]` and `[FabrCoreNote]` so the discovery registry exposes what the agent does. This metadata is returned by the `/fabrcoreapi/discovery` endpoint and is used by users and other agents to decide whether to interact with this agent.

| Attribute | Multiplicity | Purpose |
|-----------|-------------|---------|
| `[Description("...")]` | One per class | Short summary of the agent (from `System.ComponentModel`) |
| `[FabrCoreCapabilities("...")]` | One per class | Describes what the agent can do — its core responsibilities and features |
| `[FabrCoreNote("...")]` | Multiple allowed | Usage instructions, prerequisites, or when *not* to use this agent |
| `[FabrCoreHidden]` | One per class | Hides the agent from the discovery endpoint (still usable, just not listed) |

```csharp
[AgentAlias("job-agent")]
[Description("Manufacturing job management agent")]
[FabrCoreCapabilities("Manages manufacturing jobs — lookup, status tracking, priority changes, and ship date queries.")]
[FabrCoreNote("Requires a job number in the user's context before most tools will work.")]
[FabrCoreNote("Do not use for quoting or estimating — use the quotes-agent instead.")]
public class JobAgent : FabrCoreAgentProxy { /* ... */ }
```

These attributes are optional but strongly recommended for any agent that will be discoverable by other agents or surfaced in a registry UI.

## Protected Fields

Available from the base class:

```csharp
protected readonly AgentConfiguration config;
protected readonly IFabrCoreAgentHost fabrcoreAgentHost;  // NOTE: "fabrcoreAgentHost" not "fabrAgentHost"
protected readonly IServiceProvider serviceProvider;
protected readonly ILoggerFactory loggerFactory;
protected readonly ILogger<FabrCoreAgentProxy> logger;
protected readonly IConfiguration configuration;
protected readonly IFabrCoreChatClientService chatClientService;
```

**CRITICAL:** The field is `fabrcoreAgentHost` (with "fabrcore" prefix), NOT `fabrAgentHost`.

## Handle Methods

`IFabrCoreAgentHost` provides methods to access the agent's handle and its components:

Compatibility naming: `GetUserHandle()`, `HasUserHandle()`, and the `UserHandle` tuple field are legacy API names. Their value is the principal handle that scopes routing, storage, diagnostics, and ACL checks.

```csharp
// Full handle (e.g., "principal123:assistant")
var full = fabrcoreAgentHost.GetHandle();

// Principal handle portion (e.g., "principal123") — empty string if no principal handle
var principalHandle = fabrcoreAgentHost.GetUserHandle();

// Agent handle portion without principal handle prefix (e.g., "assistant")
var agent = fabrcoreAgentHost.GetAgentHandle();

// Decompose into both parts at once
var (principalHandle, agentHandle) = fabrcoreAgentHost.GetParsedHandle();

// Check if this agent has a principal handle (legacy method name)
if (fabrcoreAgentHost.HasUserHandle())
{
    // Principal-handle-scoped logic
}
```

| Method | Returns | Example (`"principal123:assistant"`) | Example (`"assistant"`) |
|--------|---------|-------------------------------|------------------------|
| `GetHandle()` | Full handle string | `"principal123:assistant"` | `"assistant"` |
| `GetUserHandle()` | Principal handle portion (legacy API name) | `"principal123"` | `""` |
| `GetAgentHandle()` | Agent handle portion | `"assistant"` | `"assistant"` |
| `GetParsedHandle()` | `(UserHandle, AgentHandle)` tuple; `UserHandle` means principal handle | `("principal123", "assistant")` | `("", "assistant")` |
| `HasUserHandle()` | `bool`; true when a principal prefix exists | `true` | `false` |

These methods are available in both agents and plugins (via `IFabrCoreAgentHost`).

## Lifecycle Methods

| Method | When It Runs | Purpose |
|--------|-------------|---------|
| Constructor | Grain activation | DI wiring only — no async work |
| `OnInitialize()` | Before first message or on reconfigure | Set up LLM client, tools, threads |
| `OnMessage(AgentMessage)` | Request/OneWay message received | Process messages, return response |
| `OnMessageBusy(AgentMessage)` | Message received while `OnMessage` is already running | Handle concurrent messages (default: returns "busy" response) |
| `OnEvent(EventMessage)` | Fire-and-forget event | Handle stream event notifications |
| `OnCompaction(...)` | After OnMessage, when threshold exceeded | Custom compaction logic |
| `GetHealth(HealthDetailLevel)` | Health check request | Return custom health metrics |
| Agent eviction | Host-managed, not an agent override | Permanently clears runtime callbacks, persisted state, stream subscriptions, registry entries, and deactivates |

### Reset vs Eviction

`ResetAgent` is a soft lifecycle operation: it calls the agent reset hook, clears chat/custom state, and reconfigures the same agent.

Eviction is a hard delete initiated through the Host API: `DELETE /fabrcoreapi/Agent/{handle}` with `x-user-handle` (legacy header name for the principal handle). It is handled by `AgentGrain`, not agent code. Eviction unregisters timers and reminders, removes stream subscriptions, clears persisted Orleans state, removes diagnostics/principal tracking entries, and deactivates the grain. If the agent is actively processing a message, the API returns `409 Conflict` and the caller should retry later.

### OnInitialize()

Called once before the first message is processed, or when the agent is reconfigured.

```csharp
public override async Task OnInitialize()
{
    // Step 1: Resolve tools from configured plugins, standalone tools, and MCP servers
    var tools = await ResolveConfiguredToolsAsync();

    // Step 2: Add local tool methods defined in this class
    tools.Add(AIFunctionFactory.Create(MyLocalTool));

    // Step 3: Create the chat client agent with tools
    var result = await CreateChatClientAgent(
        chatClientConfigName: config.Models ?? "default",  // model config from fabrcore.json
        threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),  // thread ID for history
        tools: tools);

    _agent = result.Agent;
    _session = result.Session;
}
```

**`ResolveConfiguredToolsAsync()`** must be called before `CreateChatClientAgent` — tools are NOT auto-resolved. It:
- Resolves plugins from `config.Plugins` via `[PluginAlias]` (calls `InitializeAsync` on each)
- Resolves standalone tools from `config.Tools` via `[ToolAlias]`
- Connects MCP servers from `config.McpServers` and discovers their tools
- Returns all tools as `List<AITool>`

**`CreateChatClientAgent`** signature:
```csharp
protected Task<ChatClientAgentResult> CreateChatClientAgent(
    string chatClientConfigName,          // Required: model name from fabrcore.json
    string threadId,                      // Required: ID for chat history persistence
    IList<AITool>? tools = null,
    Action<ChatClientAgentOptions>? configureOptions = null)
```

**`ChatClientAgentResult`** contains:
- `Agent` (`AIAgent`) — The configured agent instance
- `Session` (`AgentSession`) — The conversation session
- `ChatHistoryProvider` (`FabrCoreChatHistoryProvider?`) — For compaction support

**`CreateFabrCoreHarnessAgent`** is the drop-in sibling for agents that need to work a multi-step plan rather than answer one message. Same call shape, but the model gets a todo list, an iteration loop re-invokes it until that list is clear, and it can delegate to other FabrCore agents — with the whole session persisted across grain deactivation. See **fabrcore-harness**.

### OnMessage(AgentMessage)

Called for every `Request` or `OneWay` message. Must return an `AgentMessage` response.

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var response = message.Response();
    var chatMessage = new ChatMessage(ChatRole.User, message.Message);

    // Streaming response (recommended)
    await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
    {
        response.Message += update.Text;
    }

    return response;
}
```

**LLM Usage Tracking:** Token counts are automatically captured and attached to the response `Args` (e.g., `_tokens_input`, `_tokens_output`, `_llm_calls`). Clients can read these underscore-prefixed keys directly from `AgentMessage.Args`; Agent Monitor is not required for response-level usage.

### SendToUserAsync (out-of-turn principal delivery)

Use the protected helper when an agent needs to notify its owning principal outside an active
user turn, such as after a reminder, timer, workflow completion, or background job:

```csharp
await SendToUserAsync("Your report is ready");

await SendToUserAsync(
    "Your verification code is 482901",
    messageType: "verification.ready",
    target: new PrincipalDeliveryTarget("sms", "verified-phone-1"));

await SendToUserAsync(new AgentMessage
{
    MessageType = "report.ready",
    Message = "Quarterly report ready",
    DataType = "application/vnd.microsoft.card.adaptive",
    Data = cardJsonBytes
});
```

The agent must have a principal-qualified handle. The helper targets that owning principal and
sends a one-way `AgentMessage`; agents remain independent of M365, SMS, email, push, or webhook
SDKs. If the principal has a live observer, a newly arriving message follows the observer path.
Otherwise the host retains it for a supported relay; with no eligible relay it remains pending
until an endpoint becomes available or the message expires.

Use **fabrcore-principal-delivery** for explicit routing rules, durable outbox behavior, host
configuration, provider contracts, and the M365 reference provider.

### SetStatusMessage(string? message)

Controls the text sent in `_status` heartbeat messages (every 3 seconds during processing). Available as a `protected` method on the agent, and also via `IFabrCoreAgentHost` (so plugins can call it too):

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    SetStatusMessage("Searching documents..");
    var docs = await SearchRelevantDocs(message.Message);

    SetStatusMessage("Analyzing results..");
    var analysis = await AnalyzeResults(docs);

    SetStatusMessage(null); // reverts to "Thinking.."

    var response = message.Response();
    // ... process with LLM
    return response;
}

// Plugins can call it via IFabrCoreAgentHost:
// _agentHost.SetStatusMessage("Processing..");
```

For explicit progress updates that are sent as messages, use `SystemMessageTypes.Thinking` (`"_thinking"`). All underscore-prefixed message types are reserved for FabrCore system/control traffic and are ignored by agent chat stream delivery before `OnMessage`/`OnMessageBusy`.

When agent code is handed a full `AgentMessage` outside the normal grain chat-stream path (for example, direct tests, custom stream handlers, or utility methods that process captured messages), use `message.IsSystemMessage` to decide whether it is control/progress traffic:

```csharp
if (message.IsSystemMessage)
{
    return message.Response(); // or render/record it separately
}
```

Inside normal `OnMessage`, you usually do not need this guard for chat stream messages because `AgentGrain` filters underscore-prefixed system messages before dispatch. Use `SystemMessageTypes.IsSystemMessage(messageType)` only when you have a raw `MessageType` string instead of an `AgentMessage`.

### OnMessageBusy(AgentMessage)

Called when a new message arrives while `OnMessage` is already executing. The `OnMessage` method on `IAgentGrain` is marked `[AlwaysInterleave]`, which allows a second message to enter the grain while the first is still processing. The grain checks whether `OnMessage` is already running and routes to `OnMessageBusy` instead.

**Default behavior:** Returns a standard "Agent is currently processing a message. Please try again shortly." response. Override to customize.

**Safety:** The primary `OnMessage` may be at any `await` point when `OnMessageBusy` executes. Do NOT mutate shared agent state (custom state, chat history). Read-only operations are safe.

**`ActiveMessage` property:** Returns the message currently being processed by the primary `OnMessage` handler. Use it to provide context-aware busy responses.

**Stale message protection:** If the primary `OnMessage` has been running for more than 5 minutes (stuck LLM call, deadlocked tool), the grain treats the agent as stuck and allows the new message through as a fresh primary instead of busy-routing it.

```csharp
// Example: Acknowledge receipt and tell the caller what's happening
public override Task<AgentMessage> OnMessageBusy(AgentMessage message)
{
    var primaryMsg = ActiveMessage;
    var response = new AgentMessage
    {
        ToHandle = message.FromHandle,
        FromHandle = config.Handle,
        OnBehalfOfHandle = message.OnBehalfOfHandle,
        Message = $"I'm currently processing a request from {primaryMsg?.FromHandle ?? "another user"}. " +
                  "I'll be available shortly.",
        MessageType = message.MessageType,
        Kind = MessageKind.Response
    };
    // Stamp W3C trace fields from the ambient Activity — do NOT hand-copy message.TraceId.
    // The grain's OnMessageBusy ingress already opened an Activity; this keeps the response in the same trace.
    response.StampFromActivity(Activity.Current);
    return Task.FromResult(response);
}

// Example: Route timer messages differently when busy
public override Task<AgentMessage> OnMessageBusy(AgentMessage message)
{
    // Timer messages can be identified by their MessageType
    if (message.MessageType?.StartsWith("timer:") == true)
    {
        // Skip timer work when busy — the next tick will catch up
        return Task.FromResult(message.Response());
    }

    // Default busy response for user messages
    return base.OnMessageBusy(message);
}
```

**What gets captured:** Busy-routed messages are recorded in the message monitor with `BusyRouted = true`. No heartbeat is sent, no compaction runs, and no chat history is flushed for busy messages.

### OnEvent(EventMessage)

Called for fire-and-forget events via the AgentEvent stream. Events use `EventMessage` (CloudEvents-inspired), not `AgentMessage`.

```csharp
public override Task OnEvent(EventMessage eventMessage)
{
    switch (eventMessage.Type)
    {
        case "status-changed":
            // Handle status change
            break;
    }
    return Task.CompletedTask;
}
```

When verifiable execution is enabled, `AgentGrain` records event publish/delivery/handled evidence around `OnEvent`. Agent code normally does not sign records manually. If an agent performs an important external side effect directly, use the protected `VerifiableExecution` context with `FabrCore.Sdk.VerifiableExecution` helpers such as `RecordDbEffectAsync`, `RecordHttpCallAsync`, `RecordStorageEffectAsync`, or `RecordLibraryCallAsync`. See `fabrcore-spiffe`.

## Telemetry (OpenTelemetry / W3C TraceContext)

Every `AgentMessage` and `EventMessage` carries W3C `TraceId` / `SpanId` / `ParentSpanId`. Your agent's lifecycle methods run **inside** an Activity started by `AgentGrain` at source `FabrCore.Host.AgentGrain` (parented on the inbound message/event trace context via `StartIngressActivity`). That means:

- `Activity.Current` is non-null inside `OnMessage` / `OnMessageBusy` / `OnEvent` — use it.
- Any child span you start from your own `ActivitySource` auto-parents on the grain span; no context plumbing needed.
- Outbound responses returned from `OnMessage` are auto-stamped by the grain before delivery (see `src/FabrCore.Host/Grains/AgentGrain.cs:673,795`) — you only need to stamp when you build a response in a method that returns the `AgentMessage` directly *and* want to be safe (e.g. `OnMessageBusy`).
- When verifiable execution is enabled, the host also attaches `VerifiableExecutionEnvelope` to messages/events and records signed evidence; do not overwrite this envelope in agent code.

### Creating child spans in your agent

```csharp
public class MyAgent : FabrCoreAgentProxy
{
    private static readonly ActivitySource Source = new("MyCompany.MyAgent");

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        using var activity = Source.StartActivity("search-docs");
        activity?.SetTag("query.length", message.Message?.Length ?? 0);

        var result = await DoTheWork(message);

        var response = message.Response();
        response.Message = result;
        // Grain will StampFromActivity before returning to the caller; optional here.
        return response;
    }
}
```

### Fire-and-forget sends from inside your agent

When you push a message into a stream yourself (fire-and-forget), stamp it so downstream receivers can parent their spans on yours:

```csharp
var outbound = new AgentMessage { ToHandle = "peer", Message = "tick", Kind = MessageKind.OneWay };
outbound.StampFromActivity(Activity.Current);
await someStream.OnNextAsync(outbound);
```

This is the pattern `FabrCoreAgentService` uses internally — see `src/FabrCore.Host/Services/FabrCoreAgentService.cs:88-127`.

### Viewing the spans

FabrCore ships `OpenTelemetry.Api` only — no exporter. See **fabrcore-server → OpenTelemetry exporter setup** to wire Jaeger / OTLP / Console and actually see your spans. See **fabrcore-messaging → Correlation and Tracing** for the full W3C surface and helper reference.

## Custom State Persistence

Persist arbitrary state that survives grain deactivation:

```csharp
// Read state (returns default if not found, null, or undefined)
var stats = await GetStateAsync<ConversationStats>("stats");

// Safe read for migration-prone or resettable state
var stateRead = await TryGetStateAsync<ConversationStats>("stats");
if (!stateRead.Succeeded)
{
    logger.LogWarning(
        stateRead.Error,
        "Resetting unreadable state key {Key}; stored kind was {ValueKind}",
        stateRead.Key,
        stateRead.ValueKind);
    RemoveState(stateRead.Key);
    stats = new ConversationStats();
}

// Read or create with factory
var prefs = await GetStateOrCreateAsync("preferences", () => new UserPreferences
{
    Language = "en",
    Theme = "dark"
});

// Write state (buffered in memory)
prefs.Theme = "light";
SetState("preferences", prefs);

// Remove a key
RemoveState("old-key");

// Persist all pending changes to Orleans storage
await FlushStateAsync();

// Check if key exists
var hasPrefs = await HasStateAsync("preferences");
```

State is stored as `JsonElement` in the grain's persistent state. `GetStateAsync<T>` treats missing, `null`, and `JsonValueKind.Undefined` values as `default`; malformed or incompatible JSON throws an `InvalidOperationException` that includes the state key, agent handle, agent type, target type, and stored value kind. Use `TryGetStateAsync<T>` when an agent can migrate, reset, or ignore unreadable state; inspect `StateReadResult<T>.Key`, `.ValueKind`, and `.Error`.

State is automatically flushed after `OnMessage` completes and on normal grain deactivation. During hard eviction, pending state/chat buffers are intentionally not flushed because the persisted grain state is being deleted. Call `FlushStateAsync()` explicitly if you need durability mid-operation.

### Agent state vs typed entity storage

Use the built-in state API above for private state private to the current agent, such as conversation counters, local preferences, or per-agent caches. It is single-agent state and participates in the grain lifecycle.

Use typed entity storage when the data is application-level and should be addressable by `principalHandle/container/entityKey`, especially when API clients, host services, plugins, or multiple agents need to share the same record. The public abstraction is in `FabrCore.Sdk`:

```csharp
public interface IFabrCoreStorageProvider
{
    Task<T?> GetAsync<T>(string container, string entityKey, CancellationToken cancellationToken = default);
    Task UpsertAsync<T>(string container, string entityKey, T value, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string container, string entityKey, CancellationToken cancellationToken = default);
}
```

Important pitfall for agents: when resolving `IFabrCoreStorageProvider` directly inside the Host DI container, the principal-handle-free methods use the system partition. That is appropriate for system/shared data, not per-principal data. For per-agent or per-principal data, prefer `GetStateAsync`/`TryGetStateAsync`/`SetState` or call a principal-handle-aware Host/API path that explicitly supplies the principal handle from `fabrcoreAgentHost.GetUserHandle()` (legacy method name).

Do not reference Orleans storage APIs (`IGrainStorage`, `GrainId`, `IGrainState<T>`) from agent code. FabrCore keeps those Host-internal so agents and SDK consumers do not depend on Orleans storage internals.

## Context Management: the compaction ladder

FabrCore bounds context with **five ordered rungs**, cheapest and most reversible first. Everything is anchored to one setting — `ContextWindowTokens` — so the rungs stay in order without tuning them individually.

```
0.50  layer 1  evict old tool results       free, reversible, no LLM call
0.80  layer 1  truncate oldest groups       free, reversible, no LLM call
0.87  layer 2  summarize + rewrite thread   one LLM call, permanent
0.90  ---      projection fuse              blunt clip, insurance only
1.00  ---      run-safety stop              FabrCoreRunStoppedException
```

The two layers are distinguished by one question: **does it change what's on disk?**

| | Layer 1 — *context* compaction | Layer 2 — *history* compaction |
|---|---|---|
| Runs | Before every model call, in the tool loop | Preflight + post-turn |
| Bounds | What this LLM call sees | What is persisted in `MessageThreads` |
| Reversible | Yes — groups marked excluded | No — the thread is rewritten |
| Costs an LLM call | No | Yes (map-reduce summary) |
| Implemented by | `Microsoft.Agents.AI.Compaction.CompactionProvider` | `CompactionService` |
| Override hook | `CompactionStrategy` (code) | `OnCompaction` (virtual) |

Layer 1's state is deliberately **not persisted**. Its group index holds a full copy of every message it has seen; persisting it would duplicate the conversation into the agent state blob and let a stale index outlive a layer 2 rewrite. The strategy is deterministic and LLM-free, so rebuilding it each activation is free.

**No agent code is needed.** Set `ContextWindowTokens` and `MaxOutputTokens` on the model and both layers self-configure. Settings resolve in order: **defaults → host model config (fabrcore.json, or cloud server when enabled) → agent Args overrides**.

### Resolved-ladder diagnostics

Every agent logs its resolved ladder once, at information level, when compaction initializes:

```
Compaction ladder for 'my-agent' provider 'thread-1' (model config 'default'):
  evict@92000 → truncate@147200 → history@174000 → fuse@180000 → stop@200000
```

Disabled rungs render as `history:off` / `fuse:off` so a missing bound is visible rather than implied. `context:unconfigured` means `ContextWindowTokens` or `MaxOutputTokens` is missing and the agent is running with **no in-run context bound**. A `[OUT OF ORDER]` suffix means a later rung fires before an earlier one, making the earlier rung decorative — nearly always a misconfiguration.

Layer 2 also emits monitor events: `compaction.history.started`, `compaction.history.completed`, `compaction.history.failed`, each tagged with `trigger` (`preflight` or `post-turn`). Layer 1 emits OpenTelemetry spans through `CompactionTelemetry` instead.

### Model-level settings

On each model entry in `fabrcore.json`, or in the cluster config when the host uses a cloud server:

| Field | Default | Description |
|-------|---------|-------------|
| `ContextWindowTokens` | unset | **The anchor.** Total context window in tokens |
| `MaxOutputTokens` | unset | Output reserve. Layer 1 needs this and `ContextWindowTokens` |
| `ContextCompactionEnabled` | `true` | Enable/disable layer 1 (in-run context compaction) |
| `ContextEvictThreshold` | `0.5` | Fraction of input budget at which old tool results collapse |
| `ContextTruncateThreshold` | `0.8` | Fraction of input budget at which oldest groups drop |
| `CompactionEnabled` | `true` | Enable/disable layer 2 (history compaction) |
| `CompactionKeepLastN` | `20` | Keep this many recent messages when rewriting the thread |
| `CompactionThreshold` | `0.87` with layer 1, `0.75` without | Fraction of the window at which the thread is summarized |
| `CompactionStaleAfterMinutes` | `60` | Preflight-compact a dormant over-threshold thread before the next turn |
| `PerTurnMaxInputTokens` | unset | Stop a turn after cumulative input exceeds this budget |
| `MaxPromptInputTokens` | `ContextWindowTokens` | Stop a single LLM call before sending an oversized prompt |
| `RunawayBudgetBehavior` | `StopWithDiagnostic` | Behavior when a run-safety budget is exceeded |

The **input budget** for layer 1 is `ContextWindowTokens - MaxOutputTokens`; its two thresholds are fractions of that. Layer 2's threshold is a fraction of `ContextWindowTokens` itself.

### Agent-level overrides

In `AgentConfiguration.Args`, prefixed with `_`:

| Key | Layer | Description |
|-----|-------|-------------|
| `_ContextCompactionEnabled` | 1 | Turn off in-run context compaction |
| `_ContextWindowTokens` | 1 | Override the window for this agent |
| `_ContextMaxOutputTokens` | 1 | Override the output reserve |
| `_ContextEvictThreshold` | 1 | Move the tool-eviction rung |
| `_ContextTruncateThreshold` | 1 | Move the truncation rung |
| `_CompactionEnabled` | 2 | Turn off history compaction |
| `_CompactionMaxContextTokens` | 2 | Override the anchor used by layer 2 |
| `_CompactionKeepLastN` | 2 | Override keep-last-N |
| `_CompactionThreshold` | 2 | Move the history rung |
| `_CompactionStaleAfterMinutes` | 2 | Override preflight staleness |
| `_ProjectionEnabled` / `_ProjectionMaxContextTokens` / `_ProjectionThreshold` / `_ProjectionMinKeepLastN` | fuse | Move or disable the fuse |
| `_PerTurnMaxInputTokens` | stop | Override cumulative per-turn input budget |
| `_MaxPromptInputTokens` | stop | Override single-call prompt budget |
| `_RunawayBudgetBehavior` | stop | Override runaway budget behavior |

> **Retired:** `MidTurnCompactionEnabled` / `_MidTurnCompactionEnabled` no longer do anything. Mid-turn history compaction rewrote the persisted thread inside the tool loop, which corrupts a live layer 1 group index. Layer 1 replaces that job with a per-call mechanism that costs nothing and touches no storage. The setting is still accepted so existing `fabrcore.json` files keep loading; the value is ignored.

### Customizing layer 2

Override `OnCompaction` to change how the persisted thread is consolidated — a different prompt, model, or summarization strategy. This is **not** the hook for bounding a single LLM call; that is layer 1.

```csharp
public override async Task<CompactionResult?> OnCompaction(
    FabrCoreChatHistoryProvider chatHistoryProvider,
    CompactionConfig compactionConfig,
    int estimatedTokens = 0)
{
    // Custom consolidation logic — your own prompt, model, or strategy.
    // Or call the base implementation, which delegates to CompactionService:
    return await base.OnCompaction(chatHistoryProvider, compactionConfig, estimatedTokens);
}
```

`FabrCore.Services.Memory` overrides this hook to extract durable graph memories before summarizing — see the `fabrcore-services-memory` skill.

### Two things that surprise people

- **Two summary formats coexist in one thread.** Layer 1 inserts `[Tool Calls]` and `[Summary]` messages into the request; layer 2 writes `[Compacted History]` into storage. Seeing both in a transcript is expected.
- **Layer 1's work is invisible in stored history.** Exclusions live in the session index, not in `MessageThreads`. Comparing "what the model saw" against stored messages will show a mismatch — that is the design, not a bug. Watch the `compaction.history.*` monitor events and `CompactionTelemetry` spans instead.
- **Disabling layer 2 alone unbounds the state blob.** The model stays inside its window while stored history grows forever. FabrCore logs a warning when it sees this combination.

## Timers and Reminders

### Timers (Non-Persistent)

Active only while the grain is activated. Lost on normal deactivation and explicitly disposed during hard eviction.

```csharp
// In OnInitialize or OnMessage
fabrcoreAgentHost.RegisterTimer(
    timerName: "health-check",
    messageType: "timer:health-check",
    message: null,
    dueTime: TimeSpan.FromMinutes(1),
    period: TimeSpan.FromMinutes(5));

// Timer fires come as regular messages in OnMessage
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    if (message.MessageType == "timer:health-check")
    {
        var response = message.Response();
        response.Message = "Health check complete";
        return response;
    }
    // Normal message processing...
}

// Unregister
fabrcoreAgentHost.UnregisterTimer("health-check");
```

### Reminders (Persistent)

Survive normal grain deactivation and silo restarts. Minimum 1-minute period. Hard eviction enumerates and unregisters all Orleans reminders for the agent so they cannot wake the deleted grain.

```csharp
await fabrcoreAgentHost.RegisterReminder(
    reminderName: "daily-report",
    messageType: "reminder:daily-report",
    message: "Generate daily summary",
    dueTime: TimeSpan.FromHours(1),
    period: TimeSpan.FromHours(24));

// Override in your agent
public override Task OnReminder(string reminderName)
{
    if (reminderName == "daily-report")
    {
        // Perform periodic check
    }
    return Task.CompletedTask;
}

await fabrcoreAgentHost.UnregisterReminder("daily-report");
```

## Health Monitoring

Override `GetHealth()` to add custom health information:

```csharp
public override AgentHealthStatus GetHealth(HealthDetailLevel level)
{
    var health = base.GetHealth(level);

    if (level >= HealthDetailLevel.Detailed)
    {
        health = health with
        {
            Message = _isReady ? "Ready" : "Initializing"
        };
    }

    return health;
}
```

Health states: `Healthy`, `Degraded`, `Unhealthy`, `NotConfigured`.

## Local Tool Methods

Add methods directly to your agent that the LLM can call:

```csharp
public override async Task OnInitialize()
{
    var tools = await ResolveConfiguredToolsAsync();
    tools.Add(AIFunctionFactory.Create(SearchDatabase));
    tools.Add(AIFunctionFactory.Create(SendNotification));

    var result = await CreateChatClientAgent(
        config.Models ?? "default",
        threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
        tools: tools);
    _agent = result.Agent;
    _session = result.Session;
}

[Description("Search the database for records matching the query")]
private async Task<string> SearchDatabase(string query, int limit = 10)
{
    // Implementation
    return JsonSerializer.Serialize(results);
}
```

## AgentConfiguration

```csharp
var agentConfig = new AgentConfiguration
{
    Handle = "my-agent",
    AgentType = "my-agent",         // Must match [AgentAlias]
    Models = "default",             // Model name from fabrcore.json (single string)
    SystemPrompt = "You are a helpful assistant.",
    Streams =
    [
        EventStreamSubscription.For("velo-itinerary", "itinerary-event-agent")
    ],
    Plugins = ["weather"],          // Must match [PluginAlias] values
    Tools = ["calculate"],          // Must match [ToolAlias] values
    McpServers = [
        new McpServerConfig
        {
            Name = "filesystem",
            TransportType = McpTransportType.Stdio,
            Command = "npx",
            Arguments = ["-y", "@anthropic/mcp-filesystem"]
        }
    ],
    Args = new Dictionary<string, string>
    {
        ["weather:ApiKey"] = "abc123",
        ["_CompactionEnabled"] = "true"
    }
};
```

Blueprints use this same `AgentConfiguration` shape to ensure a baseline set of agents for one principal. They are applied by the Host API, not by an agent or automatically at Host startup. In a Blueprint, `ForceReconfigure` is always ignored; use `POST /fabrcoreapi/Agent/create` when an existing agent must be intentionally reconfigured. See **fabrcore-server → Blueprint Provisioning** for the caller workflow.

## Important Constraints

- **Never share tool instances across agents** — each agent must have its own tool instances due to Orleans' single-threaded actor model.
- **Don't call tools directly from other agents** — use `fabrcoreAgentHost.SendAndReceiveMessage()` for inter-agent communication.
- **Constructor must match exactly:** `(AgentConfiguration, IServiceProvider, IFabrCoreAgentHost)` — Orleans instantiates agents via DI.
- **`OnMessage` is single-entry** — Orleans interleaving allows `OnMessageBusy` to execute concurrently, but only one `OnMessage` runs at a time. Do not mutate shared state in `OnMessageBusy`.
- **Chat history is auto-flushed** after `OnMessage` completes and on grain deactivation. Not flushed after `OnMessageBusy`.
- **Custom state requires explicit flush** — call `FlushStateAsync()` if you need durability before `OnMessage` returns.
- **Do not call channel provider APIs directly for principal notifications** — use `SendToUserAsync` and an installed principal-message relay so delivery remains durable and provider-neutral.
- **Eviction is host-owned** — agents should unregister timers/reminders they no longer need during normal operation, but `DELETE /fabrcoreapi/Agent/{handle}` is responsible for final cleanup and rejects active `OnMessage` work with `409 Conflict`.
