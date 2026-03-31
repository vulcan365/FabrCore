---
name: fabrcore-plugins-tools
description: >
  Build FabrCore plugins and standalone tools — IFabrCorePlugin, PluginAlias, ToolAlias, tool calling patterns,
  plugin settings, DI access, disposable resources, and inter-agent communication from plugins.
  Triggers on: "build plugin", "create plugin", "IFabrCorePlugin", "PluginAlias", "standalone tool", "ToolAlias",
  "tool calling", "AIFunctionFactory", "add tools to agent", "[Description]", "plugin settings",
  "GetPluginSetting", "plugin DI", "tool description", "tool method".
  Do NOT use for: MCP integration — use fabrcore-mcp.
  Do NOT use for: agent lifecycle — use fabrcore-agent.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Plugins and Standalone Tools

## When to Use a Plugin vs. a Standalone Tool

| Aspect | Plugin | Standalone Tool |
|--------|--------|-----------------|
| Implementation | Class with `IFabrCorePlugin` | Static method |
| State | Yes — holds injected services, config, connections | No — pure function |
| DI Support | `IServiceProvider` in `InitializeAsync` | None (static context) |
| Lifecycle | `InitializeAsync` called once per agent | No lifecycle |
| Discovery | `[PluginAlias("name")]` on class | `[ToolAlias("name")]` on static method |
| Use Cases | API integrations, DB access, HTTP clients, stateful workflows | String formatting, math, date/time, simple conversions |

## Building a Plugin

Plugins are stateful tool collections that implement `IFabrCorePlugin`.

### Naming Convention
- **Class name:** PascalCase ending in `Plugin` (e.g., `GitHubPlugin`)
- **Plugin alias:** kebab-case or PascalCase (e.g., `"github"`) — used in `[PluginAlias]` and `AgentConfiguration.Plugins`

### Plugin Template

```csharp
using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;

[PluginAlias("my-plugin")]
public class MyPlugin : IFabrCorePlugin
{
    private IFabrCoreAgentHost _agentHost = default!;
    private ILogger<MyPlugin> _logger = default!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<MyPlugin>>();

        // Read plugin-specific configuration
        var apiKey = config.GetPluginSetting("my-plugin", "ApiKey");
        var timeout = config.GetPluginSetting("my-plugin", "Timeout", "30");

        return Task.CompletedTask;
    }

    [Description("Short, clear description of what this tool does")]
    public async Task<string> MyTool(
        [Description("Parameter description")] string input,
        [Description("Optional limit")] int limit = 10)
    {
        _logger.LogInformation("MyTool called with input: {Input}", input);
        return "result";
    }
}
```

### Plugin Lifecycle

1. **Discovery** — `FabrCoreRegistry` scans assemblies for `[PluginAlias]` at startup
2. **Resolution** — `FabrCoreToolRegistry.ResolvePluginToolsAsync()` instantiates the plugin
3. **Initialization** — `InitializeAsync(config, serviceProvider)` called with agent's config
4. **Tool Extraction** — Public methods with `[Description]` become `AITool` instances
5. **Disposal** — If plugin implements `IDisposable` or `IAsyncDisposable`, disposed on agent deactivation

### Plugin Settings Convention

Pass settings via `AgentConfiguration.Args` using `"PluginAlias:Key"` format:

```json
{
  "Handle": "my-agent",
  "Plugins": ["my-plugin"],
  "Args": {
    "my-plugin:ApiKey": "abc123",
    "my-plugin:Timeout": "60",
    "my-plugin:MaxResults": "50"
  }
}
```

Read in the plugin:
```csharp
var apiKey = config.GetPluginSetting("my-plugin", "ApiKey");
var allSettings = config.GetPluginSettings("my-plugin");
// allSettings = { "ApiKey": "abc123", "Timeout": "60", "MaxResults": "50" }
```

### Plugin DI Access

The `IServiceProvider` in `InitializeAsync` includes:
- `IFabrCoreAgentHost` — For inter-agent communication and handle access
- All services registered in the server's DI container
- `ILogger<T>` — For structured logging

### Plugin Handle Access

Plugins can access the hosting agent's handle components via `IFabrCoreAgentHost`:

```csharp
public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
{
    _agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();

    var fullHandle = _agentHost.GetHandle();        // "user123:assistant"
    var owner      = _agentHost.GetOwnerHandle();   // "user123"
    var agent      = _agentHost.GetAgentHandle();   // "assistant"
    var (o, a)     = _agentHost.GetParsedHandle();  // ("user123", "assistant")
    var hasOwner   = _agentHost.HasOwner();         // true

    return Task.CompletedTask;
}
```

### Plugin with Inter-Agent Communication

```csharp
[PluginAlias("coordinator")]
public class CoordinatorPlugin : IFabrCorePlugin
{
    private IFabrCoreAgentHost _agentHost = default!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();
        return Task.CompletedTask;
    }

    [Description("Delegates a question to a specialist agent and returns their response.")]
    public async Task<string> AskSpecialist(
        [Description("The handle of the specialist agent")] string agentHandle,
        [Description("The question to ask")] string question)
    {
        var request = new AgentMessage { Message = question };
        var response = await _agentHost.SendAndReceiveMessage(request);
        return response.Message ?? "(no response)";
    }
}
```

### Plugin with Disposable Resources

```csharp
[PluginAlias("data-access")]
public class DataAccessPlugin : IFabrCorePlugin, IAsyncDisposable
{
    private HttpClient? _httpClient;

    public async Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        var baseUrl = config.GetPluginSetting("data-access", "BaseUrl")
            ?? throw new InvalidOperationException("data-access:BaseUrl is required");
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    [Description("Fetches data from the configured API endpoint.")]
    public async Task<string> FetchData([Description("API path to query")] string path)
    {
        return await _httpClient!.GetStringAsync(path);
    }

    public async ValueTask DisposeAsync() => _httpClient?.Dispose();
}
```

## Building a Standalone Tool

For simple, stateless operations that don't need initialization or state:

```csharp
using System.ComponentModel;
using FabrCore.Sdk;

public static class UtilityTools
{
    [ToolAlias("format-json")]
    [Description("Format a JSON string with proper indentation")]
    public static string FormatJson(
        [Description("The raw JSON string to format")] string json)
    {
        var parsed = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
    }

    [ToolAlias("timestamp")]
    [Description("Get the current UTC timestamp in ISO 8601 format")]
    public static string GetTimestamp()
    {
        return DateTimeOffset.UtcNow.ToString("o");
    }
}
```

### Standalone Tool Rules
- Must be `static` methods in a `static` class
- Decorated with `[ToolAlias("name")]` and `[Description("...")]`
- Auto-discovered from assemblies at startup
- Cannot access `IFabrCoreAgentHost` or DI services (use plugins for that)
- Best for pure functions with no side effects

### Configuring Tools on Agents

```json
{
  "Plugins": ["my-plugin"],              // All tools from the plugin
  "Tools": ["format-json", "timestamp"]  // Specific standalone tools
}
```

## Tool Calling Patterns

### Three Ways to Add Tools

**1. Local tool methods** (defined in the agent class):
```csharp
public override async Task OnInitialize()
{
    var tools = new List<AITool>();
    tools.Add(AIFunctionFactory.Create(SearchDatabase));
    tools.Add(AIFunctionFactory.Create(SendEmail));
    var result = await CreateChatClientAgent("default",
        threadId: config.Handle ?? "default", tools: tools);
}

[Description("Searches the database for records matching the query.")]
private async Task<string> SearchDatabase(string query, int maxResults = 10)
{
    return $"Found {maxResults} results for '{query}'";
}
```

**2. Configured plugins and tools** (resolved from AgentConfiguration):
```csharp
var tools = await ResolveConfiguredToolsAsync();
// Optionally add local tools too
tools.Add(AIFunctionFactory.Create(MyLocalTool));
```

**3. MCP servers** (see fabrcore-mcp skill for details):
```csharp
var tools = await ResolveConfiguredToolsAsync(); // includes MCP tools
```

## Writing Effective Tool Descriptions

The LLM relies on `[Description]` attributes to decide when to call tools:

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

**Rules for tool methods:**
- Use `[Description]` on both the method and each parameter
- Method name should be descriptive (the LLM sees it)
- Return `string` or `Task<string>` for simplicity
- Complex return types are JSON-serialized automatically
- Return error messages as strings rather than throwing exceptions

## Plugin Best Practices

1. **Clear descriptions** — Each tool method needs a `[Description]` telling the LLM when and how to use it
2. **Parameter descriptions** — Use `[Description]` on parameters for complex types
3. **Return strings** — The LLM receives the return value as text
4. **Error handling** — Return error messages as strings rather than throwing
5. **Stateful is OK** — Plugins can maintain state between calls within the same agent
6. **Disposable resources** — Implement `IAsyncDisposable` for cleanup
