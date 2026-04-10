---
name: fabrcore-client
description: >
  Set up FabrCore Blazor Server clients — AddFabrCoreClient, ClientContext, ClientContextFactory,
  manual messaging, agent tracking, health monitoring, DirectMessageSender, and FabrCoreHostApiClient.
  Triggers on: "FabrCore client", "AddFabrCoreClient", "UseFabrCoreClient", "AddFabrCoreClientComponents",
  "ClientContext", "ClientContextFactory", "IClientContext", "IClientContextFactory",
  "Blazor agent", "DirectMessageSender", "FabrCoreHostApiClient", "AgentMessageReceived",
  "CreateAgent", "GetAgentHealth", "IsAgentTracked", "GetTrackedAgents", "GetAccessibleSharedAgents",
  "client setup", "Blazor Server agent".
  Do NOT use for: ChatDock component — use fabrcore-chatdock.
  Do NOT use for: Orleans configuration — use fabrcore-orleans.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Client Setup

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

**CRITICAL:** `UseFabrCoreClient()` returns `IHost`, NOT `Task`. Do NOT use `await`.

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
  "Orleans": {
    "ClusterId": "dev",
    "ServiceId": "fabrcore",
    "ClusteringMode": "Localhost"
  },
  "FabrCoreHostUrl": "http://localhost:5000"
}
```

**Important:** The `Orleans` section must match the server's clustering configuration exactly.

### App.razor Template

```razor
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <link href="_content/FabrCore.Client/fabrcore.css" rel="stylesheet" />
    <HeadOutlet @rendermode="InteractiveServer" />
</head>
<body>
    <Routes @rendermode="InteractiveServer" />
    <script src="_framework/blazor.server.js"></script>
</body>
</html>
```

**Critical:** Both `<HeadOutlet>` and `<Routes>` must have `@rendermode="InteractiveServer"`.

## IClientContextFactory

Two methods for creating client contexts:

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
        _context = await ContextFactory.GetOrCreateAsync("user1");
        _context.AgentMessageReceived += OnAgentMessage;
    }

    private void OnAgentMessage(object? sender, AgentMessage message)
    {
        InvokeAsync(StateHasChanged);
    }
}
```

### Full IClientContextFactory Interface

```csharp
public interface IClientContextFactory
{
    Task<IClientContext> GetOrCreateAsync(string handle);
    Task<IClientContext> CreateAsync(string handle);
    Task ReleaseAsync(string handle);
    bool HasContext(string handle);
}
```

## IClientContext API

### Creating Agents

`CreateAgent` takes an `AgentConfiguration` object (NOT individual named parameters):

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

**FromHandle auto-fill:** `ClientContext` automatically sets `FromHandle` to the client's handle on all outgoing messages.

| Method | Behavior | Preferred For |
|--------|----------|---------------|
| `SendMessage` | Fire-and-forget. Response via `AgentMessageReceived` event. | **Client-to-agent** (recommended) |
| `SendAndReceiveMessage` | Async RPC. Blocks until agent responds. | **Agent-to-agent** |

```csharp
// SendMessage — fire-and-forget (preferred for client-to-agent)
await _context.SendMessage(new AgentMessage
{
    ToHandle = "my-agent",
    Message = "What's the weather?"
});

// SendAndReceiveMessage — async RPC
var response = await _context.SendAndReceiveMessage(new AgentMessage
{
    ToHandle = "my-agent",
    Message = "What's the weather?"
});

// SendEvent
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
    case HealthState.Healthy: break;
    case HealthState.Degraded: break;
    case HealthState.Unhealthy: break;
    case HealthState.NotConfigured: break;
}
```

### Agent Tracking

```csharp
bool isTracked = await _context.IsAgentTracked("my-agent");
var agents = await _context.GetTrackedAgents();
```

### Discovering Shared Agents

```csharp
List<AgentInfo> sharedAgents = await _context.GetAccessibleSharedAgents();
```

### Full IClientContext Interface

```csharp
public interface IClientContext
{
    string Handle { get; }
    bool IsDisposed { get; }
    event EventHandler<AgentMessage>? AgentMessageReceived;

    Task<AgentMessage> SendAndReceiveMessage(AgentMessage request);
    Task SendMessage(AgentMessage request);
    Task SendEvent(EventMessage request, string? streamName = null);
    Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration);
    Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);
    Task<List<TrackedAgentInfo>> GetTrackedAgents();
    Task<bool> IsAgentTracked(string handle);
    Task<List<AgentInfo>> GetAccessibleSharedAgents();
}
```

## DirectMessageSender

For simple fire-and-forget messaging without a full ClientContext.

**Important:** Requires fully-qualified handles (`"owner:agent"`). Throws if passed a bare alias.

```csharp
@inject IDirectMessageSender DirectSender

await DirectSender.SendAsync("user1", "user1:agent-handle", new AgentMessage
{
    Message = "Process this in the background",
    Kind = MessageKind.OneWay
});
```

## FabrCoreHostApiClient

REST client for the Host API. The base URL is read from the `FabrCoreHostUrl` configuration key (see appsettings.json above; defaults to `http://localhost:5000` if missing).

Agent-scoped methods take a **fully-qualified handle** in the form `"owner:alias"`. The client parses the owner out of the handle via `HandleUtilities.ParseHandle` and sends it as the `x-user` header automatically — callers do not pass the user id separately. Bare aliases are rejected with `ArgumentException`.

```csharp
@inject IFabrCoreHostApiClient ApiClient

// Health — handle is full "owner:alias"
var health = await ApiClient.GetAgentHealthAsync("user1:my-agent");

// Chat
var reply = await ApiClient.ChatAsync("user1:my-agent", "hello");

// Event
await ApiClient.SendEventAsync("user1:my-agent", new EventMessage
{
    Type = "order.created",
    Source = "user1:orders",
    Channel = "user1:my-agent"
});

// Batch create — every config.Handle must be "owner:alias" and all
// configs in the batch must share the same owner.
var created = await ApiClient.CreateAgentsAsync(new List<AgentConfiguration>
{
    new() { Handle = "user1:assistant", AgentType = "ChatAgent", Models = "gpt-4o-mini" },
    new() { Handle = "user1:summarizer", AgentType = "ChatAgent", Models = "gpt-4o-mini" }
});
```

Under the hood, for `GetAgentHealthAsync("user1:my-agent")` the client issues `GET {FabrCoreHostUrl}/fabrcoreapi/Agent/health/my-agent` with header `x-user: user1`. You never have to split the handle yourself.

## Extension Methods

```csharp
// Client setup
public static IHostApplicationBuilder AddFabrCoreClient(this IHostApplicationBuilder builder)

// Client startup — returns IHost, NOT awaitable
public static IHost UseFabrCoreClient(this IHost app)

// Register ChatDock and UI components
public static IServiceCollection AddFabrCoreClientComponents(this IServiceCollection services)
```

## Troubleshooting

**ChatDock not connecting:**
1. Ensure Blazor Interactive Server render mode (not WebAssembly or static SSR)
2. Verify Orleans clustering settings match between server and client
3. Check `FabrCoreHostUrl` in client appsettings
