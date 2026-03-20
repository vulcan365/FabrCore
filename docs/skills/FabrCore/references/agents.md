# Building FabrCore Agents

## Agent Fundamentals

Every FabrCore agent extends `FabrCoreAgentProxy` and is decorated with `[AgentAlias]`:

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
    public override Task OnEvent(AgentMessage eventMessage) { /* ... */ }
}
```

### Constructor Pattern

The constructor always takes three parameters — do not add additional constructor parameters. Use `IServiceProvider` to resolve any services you need in `OnInitialize()`.

### Naming Convention

- Agent alias: `kebab-case` (e.g., `"my-agent"`, `"code-reviewer"`)
- Class name: `PascalCase` (e.g., `MyAgent`, `CodeReviewer`)
- The alias is what gets used in `AgentConfiguration.AgentType`

## Lifecycle Methods

### OnInitialize()

Called once before the first message is processed, or when the agent is reconfigured.

```csharp
public override async Task OnInitialize()
{
    // Step 1: Resolve tools from configured plugins, standalone tools, and MCP servers
    var tools = await ResolveConfiguredToolsAsync();

    // Step 2: Create the chat client agent with tools
    var result = await CreateChatClientAgent(
        "default",                                              // model config name from fabrcore.json
        threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),   // thread ID for history persistence
        tools: tools);                                          // resolved tools

    _agent = result.Agent;
    _session = result.Session;
}
```

**Important:** `ResolveConfiguredToolsAsync()` must be called before `CreateChatClientAgent` — tools are NOT auto-resolved. This method:
- Resolves plugins from `config.Plugins` via `[PluginAlias]` (calls `InitializeAsync` on each)
- Resolves standalone tools from `config.Tools` via `[ToolAlias]`
- Connects MCP servers from `config.McpServers` and discovers their tools
- Returns all tools as `List<AITool>`

**`CreateChatClientAgent`** signature:
```csharp
protected Task<ChatClientAgentResult> CreateChatClientAgent(
    string chatClientConfigName,          // Required: model name from fabrcore.json
    string threadId,                      // Required: ID for chat history persistence
    IList<AITool>? tools = null,          // Optional: resolved tools
    Action<ChatClientAgentOptions>? configureOptions = null)  // Optional: further configuration
```

**`ChatClientAgentResult`** contains:
- `Agent` (`AIAgent`) — The configured agent instance
- `Session` (`AgentSession`) — The conversation session for message history
- `ChatHistoryProvider` (`FabrCoreChatHistoryProvider?`) — For compaction support

`CreateChatClientAgent` handles:
- Creating the chat client for the specified model
- Setting up automatic chat history persistence
- Applying the system prompt from `config.SystemPrompt`
- Applying OpenTelemetry instrumentation

### OnMessage(AgentMessage)

Called for every `Request` or `OneWay` message. Must return an `AgentMessage` response.

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var response = message.Response();
    var chatMessage = new ChatMessage(ChatRole.User, message.Message);

    // Streaming response (recommended)
    await foreach (var update in _agent!.RunStreamingAsync(
        chatMessage, _session!))
    {
        response.Message += update.Text;
    }

    // Or non-streaming
    // var result = await _agent!.RunAsync(chatMessage, _session!);
    // response.Message = result.Messages.Last().Text;

    return response;
}
```

### OnEvent(AgentMessage)

Called for fire-and-forget event messages. No response expected.

```csharp
public override Task OnEvent(AgentMessage eventMessage)
{
    var eventType = eventMessage.MessageType;
    // Handle the event (logging, state updates, etc.)
    return Task.CompletedTask;
}
```

## Thread Management Patterns

### Single Session (Default)

One conversation per agent instance. Simplest pattern.

```csharp
private AIAgent? _agent;
private AgentSession? _session;

public override async Task OnInitialize()
{
    var tools = await ResolveConfiguredToolsAsync();
    var result = await CreateChatClientAgent(
        "default",
        threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
        tools: tools);
    _agent = result.Agent;
    _session = result.Session;
}
```

### Per-User Session

Separate conversation history per user. Use different `threadId` values to isolate history.

```csharp
private readonly Dictionary<string, AgentSession> _sessions = new();

public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var userId = message.FromHandle;
    if (!_sessions.TryGetValue(userId, out var session))
    {
        session = await _agent!.CreateSessionAsync();
        _sessions[userId] = session;
    }
    // Use session for this user's conversation
}
```

### Per-Message Session

No conversation history — each message is independent.

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var session = await _agent!.CreateSessionAsync();
    // Process with fresh context every time
}
```

## Custom State Persistence

Agents can persist arbitrary state that survives grain deactivation:

```csharp
// Save state
SetState("preferences", new UserPreferences { Theme = "dark" });
await FlushStateAsync();

// Load state
var prefs = await GetStateAsync<UserPreferences>("preferences");
```

State is stored as `JsonElement` in the grain's persistent state, keyed by string.

## Chat History Compaction

When conversation history grows too large for the context window, use compaction:

```csharp
// In OnMessage, before processing
await TryCompactAsync(_agent!, _session!);
```

Compaction summarizes older messages using the LLM and replaces them with a compact summary. Configure via `AgentConfiguration.Args`:

| Key | Default | Description |
|-----|---------|-------------|
| `CompactionEnabled` | `true` | Enable/disable compaction |
| `CompactionKeepLastN` | `20` | Keep this many recent messages |
| `CompactionThreshold` | `0.75` | Trigger at this % of context window |

## Timers and Reminders

### Timers (Non-Persistent)

Active only while the grain is activated. Lost on deactivation.

```csharp
// In OnInitialize or OnMessage
fabrcoreAgentHost.RegisterTimer(
    "timer-name",
    async () => { /* periodic work */ },
    TimeSpan.FromSeconds(5),   // due time
    TimeSpan.FromMinutes(1));  // period
```

### Reminders (Persistent)

Survive grain deactivation and silo restarts. Override `OnReminder`:

```csharp
await fabrcoreAgentHost.RegisterReminder(
    "daily-check",
    TimeSpan.FromMinutes(1),   // due time
    TimeSpan.FromHours(24));   // period

// Override in your agent
public override Task OnReminder(string reminderName)
{
    if (reminderName == "daily-check")
    {
        // Perform periodic check
    }
    return Task.CompletedTask;
}
```

## Health Monitoring

Override `GetHealth()` to add custom health information:

```csharp
public override AgentHealthStatus GetHealth(HealthDetailLevel level)
{
    var health = base.GetHealth(level);

    if (level >= HealthDetailLevel.Detailed)
    {
        // Add custom diagnostics
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
[AgentAlias("my-agent")]
public class MyAgent : FabrCoreAgentProxy
{
    // ... constructor, fields ...

    public override async Task OnInitialize()
    {
        // Resolve configured plugins/tools, then add local tool methods
        var tools = await ResolveConfiguredToolsAsync();
        tools.Add(AIFunctionFactory.Create(SearchDatabase));
        tools.Add(AIFunctionFactory.Create(SendNotification));

        var result = await CreateChatClientAgent(
            "default",
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

    [Description("Send a notification to a user")]
    private async Task<string> SendNotification(string userId, string message)
    {
        // Implementation
        return "Notification sent";
    }
}
```

## AgentConfiguration for Agents

Create agents programmatically or via configuration:

```csharp
var config = new AgentConfiguration
{
    Handle = "my-agent",
    AgentType = "my-agent",         // Must match [AgentAlias]
    Models = ["default"],           // Model names from fabrcore.json
    SystemPrompt = "You are a helpful assistant.",
    Plugins = ["weather", "files"], // Must match [PluginAlias] values
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
        ["CompactionEnabled"] = "true"
    }
};
```
