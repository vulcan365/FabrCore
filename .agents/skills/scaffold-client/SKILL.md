---
name: scaffold-client
description: Scaffold a new FabrCore Blazor Server client project — UI interaction layer with agent messaging, ChatDock chat component, and client-side agent proxies.
argument-hint: [ProjectName]
---

# Scaffold a FabrCore Client

Create a new FabrCore Blazor Server client project for agent interaction UI. This is where users interact with agents — sending/receiving messages, using the ChatDock component, and running client-side agent proxies on pages.

## Arguments

The project name is provided as: `$ARGUMENTS`

If no project name is provided, ask the user for one (e.g., `MyApp.Client`).

## Important Constraints

- **Must use Blazor Interactive Server render mode** (NOT WebAssembly or static SSR). FabrCore requires persistent SignalR connections for Orleans communication.
- Client connects to the FabrCore server via Orleans clustering. The `ClusterId` and `ServiceId` must match the server's configuration.

## What to Generate

Create the following files inside a `<ProjectName>/` directory:

### 1. `<ProjectName>.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FabrCore.Client" Version="*" />
  </ItemGroup>
</Project>
```

### 2. `Program.cs`

```csharp
using FabrCore.Client;

var builder = WebApplication.CreateBuilder(args);

// Add Blazor with Interactive Server rendering (required for FabrCore)
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add FabrCore client — configures Orleans client, ClientContextFactory,
// DirectMessageSender, and FabrCoreHostApiClient
builder.AddFabrCoreClient();

// Add FabrCore Blazor components (ChatDock, ChatDockManager)
builder.Services.AddFabrCoreClientComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Post-startup client configuration
((IHost)app).UseFabrCoreClient();

app.Run();
```

**Note:** `AddFabrCoreClient()` extends `IHostApplicationBuilder`. `UseFabrCoreClient()` extends `IHost`, so cast `app` to `IHost` when calling it on a `WebApplication`.

### 3. `appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Orleans": {
    "ClusteringMode": "Localhost",
    "ClusterId": "fabrcore-cluster",
    "ServiceId": "fabrcore-service"
  },
  "FabrCoreHostUrl": "http://localhost:5000"
}
```

**IMPORTANT:** The `ClusterId` and `ServiceId` must match the FabrCore server's Orleans configuration. If the server uses different values, update these to match.

### 4. `Components/App.razor`

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
    <script src="_framework/blazor.web.js"></script>
</body>
</html>
```

### 5. `Components/Routes.razor`

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)" />
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>
```

### 6. `Components/_Imports.razor`

```razor
@using System.Net.Http
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.JSInterop
@using FabrCore.Client
@using FabrCore.Client.Components
@using FabrCore.Core
```

### 7. `Components/Layout/MainLayout.razor`

```razor
@inherits LayoutComponentBase

<main>
    @Body
</main>
```

### 8. `Components/Pages/Home.razor` — ChatDock Example

This page demonstrates the ChatDock component — a pre-built chat UI that connects to an agent.

```razor
@page "/"

<h1>FabrCore Agent Chat</h1>
<p>Use the chat widget in the bottom-right corner to interact with an agent.</p>

<!-- ChatDock: pre-built chat UI component -->
<!-- UserHandle: identifies the current user session -->
<!-- AgentHandle: the agent instance name -->
<!-- AgentType: the [AgentAlias] registered agent type -->
<ChatDock UserHandle="@_userId"
          AgentHandle="assistant"
          AgentType="sample-agent"
          SystemPrompt="You are a helpful AI assistant."
          Title="AI Assistant"
          WelcomeMessage="Hello! How can I help you today?"
          Position="ChatDockPosition.BottomRight"
          LazyLoad="true" />

@code {
    // In a real app, get userId from authentication
    private string _userId = $"user-{Guid.NewGuid():N[..8]}";
}
```

### 9. `Components/Pages/AgentChat.razor` — ClientContext Example

This page demonstrates manual agent messaging using `IClientContextFactory` and `ClientContext`.

```razor
@page "/agent-chat"
@inject IClientContextFactory ClientContextFactory
@implements IAsyncDisposable

<h1>Agent Chat (Manual)</h1>

<div>
    <input @bind="_messageText" @bind:event="oninput" placeholder="Type a message..."
           @onkeydown="HandleKeyDown" disabled="@(_context == null)" />
    <button @onclick="SendMessage" disabled="@(string.IsNullOrEmpty(_messageText) || _context == null)">
        Send
    </button>
</div>

@if (_messages.Any())
{
    <div>
        @foreach (var msg in _messages)
        {
            <div><strong>@msg.From:</strong> @msg.Text</div>
        }
    </div>
}

@code {
    private IClientContext? _context;
    private string _messageText = "";
    private List<(string From, string Text)> _messages = new();

    protected override async Task OnInitializedAsync()
    {
        // Create a client context — this connects to the Orleans cluster
        _context = await ClientContextFactory.CreateAsync("manual-user");

        // Subscribe to messages from agents
        _context.AgentMessageReceived += OnAgentMessage;

        // Create an agent instance
        await _context.CreateAgent(new AgentConfiguration
        {
            Handle = "chat-agent",
            AgentType = "sample-agent",
            Models = "default",
            SystemPrompt = "You are a helpful assistant."
        });
    }

    private async Task SendMessage()
    {
        if (string.IsNullOrEmpty(_messageText) || _context == null) return;

        var request = new AgentMessage
        {
            ToHandle = "chat-agent",
            Message = _messageText,
            Channel = "chat"
        };

        _messages.Add(("You", _messageText));
        _messageText = "";

        // Send and wait for response
        var response = await _context.SendAndReceiveMessage(request);
        _messages.Add(("Agent", response.Message ?? "(no response)"));

        await InvokeAsync(StateHasChanged);
    }

    private void OnAgentMessage(object? sender, AgentMessage message)
    {
        // Handle async messages (fire-and-forget, events, thinking updates)
        _messages.Add(("Agent", message.Message ?? "(event)"));
        InvokeAsync(StateHasChanged);
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await SendMessage();
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null)
        {
            _context.AgentMessageReceived -= OnAgentMessage;
            await _context.DisposeAsync();
        }
    }
}
```

## After Generation

1. Run `dotnet build` to verify the project compiles.
2. Remind the user:
   - The **ChatDock** example on `/` shows the pre-built chat widget. Update `AgentType` to match their agent's `[AgentAlias]`.
   - The **AgentChat** example on `/agent-chat` shows manual messaging with `ClientContext`. This is useful for custom UIs.
   - The Orleans `ClusterId`/`ServiceId` must match the server. If using separate processes, both must use the same clustering config.
   - `FabrCoreClientAgentProxy<TComponent>` can be extended to create client-side agents that run on pages and react to messages.
   - Start the FabrCore server before running the client.
