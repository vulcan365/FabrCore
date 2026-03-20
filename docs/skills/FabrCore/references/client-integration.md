# Client Integration (FabrCore.Client)

## Overview

FabrCore.Client provides Blazor Server components and Orleans client connectivity for building interactive AI agent UIs.

**Critical:** FabrCore.Client requires Blazor **Interactive Server** render mode. WebAssembly and static SSR are not supported because the Orleans client needs a persistent SignalR connection.

## Minimal Client Setup

### Program.cs

```csharp
using FabrCore.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.AddFabrCoreClient();
builder.Services.AddFabrCoreClientComponents();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.UseFabrCoreClient(); // Returns IHost, NOT awaitable
app.Run();
```

### Project File

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FabrCore.Client" Version="*" />
  </ItemGroup>
</Project>
```

### appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  },
  "Orleans": {
    "ClusterId": "dev",
    "ServiceId": "fabrcore",
    "ClusteringMode": "Localhost"
  },
  "FabrCoreHostUrl": "http://localhost:5000"
}
```

**Important:** The `Orleans` section must match the server's clustering configuration exactly (same ClusterId, ServiceId, and ClusteringMode).

## ChatDock Component

The `ChatDock` is a drop-in chat component that handles agent communication, message rendering, and health monitoring.

### Basic Usage

```razor
@using FabrCore.Client.Components

<ChatDock UserHandle="user1"
          AgentHandle="assistant"
          AgentType="my-agent"
          SystemPrompt="You are a helpful assistant."
          Title="Assistant" />
```

### All Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `UserHandle` | `string` | Yes | Unique identifier for the user |
| `AgentHandle` | `string` | No | Agent instance handle (auto-generated if omitted) |
| `AgentType` | `string` | No | Agent type matching `[AgentAlias]` |
| `SystemPrompt` | `string` | No | System instructions for the agent |
| `Title` | `string` | No | Display title for the chat panel |
| `Icon` | `string` | No | Icon class for the chat toggle button |
| `Position` | `ChatDockPosition` | No | UI position (BottomRight default) |
| `AdditionalArgs` | `Dictionary<string,string>` | No | Additional args for AgentConfiguration |
| `Plugins` | `List<string>` | No | Plugin names to enable |
| `Tools` | `List<string>` | No | Standalone tool names to enable |
| `OnMessageReceived` | `EventCallback<AgentMessage>` | No | Callback when agent responds |
| `OnMessageSent` | `EventCallback<AgentMessage>` | No | Callback when user sends |

### Positions

```csharp
public enum ChatDockPosition
{
    BottomLeft,
    BottomRight,
    Left,
    Right
}
```

### Multiple ChatDocks

Use `ChatDockManager` (registered via `AddFabrCoreClientComponents()`) to coordinate multiple chat instances:

```razor
<CascadingValue Value="chatDockManager">
    <ChatDock UserHandle="user1" AgentHandle="coding-agent"
              AgentType="code-reviewer" Title="Code Review" />
    <ChatDock UserHandle="user1" AgentHandle="writing-agent"
              AgentType="writer" Title="Writing Help" />
</CascadingValue>

@code {
    [Inject] ChatDockManager chatDockManager { get; set; } = default!;
}
```

### ChatDock Features

- **Markdown rendering** — Uses Markdig for rich message display
- **Thinking indicators** — Shows when agent is processing
- **Health status** — Displays agent health (Healthy/Degraded/Unhealthy/NotConfigured)
- **Lazy loading** — Initializes only when first opened
- **Unread badges** — Shows unread message count when minimized
- **Keyboard shortcuts** — Enter to send, Shift+Enter for newline

## ClientContext API

For custom UI that doesn't use ChatDock, use `IClientContextFactory` directly:

### Creating a Context

`IClientContextFactory` provides two methods:

| Method | Behavior | Use Case |
|--------|----------|----------|
| `GetOrCreateAsync(handle)` | Returns cached context or creates new. Factory manages lifecycle. | Blazor Server components, long-lived pages |
| `CreateAsync(handle)` | New context each call. Caller manages disposal. | Short-lived / scoped usage |

```csharp
@inject IClientContextFactory ContextFactory

@code {
    private IClientContext? _context;

    protected override async Task OnInitializedAsync()
    {
        // Recommended for Blazor Server — cached, factory-managed lifecycle
        _context = await ContextFactory.GetOrCreateAsync("user1");
        _context.AgentMessageReceived += OnAgentMessage;

        // Alternative: new context each time (caller must dispose)
        // await using var scopedContext = await ContextFactory.CreateAsync("user1");
    }

    private void OnAgentMessage(object? sender, AgentMessage message)
    {
        // Handle incoming agent messages
        InvokeAsync(StateHasChanged);
    }
}
```

### Creating Agents

`CreateAgent` takes an `AgentConfiguration` object (not individual named parameters):

```csharp
await _context.CreateAgent(new AgentConfiguration
{
    Handle = "my-agent",
    AgentType = "my-agent",
    SystemPrompt = "You are helpful.",
    Plugins = ["weather"],
    Tools = ["calculate"],
    Args = new Dictionary<string, string>
    {
        ["weather:ApiKey"] = "abc123"
    }
});
```

### Sending Messages

All messaging methods take `AgentMessage` — there are no `(string, string)` convenience overloads.

**FromHandle auto-fill:** `ClientContext` automatically sets `FromHandle` to the client's handle on all outgoing messages (`SendMessage`, `SendAndReceiveMessage`, `SendEvent`) if not already set. You do **not** need to set `FromHandle` manually — the framework handles routing back to the client.

| Method | Behavior | Preferred For |
|--------|----------|---------------|
| `SendMessage` | Fire-and-forget. Response arrives via `AgentMessageReceived` event. | **Client-to-agent** (recommended) |
| `SendAndReceiveMessage` | Async RPC. Blocks until agent responds. | **Agent-to-agent** |

```csharp
// SendMessage — fire-and-forget (preferred for client-to-agent)
// Response arrives asynchronously via AgentMessageReceived event
await _context.SendMessage(new AgentMessage
{
    ToHandle = "my-agent",
    Message = "What's the weather?"
});

// SendAndReceiveMessage — async RPC (preferred for agent-to-agent)
// Can be used from client but blocks the calling thread until response
var request = new AgentMessage
{
    ToHandle = "my-agent",
    Message = "What's the weather?"
};
var response = await _context.SendAndReceiveMessage(request);

// Event (optional streamName parameter)
await _context.SendEvent(new AgentMessage
{
    ToHandle = "my-agent",
    MessageType = "status-update",
    Message = "User logged in"
});
```

### Health Monitoring

```csharp
var health = await _context.GetAgentHealth("my-agent");

switch (health.State)
{
    case HealthState.Healthy:
        // Agent is working normally
        break;
    case HealthState.Degraded:
        // Agent has issues but is functional
        break;
    case HealthState.Unhealthy:
        // Agent is not functioning
        break;
    case HealthState.NotConfigured:
        // Agent hasn't been configured yet
        break;
}
```

### Agent Tracking

```csharp
// Check if agent is tracked
bool isTracked = await _context.IsAgentTracked("my-agent");

// Get all tracked agents
var agents = await _context.GetTrackedAgents();
```

## DirectMessageSender

For simple fire-and-forget messaging without a full ClientContext.

**Important:** `DirectMessageSender` requires fully-qualified handles (`"owner:agent"`). It will throw if passed a bare alias. Use `ClientContext` if you need auto-resolution.

```csharp
@inject IDirectMessageSender DirectSender

// Must use fully-qualified handle — bare aliases are rejected
await DirectSender.SendAsync("user1", "user1:agent-handle", new AgentMessage
{
    Message = "Process this in the background",
    MessageKind = MessageKind.OneWay
});
```

## FabrCoreHostApiClient

REST client for the Host API (useful for non-Orleans operations):

```csharp
@inject FabrCoreHostApiClient ApiClient

// Get agent health via REST
var health = await ApiClient.GetAgentHealthAsync("user1", "my-agent");

// Discovery
var agentTypes = await ApiClient.GetAgentTypesAsync();
var plugins = await ApiClient.GetPluginsAsync();
```

## Connection Resilience

FabrCore.Client includes automatic retry with exponential backoff:
- `ConnectionRetryCount` — Max retries (default: 5)
- `ConnectionRetryDelay` — Base delay between retries (default: 3 seconds)
- `GatewayListRefreshPeriod` — How often to refresh the gateway list

Configure in `appsettings.json`:
```json
{
  "Orleans": {
    "ConnectionRetryCount": 10,
    "ConnectionRetryDelay": "00:00:05",
    "GatewayListRefreshPeriod": "00:00:30"
  }
}
```

## App.razor Template

```razor
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <HeadOutlet @rendermode="InteractiveServer" />
</head>
<body>
    <Routes @rendermode="InteractiveServer" />
    <script src="_framework/blazor.server.js"></script>
</body>
</html>
```

**Critical:** Both `<HeadOutlet>` and `<Routes>` must have `@rendermode="InteractiveServer"` for Orleans client connectivity.
