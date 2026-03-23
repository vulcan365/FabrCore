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

The `ChatDock` is a **floating icon button** that expands into a chat panel overlay. When collapsed, it shows a small circular icon (36x36px). When clicked, a chat panel slides in from the configured position. The panel is moved to `document.body` via JS to escape CSS stacking contexts.

**Visual states:**
- **Collapsed**: Circular icon with Bootstrap icon class. Color indicates state: blue (connected), green pulsing (open), orange pulsing (unread messages), gray (lazy/not loaded).
- **Expanded**: Full chat panel with header (title + clear/minimize buttons), scrollable message area with markdown rendering, thinking/typing indicators, and input area with send button.

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

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `UserHandle` | `string` | Yes | — | User/client identifier |
| `AgentHandle` | `string` | Yes | — | Agent instance handle (unique per agent) |
| `AgentType` | `string` | Yes | — | Agent type matching `[AgentAlias]` |
| `SystemPrompt` | `string` | No | "You are a helpful AI assistant." | System instructions |
| `Title` | `string` | No | "Assistant" | Display title in panel header |
| `Icon` | `string` | No | "bi bi-chat-dots" | Bootstrap icon class for the floating button |
| `WelcomeMessage` | `string` | No | "How can I help you today?" | Empty state message |
| `Tooltip` | `string` | No | "Open chat" | Hover tooltip on icon |
| `Position` | `ChatDockPosition` | No | `BottomRight` | Panel position (see below) |
| `AdditionalArgs` | `Dictionary<string,string>` | No | null | Extra args for AgentConfiguration |
| `LazyLoad` | `bool` | No | false | Defer agent creation until first expand |
| `Plugins` | `List<string>` | No | null | Plugin aliases to enable |
| `Tools` | `List<string>` | No | null | Standalone tool aliases to enable |
| `OnMessageReceived` | `Func<AgentMessage, Task<bool>>` | No | null | Callback when agent responds. Return `true` to display the message, `false` to suppress it. |
| `OnMessageSent` | `EventCallback<string>` | No | — | Callback when user sends a message |

### Positions

```csharp
public enum ChatDockPosition
{
    BottomRight,    // Floating icon bottom-right, panel slides up (default)
    BottomLeft,     // Floating icon bottom-left, panel slides up
    Right,          // Floating icon right edge, panel slides in from right (full height)
    Left            // Floating icon left edge, panel slides in from left (full height)
}
```

**Responsive:** On mobile (<480px), the panel expands to full width regardless of position.

**CSS customization** via variables:
```css
--chat-dock-primary: #3b82f6;
--chat-dock-width: 380px;
--chat-dock-icon-size: 36px;
```

### Agent Lifecycle

ChatDock manages the full agent lifecycle internally:

1. **Connect**: Gets or creates `IClientContext` via `IClientContextFactory`, subscribes to `AgentMessageReceived`
2. **Check Existing Agent**: Calls `IsAgentTracked()` then `GetAgentHealth()` to check if the agent is already configured. For cross-owner agents (handle contains `:`), ChatDock always calls `GetAgentHealth()` directly — the agent won't be in the user's tracked list, but `GetAgentHealth` goes straight to the grain to check the real state.
3. **Create Agent (if needed)**: Only calls `context.CreateAgent(agentConfig)` if `IsConfigured == false` **and the agent is owned by the current user** (bare alias handle). For cross-owner agents, ChatDock will not attempt creation — it displays an error if the agent is not configured. Shared/system agents must be created server-side. See [acl-shared-agents.md](acl-shared-agents.md).
4. **Send Messages**: Uses `context.SendMessage()` (fire-and-forget). Responses arrive via the `AgentMessageReceived` event.
5. **Message Filtering**: Filters by `FromHandle` (must match expected agent) and `ToHandle` (must be UserHandle). For cross-owner agents, `FromHandle` is matched using the full handle as-is (e.g., `"system:automation_agent-123"`). System messages (`_status`, `_error`) are handled internally.
6. **Cleanup**: `IDisposable` — unregisters from DockManager, unsubscribes events, disposes context

### Multiple ChatDocks

Use `ChatDockManager` (registered via `AddFabrCoreClientComponents()`) to coordinate multiple chat instances — **only one can be expanded at a time**:

```razor
<CascadingValue Value="chatDockManager">
    <ChatDock UserHandle="user1" AgentHandle="coding-agent"
              AgentType="code-reviewer" Title="Code Review"
              Position="ChatDockPosition.BottomRight" />
    <ChatDock UserHandle="user1" AgentHandle="writing-agent"
              AgentType="writer" Title="Writing Help"
              Position="ChatDockPosition.BottomLeft" />
</CascadingValue>

@code {
    [Inject] ChatDockManager chatDockManager { get; set; } = default!;
}
```

### ChatDock Features

- **Floating icon** — Small circular button, always visible, toggles the chat panel
- **Markdown rendering** — Uses Markdig for rich message display
- **Thinking indicators** — Shows when agent is processing (auto-fades after 5s)
- **Typing indicators** — Bouncing dots animation while waiting for response
- **Health status** — Displays agent health (Healthy/Degraded/Unhealthy/NotConfigured)
- **Lazy loading** — Defer agent creation until first opened (`LazyLoad="true"`)
- **Unread badges** — Shows unread message count when minimized
- **Keyboard shortcuts** — Enter to send, Shift+Enter for newline
- **Panel escape** — Panel moved to `document.body` via JS to avoid stacking context issues

### Static Assets

ChatDock requires FabrCore.Client static assets. Include in your layout or `_Imports.razor`:

```html
<!-- In App.razor or _Host.cshtml -->
<link href="_content/FabrCore.Client/fabrcore.css" rel="stylesheet" />
```

The JS module (`fabrcore.js`) is loaded dynamically by the component.

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

// Event (optional streamName parameter) — uses EventMessage, not AgentMessage
await _context.SendEvent(new EventMessage
{
    Channel = "my-agent",
    Type = "status-update",
    Data = "User logged in"
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
