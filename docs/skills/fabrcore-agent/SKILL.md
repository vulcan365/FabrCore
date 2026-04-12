---
name: fabrcore-agent
description: >
  Build FabrCore agents — extend FabrCoreAgentProxy, implement lifecycle methods (OnInitialize, OnMessage, OnEvent),
  manage custom state, configure compaction, timers, reminders, and health monitoring.
  Triggers on: "FabrCoreAgentProxy", "AgentAlias", "OnInitialize", "OnMessage", "OnMessageBusy", "OnEvent", "OnCompaction",
  "ResolveConfiguredToolsAsync", "CreateChatClientAgent", "SetStatusMessage", "agent state", "GetStateAsync",
  "SetState", "FlushStateAsync", "compaction", "agent timer", "RegisterTimer", "agent reminder", "RegisterReminder",
  "OnReminder", "agent health", "GetHealth", "AgentHealthStatus", "build agent", "create agent",
  "FabrCoreCapabilities", "FabrCoreNote", "agent capabilities", "agent notes", "busy message", "concurrent message",
  "AlwaysInterleave", "busy routing", "agent busy".
  Do NOT use for: Microsoft Agent Framework internals (AIAgent, AgentSession) — use fabrcore-agentframework.
  Do NOT use for: plugins, tools, MCP — use fabrcore-plugins-tools or fabrcore-mcp.
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

```csharp
// Full handle (e.g., "user123:assistant")
var full = fabrcoreAgentHost.GetHandle();

// Owner portion (e.g., "user123") — empty string if no owner
var owner = fabrcoreAgentHost.GetOwnerHandle();

// Agent handle portion without owner prefix (e.g., "assistant")
var agent = fabrcoreAgentHost.GetAgentHandle();

// Decompose into both parts at once
var (owner, agentHandle) = fabrcoreAgentHost.GetParsedHandle();

// Check if this agent has an owner
if (fabrcoreAgentHost.HasOwner())
{
    // Owner-scoped logic
}
```

| Method | Returns | Example (`"user123:assistant"`) | Example (`"assistant"`) |
|--------|---------|-------------------------------|------------------------|
| `GetHandle()` | Full handle string | `"user123:assistant"` | `"assistant"` |
| `GetOwnerHandle()` | Owner portion | `"user123"` | `""` |
| `GetAgentHandle()` | Agent handle portion | `"assistant"` | `"assistant"` |
| `GetParsedHandle()` | `(Owner, AgentHandle)` tuple | `("user123", "assistant")` | `("", "assistant")` |
| `HasOwner()` | `bool` | `true` | `false` |

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

**LLM Usage Tracking:** Token counts are automatically captured and attached to the response `Args` (e.g., `_tokens_input`, `_tokens_output`, `_llm_calls`).

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
    return Task.FromResult(new AgentMessage
    {
        ToHandle = message.FromHandle,
        FromHandle = config.Handle,
        OnBehalfOfHandle = message.OnBehalfOfHandle,
        Message = $"I'm currently processing a request from {primaryMsg?.FromHandle ?? "another user"}. " +
                  "I'll be available shortly.",
        MessageType = message.MessageType,
        Kind = MessageKind.Response,
        TraceId = message.TraceId
    });
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

## Custom State Persistence

Persist arbitrary state that survives grain deactivation:

```csharp
// Read state (returns default if not found)
var stats = await GetStateAsync<ConversationStats>("stats");

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

State is stored as `JsonElement` in the grain's persistent state. It is automatically flushed after `OnMessage` completes and on grain deactivation. Call `FlushStateAsync()` explicitly if you need durability mid-operation.

## Chat History Compaction

Compaction runs automatically after every `OnMessage`. When stored chat history exceeds the configured token threshold, older messages are summarized via an LLM call and replaced with a compact summary.

**No agent code is needed** — compaction is handled by the framework. Settings resolve in order: **defaults → fabrcore.json model config → agent Args overrides**.

**Model-level** (in `fabrcore.json` on each model entry):

| Field | Default | Description |
|-------|---------|-------------|
| `ContextWindowTokens` | `25000` | Total context window size in tokens |
| `CompactionEnabled` | `true` | Enable/disable compaction |
| `CompactionKeepLastN` | `20` | Keep this many recent messages |
| `CompactionThreshold` | `0.75` | Trigger at this fraction of context window |

**Agent-level overrides** (in `AgentConfiguration.Args`, prefixed with `_`):

| Key | Description |
|-----|-------------|
| `_CompactionEnabled` | Override enable/disable |
| `_CompactionMaxContextTokens` | Override context window size |
| `_CompactionKeepLastN` | Override keep-last-N |
| `_CompactionThreshold` | Override threshold ratio |

### Custom Compaction

Override `OnCompaction` to customize the compaction strategy:

```csharp
public override async Task<CompactionResult?> OnCompaction(
    FabrCoreChatHistoryProvider chatHistoryProvider,
    CompactionConfig compactionConfig,
    int estimatedTokens = 0)
{
    // Custom compaction logic — use your own prompt, model, or strategy
    // Or call the base implementation which delegates to CompactionService:
    return await base.OnCompaction(chatHistoryProvider, compactionConfig, estimatedTokens);
}
```

## Timers and Reminders

### Timers (Non-Persistent)

Active only while the grain is activated. Lost on deactivation.

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

Survive grain deactivation and silo restarts. Minimum 1-minute period. Override `OnReminder`:

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

## Important Constraints

- **Never share tool instances across agents** — each agent must have its own tool instances due to Orleans' single-threaded actor model.
- **Don't call tools directly from other agents** — use `fabrcoreAgentHost.SendAndReceiveMessage()` for inter-agent communication.
- **Constructor must match exactly:** `(AgentConfiguration, IServiceProvider, IFabrCoreAgentHost)` — Orleans instantiates agents via DI.
- **`OnMessage` is single-entry** — Orleans interleaving allows `OnMessageBusy` to execute concurrently, but only one `OnMessage` runs at a time. Do not mutate shared state in `OnMessageBusy`.
- **Chat history is auto-flushed** after `OnMessage` completes and on grain deactivation. Not flushed after `OnMessageBusy`.
- **Custom state requires explicit flush** — call `FlushStateAsync()` if you need durability before `OnMessage` returns.
