# Building FabrCore Agents

## Agent Fundamentals

Every FabrCore agent extends `FabrCoreAgentProxy` and is decorated with `[AgentAlias]`:

```csharp
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

[AgentAlias("my-agent")]
public class MyAgent : FabrCoreAgentProxy
{
    private ChatClientAgent? _agent;
    private AgentThread? _thread;

    public MyAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrAgentHost)
        : base(config, serviceProvider, fabrAgentHost) { }

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
    // Option A: Use CreateChatClientAgent helper (recommended)
    var result = await CreateChatClientAgent("default");
    _agent = result.Agent;
    _thread = result.Thread;

    // Option B: Manual setup for more control
    var chatClient = await GetChatClient("default");
    var tools = await ResolveConfiguredToolsAsync();

    _agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
    {
        ChatOptions = new ChatOptions
        {
            Instructions = config.SystemPrompt,
            Tools = tools
        },
        Name = fabrAgentHost.GetHandle()
    })
    .AsBuilder()
    .UseOpenTelemetry(null, cfg => cfg.EnableSensitiveData = true)
    .Build(serviceProvider);

    _thread = _agent.GetNewThread();
}
```

**`CreateChatClientAgent`** handles:
- Creating the chat client for the specified model
- Resolving all configured tools (plugins, standalone tools, MCP servers)
- Setting up chat history persistence
- Configuring compaction
- Applying OpenTelemetry instrumentation

### OnMessage(AgentMessage)

Called for every `Request` or `OneWay` message. Must return an `AgentMessage` response.

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var response = message.Response();

    // Streaming response
    var chatMessage = new ChatMessage(ChatRole.User, message.Message);
    await foreach (var msg in _agent!.InvokeStreamingAsync(
        [chatMessage], _thread!))
    {
        response.Message += msg.Text;
    }

    // Or non-streaming
    var result = await _agent!.InvokeAsync([chatMessage], _thread!);
    response.Message = result.Messages.Last().Text;

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

### Single Thread (Default)

One conversation per agent instance. Simplest pattern.

```csharp
private AgentThread? _thread;

public override async Task OnInitialize()
{
    var result = await CreateChatClientAgent("default");
    _agent = result.Agent;
    _thread = result.Thread;
}
```

### Per-User Thread

Separate conversation history per user (useful for shared agents).

```csharp
private readonly Dictionary<string, AgentThread> _threads = new();

public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var userId = message.FromHandle;
    if (!_threads.TryGetValue(userId, out var thread))
    {
        thread = _agent!.GetNewThread();
        _threads[userId] = thread;
    }
    // Use thread for this user's conversation
}
```

### Per-Message Thread

No conversation history — each message is independent.

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var thread = _agent!.GetNewThread();
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
await TryCompactAsync(_agent!, _thread!);
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
fabrAgentHost.RegisterTimer(
    "timer-name",
    async () => { /* periodic work */ },
    TimeSpan.FromSeconds(5),   // due time
    TimeSpan.FromMinutes(1));  // period
```

### Reminders (Persistent)

Survive grain deactivation and silo restarts. Override `OnReminder`:

```csharp
await fabrAgentHost.RegisterReminder(
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
        var result = await CreateChatClientAgent("default",
            additionalTools: [
                AIFunctionFactory.Create(SearchDatabase),
                AIFunctionFactory.Create(SendNotification)
            ]);
        _agent = result.Agent;
        _thread = result.Thread;
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
