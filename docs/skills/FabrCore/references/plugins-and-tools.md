# Plugins and Standalone Tools

## Plugins

Plugins are stateful tool collections that implement `IFabrCorePlugin`. They are the primary way to extend agent capabilities.

### Plugin Structure

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
        // Tool implementation
        return "result";
    }

    [Description("Another tool in this plugin")]
    public async Task<string> AnotherTool(string query)
    {
        // Can communicate with other agents
        var msg = new AgentMessage
        {
            Message = query,
            MessageKind = MessageKind.Request
        };
        var reply = await _agentHost.SendAndReceiveMessage("other-agent", msg);
        return reply.Message;
    }
}
```

### Plugin Lifecycle

1. **Discovery** — `FabrCoreRegistry` scans assemblies for `[PluginAlias]` at startup
2. **Resolution** — `FabrCoreToolRegistry.ResolvePluginToolsAsync()` instantiates the plugin
3. **Initialization** — `InitializeAsync(config, serviceProvider)` called with agent's config and a plugin-scoped service provider
4. **Tool Extraction** — Public methods with `[Description]` become `AITool` instances via `AIFunctionFactory`
5. **Disposal** — If the plugin implements `IDisposable` or `IAsyncDisposable`, it is disposed when the agent deactivates

### Plugin Configuration

Pass plugin-specific settings via `AgentConfiguration.Args` using the `"PluginAlias:Key"` convention:

```json
{
  "Handle": "my-agent",
  "AgentType": "my-agent",
  "Plugins": ["my-plugin"],
  "Args": {
    "my-plugin:ApiKey": "abc123",
    "my-plugin:Timeout": "60",
    "my-plugin:MaxResults": "50"
  }
}
```

Read settings in the plugin:

```csharp
public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
{
    var apiKey = config.GetPluginSetting("my-plugin", "ApiKey");
    var timeout = config.GetPluginSetting("my-plugin", "Timeout", "30"); // with default
    return Task.CompletedTask;
}
```

### Plugin DI Access

The `IServiceProvider` passed to `InitializeAsync` is a plugin-scoped provider that includes:
- `IFabrCoreAgentHost` — For inter-agent communication and agent lifecycle access
- All services registered in the server's DI container
- `ILogger<T>` — For structured logging

### Plugin Best Practices

1. **Clear descriptions** — Each tool method needs a `[Description]` that tells the LLM exactly when and how to use it
2. **Parameter descriptions** — Use `[Description]` on parameters for complex types
3. **Return strings** — Tool methods should return `string` (or `Task<string>`). The LLM receives this as the tool result
4. **Error handling** — Return error messages as strings rather than throwing exceptions
5. **Stateful is OK** — Plugins can maintain state between tool calls within the same agent instance
6. **Disposable resources** — Implement `IAsyncDisposable` for cleanup

### Inter-Agent Communication from Plugins

Plugins can communicate with other agents through `IFabrCoreAgentHost`:

```csharp
[Description("Delegate a task to a specialist agent")]
public async Task<string> DelegateTask(string task, string targetAgent)
{
    var message = new AgentMessage
    {
        Message = task,
        MessageKind = MessageKind.Request
    };

    var reply = await _agentHost.SendAndReceiveMessage(targetAgent, message);
    return reply.Message;
}
```

## Standalone Tools

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
        return JsonSerializer.Serialize(parsed, new JsonSerializerOptions
        {
            WriteIndented = true
        });
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

In `AgentConfiguration`:
```json
{
  "Plugins": ["my-plugin"],     // Includes ALL tools from the plugin
  "Tools": ["format-json", "timestamp"]  // Include specific standalone tools
}
```

## Tool Description Guidelines

The LLM uses tool descriptions to decide when to call each tool. Write effective descriptions:

**Good:**
```csharp
[Description("Search the product catalog by name, category, or SKU. Returns up to 10 matching products with prices and availability.")]
public async Task<string> SearchProducts(
    [Description("Search query — product name, category, or SKU number")] string query,
    [Description("Maximum results to return (1-50, default 10)")] int limit = 10)
```

**Bad:**
```csharp
[Description("Search products")]  // Too vague — LLM won't know when to use it
public async Task<string> SearchProducts(string query, int limit = 10)
```

**Tips:**
- Start with the action verb: "Search", "Create", "Get", "Update", "Delete"
- Mention what inputs are expected
- Describe what the output contains
- Include constraints (limits, formats, valid values)
