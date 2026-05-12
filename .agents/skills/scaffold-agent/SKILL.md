---
name: scaffold-agent
description: Scaffold a new FabrCore server-side agent class — AI business logic that runs on Orleans grains, processes messages, calls LLMs, and uses tools.
argument-hint: [AgentName]
---

# Scaffold a FabrCore Agent

Create a new server-side agent class that extends `FabrCoreAgentProxy`. Agents are the core business logic — they run on Orleans grains, process messages, invoke LLMs, use tools/plugins, and communicate with other agents.

## Arguments

The agent name is provided as: `$ARGUMENTS`

If no name is provided, ask the user for:
1. **Agent name** (e.g., `WeatherAgent`, `OrderProcessor`, `ResearchAssistant`)
2. **Brief description** of what the agent should do

## Naming Convention

- **Class name:** PascalCase (e.g., `WeatherAgent`)
- **Agent alias:** kebab-case (e.g., `weather-agent`) — used in `[AgentAlias]` and `AgentConfiguration.AgentType`
- Convert the provided name to both forms automatically.

## What to Generate

Generate a single file `<AgentName>.cs` in the current directory.

### Agent Template

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
    private ChatClientAgent? _agent;
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

        // Resolve tools from configuration (plugins, tools, MCP servers)
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

        // Route by channel or message type if needed
        // var channel = message.Channel;
        // var messageType = message.MessageType;

        // Process the message through the LLM agent
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

        // Process events as needed (notifications, state updates, etc.)
    }

    /// <summary>
    /// Example tool method. LLMs can invoke this via function calling.
    /// Replace with your own tools or remove if not needed.
    /// </summary>
    [Description("Gets the current date and time in UTC.")]
    private string ExampleTool()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
    }
}

// Example AgentConfiguration for creating this agent:
//
// var config = new AgentConfiguration
// {
//     Handle = "<kebab-case-alias>",
//     AgentType = "<kebab-case-alias>",
//     Models = "default",
//     SystemPrompt = "<Description of agent behavior>",
//     Plugins = [],      // [PluginAlias] plugin names
//     Tools = [],        // [ToolAlias] tool names
//     McpServers = [],   // MCP server configs
//     Args = new()       // Custom key-value arguments
// };
```

## Customization Based on User Description

Based on what the user says the agent should do, customize:

1. **The `[Description]` on tool methods** — add domain-specific tools (e.g., weather lookup, database query, file processing)
2. **The `SystemPrompt` in the example config** — tailor to the agent's purpose
3. **The `OnMessage` routing** — add channel/type-based routing if the agent handles multiple message types
4. **The `OnEvent` handler** — implement if the agent subscribes to event streams
5. **Custom state** — use `GetStateAsync<T>(key)` / `SetState(key, value)` / `FlushStateAsync()` for persistent agent state

## Key APIs Available in the Base Class

Remind the user of these protected members they can use:

- **`CreateChatClientAgent(configName, threadId, tools?, configureOptions?)`** — creates an LLM-backed agent with auto-persisted chat history
- **`ResolveConfiguredToolsAsync()`** — loads plugins, tools, and MCP servers from AgentConfiguration
- **`GetChatClient(name)`** — gets a raw `IChatClient` for direct LLM calls without the agent framework
- **`TryCompactAsync()`** — compacts chat history when the context window fills up (threshold configurable via Args)
- **`GetStateAsync<T>(key)` / `SetState<T>(key, value)` / `FlushStateAsync()`** — custom persistent state (buffered, flushed together)
- **`ConnectMcpServerAsync(McpServerConfig)`** — connect to an MCP server programmatically
- **`config`** — the AgentConfiguration (Handle, AgentType, SystemPrompt, Models, Args, Plugins, Tools, McpServers)
- **`fabrcoreAgentHost`** — host access for inter-agent messaging, timers, reminders
- **`logger`** — ILogger instance

## After Generation

1. Detect the namespace from the project file or existing files in the directory.
2. Run `dotnet build` to verify the file compiles.
3. Remind the user:
   - Add this agent's assembly to the server's `FabrCoreServerOptions.AdditionalAssemblies` if it's in a separate project.
   - Update `fabrcore.json` to include the model configuration referenced by the agent (defaults to `"default"`).
   - The `[AgentAlias]` value is what you use as `AgentType` when creating the agent via `ClientContext.CreateAgent()` or the REST API.
