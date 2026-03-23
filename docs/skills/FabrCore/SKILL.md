---
name: fabrcore
description: >
  Build distributed AI agent systems with FabrCore — an open-source .NET framework powered by Orleans.
  Use when building FabrCore agents, plugins, tools, servers (Orleans silo), clients (Blazor UI),
  or complete multi-agent systems. Triggers on: "FabrCore", "agent grain", "FabrCoreAgentProxy",
  "Orleans agent", "ChatDock", "fabrcore.json", "AddFabrCoreServer", "AddFabrCoreServices", "AddFabrCore", "AddFabrCoreClient",
  "AgentConfiguration", "IFabrCorePlugin", scaffold agent/server/client/system,
  or any .NET AI agent development using the FabrCore framework.
  Do NOT use for general Orleans questions unrelated to FabrCore, or for other AI frameworks
  (LangChain, Semantic Kernel, AutoGen) unless integrating with FabrCore.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
metadata:
  author: FabrCore
  version: 1.0.0
  documentation: https://fabrcore.ai/docs
---

# FabrCore Development Skill

Build distributed AI agent systems with FabrCore — an open-source .NET 10 framework for creating, hosting, and orchestrating AI agents on Microsoft Orleans.

## Quick Reference

| Concept | Type | Key Class/Interface |
|---------|------|-------------------|
| Agent | Business logic actor | `FabrCoreAgentProxy` (extend this) |
| Plugin | Stateful tool collection | `IFabrCorePlugin` (implement this) |
| Standalone Tool | Single static method | `[ToolAlias]` attribute |
| Server/Host | Orleans silo + REST API | `AddFabrCoreServer()` (simple) or `AddFabrCoreServices()` + `AddFabrCore()` (advanced) |
| Client | Blazor UI + Orleans client | `AddFabrCoreClient()` / `UseFabrCoreClient()` |
| ChatDock | Floating icon → chat overlay | `<ChatDock>` Blazor component |
| Configuration | Agent definition | `AgentConfiguration` class |
| Messaging | Agent communication | `AgentMessage` class |

## Architecture Overview

FabrCore layers on top of Orleans (distributed actor model) and Microsoft.Extensions.AI:

```
┌─────────────────────────────────────────────┐
│  Client Layer (Blazor Server)               │
│  ChatDock, ClientContext, DirectMessage      │
├─────────────────────────────────────────────┤
│  Host Layer (Orleans Silo + ASP.NET Core)   │
│  AgentGrain, REST API, WebSocket, Streams   │
├─────────────────────────────────────────────┤
│  SDK Layer                                  │
│  FabrCoreAgentProxy, Plugins, Tools, MCP    │
├─────────────────────────────────────────────┤
│  Core Layer (Shared Contracts)              │
│  Interfaces, Models, Configuration          │
└─────────────────────────────────────────────┘
```

- **FabrCore.Core** — Interfaces (`IAgentGrain`, `IClientGrain`), models (`AgentConfiguration`, `AgentMessage`, `AgentHealthStatus`), Orleans surrogates
- **FabrCore.Sdk** — Agent base class (`FabrCoreAgentProxy`), plugin system, tool registry, chat client factory, MCP integration, compaction, state persistence
- **FabrCore.Host** — Orleans grains (`AgentGrain`, `ClientGrain`), REST API controllers, streaming, WebSocket, agent service
- **FabrCore.Client** — Blazor components (`ChatDock`), `ClientContext`/`ClientContextFactory`, Orleans client configuration

## Prerequisites

- .NET 10 SDK
- Visual Studio 2022+ (17.x) or VS Code with C# Dev Kit
- An LLM API key (Azure OpenAI, OpenAI, OpenRouter, Grok, or Gemini)

## Getting Started

### NuGet Packages

For the server (Orleans silo):
```
dotnet add package FabrCore.Host
```

For the client (Blazor UI):
```
dotnet add package FabrCore.Client
```

For agents (class library):
```
dotnet add package FabrCore.Sdk
```

Create `fabrcore.json` in the server project root with your LLM provider configuration (see `assets/fabrcore-json-openai.json` or `assets/fabrcore-json-azure.json`).

## Core Concepts

### 1. Building an Agent

Agents extend `FabrCoreAgentProxy` and override three lifecycle methods:

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

    public override async Task OnInitialize()
    {
        // Resolve plugins, standalone tools, and MCP tools from config
        var tools = await ResolveConfiguredToolsAsync();

        var result = await CreateChatClientAgent(
            "default",
            threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
            tools: tools);
        _agent = result.Agent;
        _session = result.Session;
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var chatMessage = new ChatMessage(ChatRole.User, message.Message);
        await foreach (var update in _agent!.RunStreamingAsync(
            chatMessage, _session!))
        {
            response.Message += update.Text;
        }
        return response;
    }

    public override Task OnEvent(EventMessage eventMessage)
    {
        return Task.CompletedTask;
    }
}
```

**Key lifecycle:**

| Method | When Called | Purpose |
|--------|-----------|---------|
| `OnInitialize()` | First message or reconfigure | Set up LLM client, tools, state |
| `OnMessage()` | Request or OneWay message | Process user messages, return response |
| `OnEvent()` | Event message | Handle fire-and-forget notifications |
| `OnCompaction()` | Token threshold exceeded | Custom compaction logic (default: LLM summarization) |

**Agent naming:** Use `[AgentAlias("kebab-case-name")]`. The alias is used in `AgentConfiguration.AgentType`.

For complete agent patterns (threading, state, compaction, health, timers), see `references/agents.md`.

### 2. Building a Plugin

Plugins are stateful tool collections that implement `IFabrCorePlugin`:

```csharp
using System.ComponentModel;
using FabrCore.Core;
using FabrCore.Sdk;

[PluginAlias("weather")]
public class WeatherPlugin : IFabrCorePlugin
{
    private IFabrCoreAgentHost _agentHost = default!;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _agentHost = serviceProvider.GetRequiredService<IFabrCoreAgentHost>();
        return Task.CompletedTask;
    }

    [Description("Get the current weather for a city")]
    public async Task<string> GetWeather(string city)
    {
        // Tool implementation — the LLM calls this automatically
        return $"Weather in {city}: 72F, sunny";
    }
}
```

**Plugin discovery:** Plugins are auto-discovered from assemblies at startup via `[PluginAlias]`.
**Plugin config:** Pass settings via `AgentConfiguration.Args` using `"PluginAlias:Key"` convention.
**Plugin DI:** Access `IFabrCoreAgentHost` and registered services via `IServiceProvider` in `InitializeAsync`.

For complete plugin patterns, see `references/plugins-and-tools.md`.

### 3. Building a Standalone Tool

For simple, stateless operations:

```csharp
using System.ComponentModel;
using FabrCore.Sdk;

public static class MathTools
{
    [ToolAlias("calculate")]
    [Description("Calculate a mathematical expression")]
    public static string Calculate(string expression)
    {
        // Implementation
        return result.ToString();
    }
}
```

### 4. Server Setup (Orleans Silo)

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
});

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
```

The server:
- Hosts the Orleans silo (agent grains)
- Exposes REST API at `/fabrcoreapi/`
- Provides WebSocket at `/ws`
- Auto-discovers agents, plugins, and tools from `AdditionalAssemblies`

See `references/server-setup.md` for clustering modes, persistence options, and deployment patterns.

### 5. Client Setup (Blazor Server)

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.AddRazorComponents().AddInteractiveServerComponents();
builder.AddFabrCoreClient();
builder.Services.AddFabrCoreClientComponents();

var app = builder.Build();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.UseFabrCoreClient(); // Returns IHost, NOT awaitable
app.Run();
```

**ChatDock component** — floating icon that expands into a chat panel overlay:

ChatDock renders as a small circular icon button (36x36px). Clicking it expands a chat panel that slides in from the configured position. The panel includes a header, scrollable message area with markdown rendering, thinking/typing indicators, and input area. The panel is moved to `document.body` via JS to escape CSS stacking contexts.

```razor
@using FabrCore.Client.Components

<ChatDock UserHandle="user1"
          AgentHandle="my-agent"
          AgentType="my-agent"
          SystemPrompt="You are a helpful assistant."
          Title="My Agent"
          Plugins="@(new List<string> { "weather" })"
          Position="ChatDockPosition.BottomRight" />
```

**Positions:** `BottomRight` (default), `BottomLeft`, `Right` (full-height right edge), `Left` (full-height left edge).

**Scoped ChatDock** — scope an agent to a specific context by embedding IDs in the handle and system prompt:
```razor
<ChatDock UserHandle="user1"
          AgentHandle="@($"project-{ProjectId}")"
          AgentType="project-agent"
          SystemPrompt="@($"You manage project {ProjectId}. Use this ID automatically.")"
          Title="Project Assistant"
          OnMessageSent="@(async (msg) => { await RefreshData(); StateHasChanged(); })"
          Position="ChatDockPosition.Right" />
```

**Key parameters:** `UserHandle`, `AgentHandle`, `AgentType` (required). Optional: `SystemPrompt`, `Title`, `Icon`, `WelcomeMessage`, `Tooltip`, `Position`, `Plugins` (List<string>), `Tools` (List<string>), `AdditionalArgs` (Dictionary<string,string>), `LazyLoad` (bool), `OnMessageReceived` (Func<AgentMessage, Task<bool>> — return true to display), `OnMessageSent` (EventCallback<string>).

**Multiple ChatDocks:** Use `ChatDockManager` (registered via `AddFabrCoreClientComponents()`) — only one can be expanded at a time. Wrap in `<CascadingValue Value="chatDockManager">`.

**Manual messaging** via `IClientContextFactory`:
```csharp
@inject IClientContextFactory ContextFactory

var context = await ContextFactory.GetOrCreateAsync("user1");
await context.CreateAgent("my-agent", "my-agent", "System prompt", plugins: ["weather"]);

// SendMessage — fire-and-forget, preferred for client-to-agent
// Response arrives asynchronously via AgentMessageReceived event
var request = new AgentMessage { ToHandle = "my-agent", Message = "Hello!" };
await context.SendMessage(request);
```

See `references/client-integration.md` for ChatDock parameters, ClientContext API, and health monitoring.

### 6. Configuration

**`fabrcore.json`** — LLM provider configuration (server-side):
```json
{
  "ModelConfigurations": [
    {
      "Name": "default",
      "Provider": "OpenAI",
      "Model": "gpt-4o",
      "ApiKeyAlias": "openai-key",
      "TimeoutSeconds": 120,
      "MaxOutputTokens": 16384,
      "ContextWindowTokens": 128000
    }
  ],
  "ApiKeys": [
    { "Alias": "openai-key", "Value": "sk-..." }
  ]
}
```

**Supported providers:** OpenAI, Azure, OpenRouter, Grok, Gemini (any OpenAI-compatible endpoint).

**`AgentConfiguration`** — defines an agent instance:
```json
{
  "Handle": "my-agent",
  "AgentType": "my-agent",
  "Models": ["default"],
  "SystemPrompt": "You are a helpful assistant.",
  "Plugins": ["weather"],
  "Tools": ["calculate"],
  "McpServers": [],
  "Args": {
    "weather:ApiKey": "abc123"
  }
}
```

See `references/configuration.md` for the complete configuration reference.

### 7. Handle Routing

Handles use the format `"owner:agentAlias"` (e.g., `"user1:assistant"`).

**Routing rules** (implemented by `HandleUtilities`):
- **Bare alias** (no colon, e.g., `"assistant"`) — auto-prefixed with the caller's owner. An agent owned by `user1` sending to `"assistant"` resolves to `"user1:assistant"`.
- **Fully-qualified handle** (contains colon, e.g., `"user2:assistant"`) — used as-is, enabling cross-owner routing.

```csharp
// HandleUtilities API
HandleUtilities.BuildPrefix("user1");                    // "user1:"
HandleUtilities.EnsurePrefix("assistant", "user1:");     // "user1:assistant"
HandleUtilities.EnsurePrefix("user2:assistant", "user1:"); // "user2:assistant" (unchanged)
HandleUtilities.StripPrefix("user1:assistant", "user1:"); // "assistant"
```

**Where resolution happens:**
- **AgentGrain** — `ResolveTargetHandle()` normalizes `ToHandle` in `SendAndReceiveMessage`, `SendMessage`, and `SendEvent`. Agents can use bare aliases for same-owner targets.
- **ChatDock** — Uses `HandleUtilities.EnsurePrefix` to build expected `FromHandle` for message filtering.
- **DirectMessageSender** — Requires fully-qualified handles (throws if bare alias passed). Use `ClientContext` for auto-resolution.

### 8. AgentMessage Structure

All communication uses `AgentMessage`:

```csharp
// Fields
message.FromHandle    // Sender handle (auto-filled by ClientContext and AgentGrain)
message.ToHandle      // Target handle (bare alias or fully-qualified "owner:agent")
message.Message       // Text content
message.MessageType   // Custom type identifier (see system message types below)
message.MessageKind   // Request, OneWay, or Response
message.State         // Dictionary<string, string> for metadata
message.Args          // Dictionary<string, string> for parameters
message.Data          // Dictionary<string, string> for structured data
message.Files         // Dictionary<string, byte[]> for file attachments
message.TraceId       // Correlation ID

// Create response
var response = message.Response(); // Pre-fills routing fields
```

**System Message Types (`_` prefix convention):**

All `MessageType` values starting with `_` are reserved for FabrCore internal use. They route normally through streams but clients handle them differently — not displayed as regular chat messages.

| MessageType | Constant | Description |
|-------------|----------|-------------|
| `_status` | `SystemMessageTypes.Status` | Heartbeat sent every 3 seconds while agent processes a message. Message = "Thinking.." |
| `_error` | `SystemMessageTypes.Error` | Sent when agent encounters an exception. Message = exception text |

```csharp
// Check if a message is a system message
SystemMessageTypes.IsSystemMessage(message.MessageType); // true for "_status", "_error", etc.
```

**Automatic behavior (no agent code required):**
- `AgentGrain` automatically sends `_status` heartbeats every 3 seconds while `OnMessage` runs. If the agent responds in under 3 seconds, no heartbeat is sent.
- On exception, `AgentGrain` automatically sends `_error` to the original sender before rethrowing.
- `ChatDock` automatically displays `_status` as a thinking indicator and `_error` as an error message.

### 9. Messaging Patterns

Two messaging methods are available on both `IClientContext` and `IFabrCoreAgentHost`:

| Method | Behavior | Preferred For |
|--------|----------|---------------|
| `SendMessage(AgentMessage)` | Fire-and-forget. Response arrives asynchronously via stream/observer. | **Client-to-agent** communication |
| `SendAndReceiveMessage(AgentMessage)` | Async RPC. Blocks until the agent returns a response. | **Agent-to-agent** communication |

**Client-to-agent** — Use `SendMessage` (fire-and-forget):
```csharp
// Client sends message; response arrives via AgentMessageReceived event
var request = new AgentMessage { ToHandle = "my-agent", Message = "Hello!" };
await context.SendMessage(request);

// Handle the async response in an event handler
context.AgentMessageReceived += (sender, response) => { /* process response */ };
```

**Agent-to-agent** — Use `SendAndReceiveMessage` (async RPC):
```csharp
// Inside an agent's OnMessage — waits for the target agent to respond
var request = new AgentMessage { Message = "Analyze this" };
var reply = await fabrcoreAgentHost.SendAndReceiveMessage("analyst", request);
// reply.Message contains the response

// Cross-owner: use fully-qualified handle
var reply = await fabrcoreAgentHost.SendAndReceiveMessage("user2:analyst", request);
```

**Event broadcast** — Use `SendEvent` (fire-and-forget, no response):
```csharp
await fabrcoreAgentHost.SendEvent("listener", eventMessage);
```

See `references/inter-agent-communication.md` for orchestration patterns.

### 9. MCP Integration

Configure MCP servers in `AgentConfiguration`:
```json
{
  "McpServers": [
    {
      "Name": "filesystem",
      "TransportType": "Stdio",
      "Command": "npx",
      "Arguments": ["-y", "@anthropic/mcp-filesystem"],
      "Env": { "HOME": "/tmp" }
    },
    {
      "Name": "remote-api",
      "TransportType": "Http",
      "Url": "https://api.example.com/mcp",
      "Headers": { "Authorization": "Bearer token" }
    }
  ]
}
```

MCP tools are automatically resolved and available to the agent's LLM alongside plugin and standalone tools.

See `references/mcp-integration.md` for details.

### 10. Access Control & Shared Agents

FabrCore supports shared agents — agents owned by one entity (e.g., `"system"`) accessible to multiple users via ACL rules.

**Create a system agent** (server-side):
```csharp
await agentService.ConfigureSystemAgentAsync(new AgentConfiguration
{
    Handle = "automation_agent-123",
    AgentType = "automation-agent",
    SystemPrompt = "You are an automation assistant."
});
```

**Message a system agent** (from any user's ClientContext):
```csharp
var response = await context.SendAndReceiveMessage(new AgentMessage
{
    ToHandle = "system:automation_agent-123",
    Message = "Run the report"
});
```

**Configure ACL rules** in `fabrcore.json`:
```json
{
  "Acl": {
    "Rules": [
      { "OwnerPattern": "system", "AgentPattern": "*", "CallerPattern": "*", "Permission": "Message,Read" }
    ],
    "Groups": { "admins": ["alice", "bob"] }
  }
}
```

**Custom ACL provider** — swap the default in-memory provider:
```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}.UseAclProvider<SqlAclProvider>());
```

See `references/acl-shared-agents.md` for the full ACL system, pattern matching, groups, permissions, and custom provider implementation.

### 11. Health Monitoring

Override `GetHealth()` in your agent:
```csharp
public override AgentHealthStatus GetHealth(HealthDetailLevel level)
{
    var health = base.GetHealth(level);
    // Add custom health info
    return health;
}
```

Query health via client:
```csharp
var health = await context.GetAgentHealth("my-agent");
// health.State: Healthy, Degraded, Unhealthy, NotConfigured
```

## Project Templates

Use the asset templates in `assets/` to quickly scaffold new components:
- `assets/agent-template.cs` — Agent class template
- `assets/plugin-template.cs` — Plugin class template
- `assets/tool-template.cs` — Standalone tool template
- `assets/server-program.cs` — Server Program.cs
- `assets/client-program.cs` — Client Program.cs
- `assets/fabrcore-json-openai.json` — OpenAI provider config
- `assets/fabrcore-json-azure.json` — Azure OpenAI provider config
- `assets/appsettings-server.json` — Server appsettings
- `assets/appsettings-client.json` — Client appsettings
- `assets/server-csproj.xml` — Server .csproj
- `assets/client-csproj.xml` — Client .csproj
- `assets/agents-csproj.xml` — Agents library .csproj
- `assets/chatdock-example.razor` — ChatDock usage example
- `assets/agent-chat-example.razor` — Manual messaging example

## Scaffolding a Complete System

To scaffold a full FabrCore solution (server + client + agents):

1. Create solution and projects:
```powershell
dotnet new sln -n MySystem
dotnet new web -n MySystem.Server -o src/MySystem.Server
dotnet new razorclasslib -n MySystem.Client -o src/MySystem.Client
dotnet new classlib -n MySystem.Agents -o src/MySystem.Agents
dotnet sln add src/MySystem.Server src/MySystem.Client src/MySystem.Agents
```

2. Add NuGet packages:
```powershell
dotnet add src/MySystem.Server package FabrCore.Host
dotnet add src/MySystem.Client package FabrCore.Client
dotnet add src/MySystem.Agents package FabrCore.Sdk
```

3. Add project references:
```powershell
dotnet add src/MySystem.Server reference src/MySystem.Agents
dotnet add src/MySystem.Client reference src/MySystem.Agents
```

4. Copy and customize the asset templates for each project.

## REST API Reference

Base path: `/fabrcoreapi/`

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/Agent/create` | POST | Batch create/configure agents |
| `/Agent/health/{handle}` | GET | Get agent health status |
| `/Agent/chat/{handle}` | POST | Send chat message |
| `/Agent/event/{handle}` | POST | Send event |
| `/Discovery/agents` | GET | List registered agent types |
| `/Discovery/plugins` | GET | List registered plugins |
| `/Discovery/tools` | GET | List registered tools |
| `/ModelConfig/model/{name}` | POST | Get model configuration |
| `/ModelConfig/apikey/{alias}` | GET | Get API key |

## Orleans Clustering Modes

Configure in `appsettings.json` under `"Orleans"`:

| Mode | Use Case | Persistence |
|------|----------|-------------|
| `Localhost` | Development | In-memory |
| `SqlServer` | Production (SQL Server) | ADO.NET |
| `AzureStorage` | Production (Azure) | Azure Tables/Blobs |

## Troubleshooting

**Agent not responding:**
1. Check `fabrcore.json` — ensure model name and API key are correct
2. Verify agent type matches `[AgentAlias]` value
3. Check server logs for Orleans activation errors

**ChatDock not connecting:**
1. Ensure Blazor Interactive Server render mode (not WebAssembly or static SSR)
2. Verify Orleans clustering settings match between server and client
3. Check `FabrCoreHostUrl` in client appsettings

**Plugin tools not appearing:**
1. Verify `[PluginAlias]` matches the name in `AgentConfiguration.Plugins`
2. Ensure plugin assembly is included in `AdditionalAssemblies`
3. Check that tool methods have `[Description]` attributes

**MCP server connection failed:**
1. Verify the MCP command/URL is accessible from the server
2. Check TransportType matches (Stdio vs Http)
3. Review server logs for MCP client errors

For detailed reference documentation, consult the files in `references/`.
