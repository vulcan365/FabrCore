---
name: fabrcore-building-agents
description: Build FabrCore AI agents — create agent classes, plugins, standalone tools, MCP integrations, inter-agent communication, and multi-agent orchestration patterns on the Orleans distributed actor framework.
argument-hint: [AgentName or "plugin PluginName" or "tool ToolName"]
---

# Build FabrCore AI Agents

You are an expert FabrCore agent developer. Help the user build agents, plugins, standalone tools, and multi-agent systems on FabrCore — a .NET framework for distributed AI agents powered by Microsoft Orleans.

## Arguments

The input is provided as: `$ARGUMENTS`

Parse the input to determine what to build:
- **No arguments or an agent name** (e.g., `WeatherAgent`) → Build an agent
- **`plugin <Name>`** (e.g., `plugin GitHubPlugin`) → Build a plugin
- **`tool <Name>`** (e.g., `tool GetDateTime`) → Build a standalone tool

If no arguments are provided, ask the user:
1. What do you want to build? (agent, plugin, or tool)
2. A name for it
3. A brief description of what it should do

---

## Understanding FabrCore Architecture

Before generating code, understand these core concepts so you can make informed decisions:

### The Stack
- **Orleans** — distributed actor model (grains, silos, clustering)
- **Microsoft.Extensions.AI** — AI abstraction layer (`IChatClient`, `AIFunctionFactory`, `AITool`)
- **Microsoft Agent Framework** — `AIAgent`, `AgentSession`, `ChatClientAgent` abstractions
- **FabrCore.Sdk** — `FabrCoreAgentProxy`, `IFabrCorePlugin`, tool registry, MCP integration

### How It Fits Together
Each FabrCore agent is an Orleans grain — a distributed actor with isolated state, single-threaded execution, and location transparency. The `FabrCoreAgentProxy` base class wraps the Microsoft Agent Framework's `ChatClientAgent`, connecting it to Orleans persistence, tool resolution, MCP servers, and inter-agent messaging.

### Key Types Reference

| Type | Package | Purpose |
|------|---------|---------|
| `FabrCoreAgentProxy` | FabrCore.Sdk | Agent base class — extend this |
| `IFabrCorePlugin` | FabrCore.Sdk | Plugin interface — implement this |
| `IFabrCoreAgentHost` | FabrCore.Sdk | Host services (messaging, timers, state) |
| `AgentConfiguration` | FabrCore.Core | Agent config (Handle, AgentType, Models, SystemPrompt, Plugins, Tools, McpServers, Args) |
| `AgentMessage` | FabrCore.Core | Message contract (Id, ToHandle, FromHandle, OnBehalfOfHandle, DeliverToHandle, Channel, MessageType, Message, Kind, DataType, Data, Files, State, Args, TraceId) |
| `ChatClientAgentResult` | FabrCore.Sdk | Result record: `(AIAgent Agent, AgentSession Session, FabrCoreChatHistoryProvider? ChatHistoryProvider)` |
| `McpServerConfig` | FabrCore.Core | MCP server config (Name, TransportType, Command, Arguments, Env, Url, Headers) |
| `[AgentAlias]` | FabrCore.Sdk | Marks agent class for discovery |
| `[PluginAlias]` | FabrCore.Sdk | Marks plugin class for discovery |
| `[ToolAlias]` | FabrCore.Sdk | Marks standalone tool method for discovery |

---

## Part 1: Building an Agent

### Naming Convention
- **Class name:** PascalCase (e.g., `WeatherAgent`)
- **Agent alias:** kebab-case (e.g., `weather-agent`) — used in `[AgentAlias]` and `AgentConfiguration.AgentType`
- Convert the provided name to both forms automatically.

### Agent Template

Generate a single file `<AgentName>.cs`:

```csharp
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ComponentModel;

namespace <DetectNamespaceFromProject>;

/// <summary>
/// <Brief description of what this agent does.>
/// </summary>
[AgentAlias("<kebab-case-alias>")]
public class <AgentName> : FabrCoreAgentProxy
{
    private AIAgent? _agent;
    private AgentSession? _session;

    public <AgentName>(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
    }

    public override async Task OnInitialize()
    {
        // Resolve the model config name from agent configuration (defaults to "default")
        var modelConfigName = config.Models ?? "default";

        // Resolve tools from configuration (plugins + standalone tools + MCP servers)
        var tools = await ResolveConfiguredToolsAsync();

        // Add local tool methods defined in this class
        tools.Add(AIFunctionFactory.Create(ExampleTool));

        // Create a ChatClientAgent with auto-persisted chat history
        var result = await CreateChatClientAgent(
            chatClientConfigName: modelConfigName,
            threadId: config.Handle ?? "default",
            tools: tools);

        _agent = result.Agent;
        _session = result.Session;

        logger.LogInformation("Agent '{Handle}' initialized with model '{Model}'",
            config.Handle, modelConfigName);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        if (_agent == null || _session == null)
        {
            response.Message = "Agent is not initialized. Please try again.";
            return response;
        }

        // Process the message through the LLM agent (tools are called automatically)
        var result = await _session.ProcessAsync(message.Message ?? "", default);

        // Collect the response text
        response.Message = string.Join("", result.Select(r => r.Text));

        // Compact history if the context window is filling up
        await TryCompactAsync();

        return response;
    }

    public override async Task OnEvent(AgentMessage message)
    {
        // Handle fire-and-forget events from streams
        logger.LogInformation("Event received from '{From}': {Type}",
            message.FromHandle, message.MessageType);
    }

    /// <summary>
    /// Example tool method. The LLM sees the [Description] and calls this via function calling.
    /// Replace with your own tools or remove if not needed.
    /// </summary>
    [Description("Gets the current date and time in UTC.")]
    private string ExampleTool()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
    }
}
```

### Customization Based on User Description

Based on what the user says the agent should do, customize:

1. **Tool methods** — add domain-specific `[Description]`-annotated methods (the LLM sees method name, parameter names/types, and description to decide when to call)
2. **SystemPrompt** — tailor to the agent's purpose in the example config
3. **OnMessage routing** — add channel/type-based routing if handling multiple message types
4. **OnEvent handler** — implement if subscribing to event streams
5. **Custom state** — use `GetStateAsync<T>(key)` / `SetState(key, value)` / `FlushStateAsync()` for persistent agent state
6. **Plugins** — add `[PluginAlias]` names to the config if the agent needs plugin capabilities
7. **MCP servers** — add MCP configs for external tool integration

### Agent Lifecycle

Explain this to the user so they understand when each method runs:

| Method | When It Runs | Purpose |
|--------|-------------|---------|
| Constructor | Grain activation | DI wiring only — no async work |
| `OnInitialize()` | After construction | Set up LLM client, tools, threads |
| `OnMessage(AgentMessage)` | Request/response received | Process user/agent messages, return response |
| `OnEvent(AgentMessage)` | Fire-and-forget event | Handle stream event notifications |
| `GetHealth(HealthDetailLevel)` | Health check | Return custom health metrics |

### AgentSession Thread Patterns

Choose the right pattern based on the agent's use case:

**Single thread per agent** (default — personal assistant, most common):
```csharp
// In OnInitialize:
var result = await CreateChatClientAgent(modelConfigName,
    threadId: config.Handle ?? "default", tools: tools);
```

**Thread per user** (multi-user agent):
```csharp
private readonly Dictionary<string, (AIAgent Agent, AgentSession Session)> _userSessions = new();

public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var userId = message.FromHandle ?? "anonymous";
    if (!_userSessions.TryGetValue(userId, out var session))
    {
        var result = await CreateChatClientAgent(
            config.Models ?? "default",
            threadId: $"{config.Handle}-{userId}",
            tools: await ResolveConfiguredToolsAsync());
        session = (result.Agent, result.Session);
        _userSessions[userId] = session;
    }

    var response = message.Response();
    var result = await session.Session.ProcessAsync(message.Message ?? "", default);
    response.Message = string.Join("", result.Select(r => r.Text));
    return response;
}
```

**New thread per message** (stateless processing):
```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var tools = await ResolveConfiguredToolsAsync();
    var result = await CreateChatClientAgent(
        config.Models ?? "default",
        threadId: Guid.NewGuid().ToString(),
        tools: tools);

    var response = message.Response();
    var aiResult = await result.Session.ProcessAsync(message.Message ?? "", default);
    response.Message = string.Join("", aiResult.Select(r => r.Text));
    return response;
}
```

---

## Part 2: Building a Plugin

Plugins are **stateful tool collections** that implement `IFabrCorePlugin`. They receive DI services and agent configuration, making them ideal for API integrations, database access, and stateful workflows.

### When to Use a Plugin vs. a Tool

| Aspect | Plugin | Standalone Tool |
|--------|--------|-----------------|
| Implementation | Class with `IFabrCorePlugin` | Static method |
| State | Yes — holds injected services, config, connections | No — pure function |
| DI Support | Constructor injection + `IServiceProvider` in `InitializeAsync` | None (static context) |
| Lifecycle | `InitializeAsync` called once per agent | No lifecycle |
| Discovery | `[PluginAlias("name")]` on class | `[ToolAlias("name")]` on static method |
| Use Cases | API integrations, DB access, HTTP clients, stateful workflows | String formatting, math, date/time, simple conversions |

### Plugin Naming Convention
- **Class name:** PascalCase ending in `Plugin` (e.g., `GitHubPlugin`)
- **Plugin alias:** PascalCase without suffix (e.g., `GitHub`) — used in `[PluginAlias]` and `AgentConfiguration.Plugins`

### Plugin Template

Generate a single file `<PluginName>.cs`:

```csharp
using FabrCore.Core;
using FabrCore.Sdk;
using System.ComponentModel;

namespace <DetectNamespaceFromProject>;

/// <summary>
/// <Brief description of what this plugin provides.>
/// Provides tools for <describe capabilities>.
/// </summary>
[PluginAlias("<PluginAlias>")]
public class <PluginName> : IFabrCorePlugin
{
    private readonly IFabrCoreAgentHost _agentHost;
    private AgentConfiguration? _config;

    public <PluginName>(IFabrCoreAgentHost agentHost)
    {
        _agentHost = agentHost;
    }

    public async Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _config = config;

        // Access plugin-specific settings from AgentConfiguration.Args
        // Convention: "PluginAlias:SettingName" (e.g., "GitHub:ApiToken")
        var settings = config.GetPluginSettings("<PluginAlias>");
        // var apiToken = config.GetPluginSetting("<PluginAlias>", "ApiToken");

        // Initialize HTTP clients, database connections, etc.
        await Task.CompletedTask;
    }

    /// <summary>
    /// Each public method with a [Description] attribute becomes an LLM-callable tool.
    /// The LLM sees: method name, parameter names/types, and the description text.
    /// </summary>
    [Description("Describe what this tool does clearly — the LLM uses this to decide when to call it.")]
    public async Task<string> ExampleToolMethod(
        [Description("Describe this parameter")] string input)
    {
        // Implement tool logic here
        return $"Result for: {input}";
    }

    [Description("Another tool provided by this plugin.")]
    public string AnotherTool()
    {
        return "Tool result";
    }
}
```

### Plugin with IFabrCoreAgentHost (Inter-Agent Communication)

Plugins can use `IFabrCoreAgentHost` to communicate with other agents:

```csharp
[PluginAlias("Coordinator")]
public class CoordinatorPlugin : IFabrCorePlugin
{
    private readonly IFabrCoreAgentHost _agentHost;

    public CoordinatorPlugin(IFabrCoreAgentHost agentHost)
    {
        _agentHost = agentHost;
    }

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
        => Task.CompletedTask;

    [Description("Delegates a question to a specialist agent and returns their response.")]
    public async Task<string> AskSpecialist(
        [Description("The handle of the specialist agent")] string agentHandle,
        [Description("The question to ask")] string question)
    {
        var request = new AgentMessage
        {
            ToHandle = agentHandle,
            Message = question
        };

        var response = await _agentHost.SendAndReceiveMessage(request);
        return response.Message ?? "(no response)";
    }

    [Description("Sends a notification event to another agent (fire-and-forget).")]
    public async Task NotifyAgent(
        [Description("The handle of the agent to notify")] string agentHandle,
        [Description("The notification message")] string notification)
    {
        var eventMessage = new AgentMessage
        {
            ToHandle = agentHandle,
            Message = notification,
            MessageType = "notification",
            Kind = MessageKind.OneWay
        };

        await _agentHost.SendEvent(eventMessage);
    }
}
```

### Plugin with Disposable Resources

If your plugin holds HTTP clients, database connections, or other resources:

```csharp
[PluginAlias("DataAccess")]
public class DataAccessPlugin : IFabrCorePlugin, IAsyncDisposable
{
    private HttpClient? _httpClient;

    public DataAccessPlugin() { }

    public async Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        var baseUrl = config.GetPluginSetting("DataAccess", "BaseUrl")
            ?? throw new InvalidOperationException("DataAccess:BaseUrl is required in agent Args");

        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        var apiKey = config.GetPluginSetting("DataAccess", "ApiKey");
        if (apiKey != null)
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    [Description("Fetches data from the configured API endpoint.")]
    public async Task<string> FetchData([Description("API path to query")] string path)
    {
        var response = await _httpClient!.GetStringAsync(path);
        return response;
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
    }
}
```

### Plugin Settings Convention

Plugin settings are stored in `AgentConfiguration.Args` using the `PluginAlias:Key` naming pattern:

```csharp
// In AgentConfiguration:
Args = new Dictionary<string, string>
{
    ["GitHub:ApiToken"] = "ghp_xxx",
    ["GitHub:Organization"] = "my-org",
    ["DataAccess:BaseUrl"] = "https://api.example.com",
    ["DataAccess:ApiKey"] = "sk-xxx"
}

// In plugin InitializeAsync:
var token = config.GetPluginSetting("GitHub", "ApiToken");
var allSettings = config.GetPluginSettings("GitHub");
// allSettings = { "ApiToken": "ghp_xxx", "Organization": "my-org" }
```

---

## Part 3: Building a Standalone Tool

Standalone tools are **static methods** for simple, stateless operations. They require no class instance and are discovered via `[ToolAlias]`.

### Tool Template

```csharp
using FabrCore.Sdk;
using System.ComponentModel;

namespace <DetectNamespaceFromProject>;

/// <summary>
/// Standalone tools — static methods discovered by [ToolAlias].
/// Reference these by alias in AgentConfiguration.Tools.
/// </summary>
public static class <ToolClassName>
{
    [ToolAlias("<tool-alias>")]
    [Description("Describe what this tool does — the LLM uses this to decide when to call it.")]
    public static string <ToolMethodName>(
        [Description("Describe this parameter")] string input)
    {
        return $"Result: {input}";
    }
}
```

### Example: Multiple Standalone Tools

```csharp
using FabrCore.Sdk;
using System.ComponentModel;

namespace MyProject.Tools;

public static class UtilityTools
{
    [ToolAlias("GetDateTime")]
    [Description("Gets the current date and time in the specified timezone, or UTC if not specified.")]
    public static string GetDateTime(
        [Description("IANA timezone name (e.g., 'America/New_York'). Defaults to UTC.")] string? timezone = null)
    {
        var tz = timezone != null
            ? TimeZoneInfo.FindSystemTimeZoneById(timezone)
            : TimeZoneInfo.Utc;
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz)
            .ToString("yyyy-MM-dd HH:mm:ss");
    }

    [ToolAlias("JsonFormat")]
    [Description("Formats a JSON string with indentation for readability.")]
    public static string FormatJson(
        [Description("The JSON string to format")] string json)
    {
        var doc = System.Text.Json.JsonDocument.Parse(json);
        return System.Text.Json.JsonSerializer.Serialize(doc,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
```

---

## Part 4: Adding Tool Calling to Agents

Tool calling is the primary way agents interact with the world. The LLM sees method names, parameter names/types, and `[Description]` attributes, then decides when to call each tool. The Microsoft Agent Framework automatically invokes the C# method and feeds the result back to the LLM.

### Three Ways to Add Tools

**1. Local tool methods** (defined in the agent class):
```csharp
public override async Task OnInitialize()
{
    var tools = new List<AITool>();

    // Wrap private/public methods as tools
    tools.Add(AIFunctionFactory.Create(SearchDatabase));
    tools.Add(AIFunctionFactory.Create(SendEmail));

    var result = await CreateChatClientAgent("default",
        threadId: config.Handle ?? "default", tools: tools);
    _agent = result.Agent;
    _session = result.Session;
}

[Description("Searches the database for records matching the query.")]
private async Task<string> SearchDatabase(
    [Description("The search query")] string query,
    [Description("Maximum results to return")] int maxResults = 10)
{
    // Implementation here
    return $"Found {maxResults} results for '{query}'";
}

[Description("Sends an email to the specified recipient.")]
private async Task<string> SendEmail(
    [Description("Recipient email address")] string to,
    [Description("Email subject line")] string subject,
    [Description("Email body content")] string body)
{
    // Implementation here
    return $"Email sent to {to}";
}
```

**2. Configured plugins and tools** (resolved from AgentConfiguration):
```csharp
public override async Task OnInitialize()
{
    // Resolves plugins from config.Plugins + tools from config.Tools + MCP servers from config.McpServers
    var tools = await ResolveConfiguredToolsAsync();

    // Optionally add local tools too
    tools.Add(AIFunctionFactory.Create(MyLocalTool));

    var result = await CreateChatClientAgent("default",
        threadId: config.Handle ?? "default", tools: tools);
    _agent = result.Agent;
    _session = result.Session;
}
```

**3. MCP servers** (external tool providers via Model Context Protocol):

Config-driven (fail-open — agent continues without tools if connection fails):
```csharp
// MCP servers listed in AgentConfiguration.McpServers are automatically
// connected by ResolveConfiguredToolsAsync()
var tools = await ResolveConfiguredToolsAsync();
```

Code-driven (exceptions propagate — you handle errors):
```csharp
var mcpTools = await ConnectMcpServerAsync(new McpServerConfig
{
    Name = "my-mcp-server",
    TransportType = McpTransportType.Stdio,
    Command = "npx",
    Arguments = ["-y", "@my-org/mcp-server"],
    Env = new() { ["API_KEY"] = "secret" }
});
tools.AddRange(mcpTools);
```

HTTP MCP server:
```csharp
var mcpTools = await ConnectMcpServerAsync(new McpServerConfig
{
    Name = "remote-tools",
    TransportType = McpTransportType.Http,
    Url = "https://tools.example.com/mcp",
    Headers = new() { ["Authorization"] = "Bearer token" }
});
```

### Writing Effective Tool Descriptions

The LLM relies on `[Description]` attributes to decide when to call tools. Good descriptions are critical:

```csharp
// GOOD — specific, explains when to use, describes parameters
[Description("Searches the product catalog by name or category. Returns up to 20 matching products with prices.")]
public async Task<string> SearchProducts(
    [Description("Search query — can be a product name, category, or keyword")] string query,
    [Description("Maximum number of results (1-50, default 20)")] int limit = 20)

// BAD — vague, unhelpful to the LLM
[Description("Search")]
public async Task<string> Search(string q, int n = 20)
```

Rules for tool methods:
- Use `[Description]` on both the method and each parameter
- Method name should be descriptive (the LLM sees it)
- Parameter names should be clear (the LLM sees them)
- Return `string` or `Task<string>` for simplicity — the LLM receives the return value as text
- Complex return types are JSON-serialized automatically

---

## Part 5: Inter-Agent Communication

Agents communicate through `IFabrCoreAgentHost`, available as `fabrcoreAgentHost` in the base class.

### Request/Response (synchronous delegation)
```csharp
var request = new AgentMessage
{
    ToHandle = "specialist-agent",
    Message = "Analyze this data: ...",
    Channel = "analysis"
};

var response = await fabrcoreAgentHost.SendAndReceiveMessage(request);
var analysisResult = response.Message;
```

### Fire-and-Forget (one-way message)
```csharp
var notification = new AgentMessage
{
    ToHandle = "logger-agent",
    Message = "Task completed successfully",
    MessageType = "task-complete",
    Kind = MessageKind.OneWay
};

await fabrcoreAgentHost.SendMessage(notification);
```

### Events (stream-based, fire-and-forget)
```csharp
var eventMsg = new EventMessage
{
    Type = "anomaly",
    Channel = "monitor-agent",
    Data = "Anomaly detected"
};

await fabrcoreAgentHost.SendEvent(eventMsg);
```

### Progress Updates During Long Operations
```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    // Send progress updates back to the caller
    await fabrcoreAgentHost.SendMessage(new AgentMessage
    {
        ToHandle = message.FromHandle,
        Message = "Starting analysis...",
        Kind = MessageKind.OneWay
    });

    // Do work...

    await fabrcoreAgentHost.SendMessage(new AgentMessage
    {
        ToHandle = message.FromHandle,
        Message = "Analysis 50% complete...",
        Kind = MessageKind.OneWay
    });

    // Final response
    var response = message.Response();
    response.Message = "Analysis complete: ...";
    return response;
}
```

---

## Part 6: Custom State Persistence

Agents can persist custom state across conversations using the built-in state API. State is backed by Orleans grain storage — it survives grain deactivation and silo restarts.

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

State is automatically flushed after `OnMessage` completes and on grain deactivation. Call `FlushStateAsync()` explicitly if you need durability mid-operation.

---

## Part 7: Timers and Reminders

### Timers (non-persistent, lost on grain deactivation)
```csharp
// In OnInitialize or OnMessage:
fabrcoreAgentHost.RegisterTimer(
    timerName: "health-check",
    messageType: "timer:health-check",
    message: null,
    dueTime: TimeSpan.FromMinutes(1),
    period: TimeSpan.FromMinutes(5));

// Handle in OnMessage — timer fires come as regular messages
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    if (message.MessageType == "timer:health-check")
    {
        // Perform health check logic
        var response = message.Response();
        response.Message = "Health check complete";
        return response;
    }

    // Normal message processing...
}

// Unregister when done
fabrcoreAgentHost.UnregisterTimer("health-check");
```

### Reminders (persistent, survive restarts, minimum 1-minute period)
```csharp
await fabrcoreAgentHost.RegisterReminder(
    reminderName: "daily-report",
    messageType: "reminder:daily-report",
    message: "Generate daily summary",
    dueTime: TimeSpan.FromHours(1),
    period: TimeSpan.FromHours(24));

await fabrcoreAgentHost.UnregisterReminder("daily-report");
```

---

## Part 8: Chat History Compaction

When conversations get long, `TryCompactAsync()` summarizes older messages to stay within the context window:

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    // Safe to call every message — returns null if not needed
    var compaction = await TryCompactAsync();
    if (compaction?.WasCompacted == true)
    {
        logger.LogInformation("Compacted: {Original} → {Compacted} messages",
            compaction.OriginalMessageCount, compaction.CompactedMessageCount);
    }

    // Process message normally...
}
```

Compaction is configured via `AgentConfiguration.Args`:
- `CompactionEnabled` — `"true"` (default) or `"false"`
- `CompactionThreshold` — fraction of context window that triggers compaction (default `"0.75"`)
- `CompactionKeepLastN` — number of recent messages always preserved (default `"20"`)
- `CompactionMaxContextTokens` — override max tokens (defaults to model config's `ContextWindowTokens`)

---

## Part 9: Health Monitoring

Override `GetHealth` to report custom metrics:

```csharp
public override Task<ProxyHealthStatus> GetHealth(HealthDetailLevel detailLevel)
{
    return Task.FromResult(new ProxyHealthStatus
    {
        State = _isReady ? HealthState.Healthy : HealthState.Degraded,
        IsInitialized = true,
        ProxyTypeName = nameof(MyAgent),
        Message = _isReady ? "Agent is ready" : "Warming up",
        CustomMetrics = detailLevel >= HealthDetailLevel.Detailed
            ? new Dictionary<string, string>
            {
                ["QueueDepth"] = _queueDepth.ToString(),
                ["CacheHitRate"] = $"{_cacheHitRate:P1}"
            }
            : null
    });
}
```

---

## Part 10: Embeddings and Memory

### IEmbeddings — Vector Embedding Service

`IEmbeddings` is registered automatically by `AddFabrCoreServer()` as a singleton. It generates vector embeddings using the model configured as `"embeddings"` in `fabrcore.json`. Agents and plugins can inject it from DI.

```csharp
public interface IEmbeddings
{
    Task<Embedding<float>> GetEmbeddings(string text);
    Task<IReadOnlyList<Embedding<float>>> GetBatchEmbeddings(IReadOnlyList<string> texts);
}
```

**Using in a plugin:**
```csharp
[PluginAlias("semantic-search")]
public class SemanticSearchPlugin : IFabrCorePlugin
{
    private IEmbeddings _embeddings = default!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _embeddings = serviceProvider.GetRequiredService<IEmbeddings>();
        return Task.CompletedTask;
    }

    [Description("Generate a vector embedding for the given text")]
    public async Task<string> Embed([Description("Text to embed")] string text)
    {
        var result = await _embeddings.GetEmbeddings(text);
        return $"Generated {result.Vector.Length}-dimensional embedding";
    }
}
```

**Using directly in an agent's OnInitialize:**
```csharp
var embeddings = serviceProvider.GetRequiredService<IEmbeddings>();
var embedding = await embeddings.GetEmbeddings("sample text");
// embedding.Vector is ReadOnlyMemory<float>
```

**Required `fabrcore.json` entry:**
```json
{
  "ModelConfigurations": [
    {
      "Name": "embeddings",
      "Provider": "OpenAI",
      "Model": "text-embedding-3-small",
      "ApiKeyAlias": "openai-key"
    }
  ]
}
```

---

## Example AgentConfiguration

Show this to the user after generating code, customized for what they built:

```csharp
var config = new AgentConfiguration
{
    Handle = "<kebab-case-alias>",
    AgentType = "<kebab-case-alias>",    // Matches [AgentAlias] value
    Models = "default",                   // References fabrcore.json ModelConfigurations.Name
    SystemPrompt = "<Description of agent behavior and personality>",
    Plugins = ["<PluginAlias>"],          // [PluginAlias] names to load
    Tools = ["<ToolAlias>"],             // [ToolAlias] names to load
    McpServers =
    [
        new McpServerConfig
        {
            Name = "example-mcp",
            TransportType = McpTransportType.Stdio,
            Command = "npx",
            Arguments = ["-y", "@example/mcp-server"]
        }
    ],
    Args = new()
    {
        // Plugin settings use "PluginAlias:Key" convention
        ["MyPlugin:ApiKey"] = "your-key-here",
        // Compaction settings
        ["CompactionEnabled"] = "true",
        ["CompactionThreshold"] = "0.75"
    }
};
```

---

## After Generation

1. Detect the namespace from the project file or existing files in the directory.
2. Run `dotnet build` to verify the file compiles.
3. Remind the user:
   - **For agents:** Add this agent's assembly to the server's `FabrCoreServerOptions.AdditionalAssemblies` if it's in a separate project. The `[AgentAlias]` value is what you use as `AgentType` when creating the agent via the REST API or `ClientContext`.
   - **For plugins:** Add the `[PluginAlias]` value to the agent's `Plugins` list in `AgentConfiguration`. Plugin settings go in `Args` using the `PluginAlias:Key` convention.
   - **For tools:** Add the `[ToolAlias]` value to the agent's `Tools` list in `AgentConfiguration`.
   - Update `fabrcore.json` to include the model configuration referenced by the agent (defaults to `"default"`).
   - Reference: [FabrCore Documentation](https://fabrcore.ai/docs)

---

## Important Constraints

- **Never share tool instances across agents** — each agent must have its own tool instances due to Orleans' single-threaded actor model.
- **Don't call tools directly from other agents** — use `fabrcoreAgentHost.SendAndReceiveMessage()` for inter-agent communication.
- **Use the `serviceProvider` parameter passed to `InitializeAsync`** in plugins — not a cached one from construction.
- **Constructor must match exactly:** `(AgentConfiguration config, IServiceProvider serviceProvider, IFabrCoreAgentHost fabrcoreAgentHost)` — Orleans instantiates agents via DI.
- **Agents are single-threaded** — no need for locks or concurrent collections within an agent.
- **Chat history is auto-flushed** after `OnMessage` completes and on grain deactivation — no manual flush needed for chat history.
- **Custom state requires explicit flush** — call `FlushStateAsync()` if you need durability before `OnMessage` returns.
