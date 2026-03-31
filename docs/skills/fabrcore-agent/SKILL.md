---
name: fabrcore-agent
description: >
  Build FabrCore agents — extend FabrCoreAgentProxy, implement lifecycle methods (OnInitialize, OnMessage, OnEvent),
  manage custom state, configure compaction, timers, reminders, and health monitoring.
  Triggers on: "FabrCoreAgentProxy", "AgentAlias", "OnInitialize", "OnMessage", "OnEvent", "OnCompaction",
  "ResolveConfiguredToolsAsync", "CreateChatClientAgent", "SetStatusMessage", "agent state", "GetStateAsync",
  "SetState", "FlushStateAsync", "compaction", "agent timer", "RegisterTimer", "agent reminder", "RegisterReminder",
  "OnReminder", "agent health", "GetHealth", "AgentHealthStatus", "build agent", "create agent".
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

Controls the text sent in `_status` heartbeat messages (every 3 seconds during processing):

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
```

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
    CompactionConfig compactionConfig)
{
    // Custom compaction logic — use your own prompt, model, or strategy
    // Or call the default CompactionService:
    if (CompactionServiceInstance is null || CompactionChatClientConfigName is null)
        return null;

    return await CompactionServiceInstance.CompactIfNeededAsync(
        chatHistoryProvider, compactionConfig, CompactionChatClientConfigName);
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
- **Agents are single-threaded** — no need for locks or concurrent collections within an agent.
- **Chat history is auto-flushed** after `OnMessage` completes and on grain deactivation.
- **Custom state requires explicit flush** — call `FlushStateAsync()` if you need durability before `OnMessage` returns.
