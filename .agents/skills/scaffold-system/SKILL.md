---
name: scaffold-system
description: Scaffold a complete FabrCore system — solution with server (Orleans silo), client (Blazor UI), and agents library, all wired together and ready to run.
argument-hint: [SolutionName]
---

# Scaffold a Complete FabrCore System

Create a full FabrCore system with three projects wired together: a server (Orleans infrastructure), a client (Blazor UI), and an agents library (server-side business logic).

## Arguments

The solution name is provided as: `$ARGUMENTS`

If no name is provided, ask the user for one (e.g., `MyAgentSystem`).

Also ask:
1. **AI provider preference** — OpenAI, Azure OpenAI, or both (for fabrcore.json template)

## What to Generate

### Directory Structure

```
<SolutionName>/
├── <SolutionName>.sln
├── .gitignore
├── README.md
└── src/
    ├── <SolutionName>.Server/
    │   ├── <SolutionName>.Server.csproj
    │   ├── Program.cs
    │   ├── appsettings.json
    │   ├── appsettings.Development.json
    │   ├── fabrcore.json
    │   └── .gitignore
    ├── <SolutionName>.Client/
    │   ├── <SolutionName>.Client.csproj
    │   ├── Program.cs
    │   ├── appsettings.json
    │   └── Components/
    │       ├── App.razor
    │       ├── Routes.razor
    │       ├── _Imports.razor
    │       ├── Layout/
    │       │   └── MainLayout.razor
    │       └── Pages/
    │           ├── Home.razor
    │           └── AgentChat.razor
    └── <SolutionName>.Agents/
        ├── <SolutionName>.Agents.csproj
        └── SampleAgent.cs
```

### Step-by-Step Generation

Use `dotnet` CLI commands to create the project structure, then write custom source files.

#### Step 1: Create Solution and Projects

```bash
mkdir -p <SolutionName>/src
cd <SolutionName>

# Create solution
dotnet new sln -n <SolutionName>

# Create server project (ASP.NET Core web)
dotnet new web -n <SolutionName>.Server -o src/<SolutionName>.Server

# Create client project (Blazor Server)
dotnet new blazor -n <SolutionName>.Client -o src/<SolutionName>.Client --interactivity Server

# Create agents class library
dotnet new classlib -n <SolutionName>.Agents -o src/<SolutionName>.Agents

# Add projects to solution
dotnet sln add src/<SolutionName>.Server
dotnet sln add src/<SolutionName>.Client
dotnet sln add src/<SolutionName>.Agents
```

#### Step 2: Add NuGet References

```bash
# Server needs FabrCore.Host (includes Sdk + Core transitively)
dotnet add src/<SolutionName>.Server package FabrCore.Host

# Client needs FabrCore.Client (includes Core transitively)
dotnet add src/<SolutionName>.Client package FabrCore.Client

# Agents library needs FabrCore.Sdk (includes Core transitively)
dotnet add src/<SolutionName>.Agents package FabrCore.Sdk
```

#### Step 3: Add Project References

```bash
# Server references Agents library (so Orleans can discover agent types)
dotnet add src/<SolutionName>.Server reference src/<SolutionName>.Agents
```

#### Step 4: Delete Generated Template Files

Remove auto-generated template files that will be replaced with FabrCore-specific versions:
- Delete `src/<SolutionName>.Server/Program.cs` (replace with FabrCore server Program.cs)
- Delete `src/<SolutionName>.Agents/Class1.cs` (replace with SampleAgent.cs)
- Delete any auto-generated Blazor template files in Client that conflict

#### Step 5: Write Source Files

##### `src/<SolutionName>.Server/Program.cs`

```csharp
using FabrCore.Host;
using <SolutionName>.Agents;

var builder = WebApplication.CreateBuilder(args);

// Add FabrCore server with Orleans silo, REST API, and WebSocket support
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    // Load agent types from the Agents library
    AdditionalAssemblies = [typeof(SampleAgent).Assembly]
});

var app = builder.Build();

// Maps API controllers, enables WebSocket middleware at /ws
app.UseFabrCoreServer();

app.Run();
```

##### `src/<SolutionName>.Server/appsettings.json`

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
  }
}
```

##### `src/<SolutionName>.Server/appsettings.Development.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Warning",
      "FabrCore": "Debug",
      "Orleans": "Warning"
    }
  }
}
```

##### `src/<SolutionName>.Server/fabrcore.json`

Generate based on the user's AI provider preference (see scaffold-server skill for OpenAI/Azure templates). Use model config name `"default"`.

##### `src/<SolutionName>.Server/.gitignore`

```
fabrcore.json
```

##### `src/<SolutionName>.Agents/SampleAgent.cs`

```csharp
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ComponentModel;

namespace <SolutionName>.Agents;

/// <summary>
/// A sample AI agent that demonstrates FabrCore agent patterns.
/// </summary>
[AgentAlias("sample-agent")]
public class SampleAgent : FabrCoreAgentProxy
{
    private ChatClientAgent? _agent;
    private AgentSession? _session;

    public SampleAgent(
        AgentConfiguration config,
        IServiceProvider serviceProvider,
        IFabrCoreAgentHost fabrcoreAgentHost)
        : base(config, serviceProvider, fabrcoreAgentHost)
    {
    }

    public override async Task OnInitialize()
    {
        var modelConfigName = config.Models ?? "default";

        // Resolve tools from configuration + add local tools
        var tools = await ResolveConfiguredToolsAsync();
        tools.Add(AIFunctionFactory.Create(GetCurrentTime));

        // Create LLM-backed agent with auto-persisted chat history
        var result = await CreateChatClientAgent(
            chatClientConfigName: modelConfigName,
            threadId: config.Handle ?? "default",
            tools: tools);

        _agent = result.Agent;
        _session = result.Session;

        logger.LogInformation("SampleAgent '{Handle}' initialized", config.Handle);
    }

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        if (_agent == null || _session == null)
        {
            response.Message = "Agent is not initialized.";
            return response;
        }

        // Process message through LLM
        var result = await _session.ProcessAsync(message.Message ?? "", default);
        response.Message = string.Join("", result.Select(r => r.Text));

        // Compact history if context window is filling up
        await TryCompactAsync();

        return response;
    }

    [Description("Gets the current date and time in UTC.")]
    private string GetCurrentTime()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
    }
}
```

##### `src/<SolutionName>.Client/Program.cs`

```csharp
using FabrCore.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add FabrCore client (Orleans client, messaging, API client)
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

((IHost)app).UseFabrCoreClient();

app.Run();
```

**Note:** The `App` type reference in `MapRazorComponents<App>()` must resolve. This may require either:
- A global using for the root namespace component, OR
- Using the fully qualified name: `MapRazorComponents<Components.App>()`

Adjust based on how the Blazor template generates the `App.razor` component.

##### `src/<SolutionName>.Client/appsettings.json`

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

##### Client Blazor Components

Generate `App.razor`, `Routes.razor`, `_Imports.razor`, `Layout/MainLayout.razor`, `Pages/Home.razor`, and `Pages/AgentChat.razor` following the same templates as the scaffold-client skill. Use `sample-agent` as the `AgentType` in the ChatDock example.

##### `README.md`

```markdown
# <SolutionName>

A FabrCore AI agent system built on Orleans.

## Projects

| Project | Purpose |
|---------|---------|
| `<SolutionName>.Server` | Orleans silo — hosts agents, REST API (`/fabrcoreapi/`), WebSocket (`/ws`) |
| `<SolutionName>.Client` | Blazor Server UI — ChatDock, agent messaging, client-side interaction |
| `<SolutionName>.Agents` | Agent business logic — server-side agents extending FabrCoreAgentProxy |

## Getting Started

1. **Configure AI provider** — Edit `src/<SolutionName>.Server/fabrcore.json` with your API keys.

2. **Start the server:**
   ```bash
   dotnet run --project src/<SolutionName>.Server
   ```

3. **Start the client** (in a second terminal):
   ```bash
   dotnet run --project src/<SolutionName>.Client
   ```

4. Open the client URL in your browser and use the ChatDock widget to chat with the sample agent.

## Adding New Agents

1. Create a new class in the `<SolutionName>.Agents` project extending `FabrCoreAgentProxy`.
2. Add the `[AgentAlias("your-agent")]` attribute.
3. Implement `OnInitialize()` and `OnMessage()`.
4. The server auto-discovers agents via the `AdditionalAssemblies` configuration.

## Documentation

- [FabrCore Documentation](https://fabrcore.ai/docs)
```

##### `.gitignore`

```
## .NET
bin/
obj/
*.user
*.suo
.vs/

## FabrCore secrets
**/fabrcore.json
```

#### Step 6: Build and Verify

```bash
dotnet build <SolutionName>.sln
```

## After Generation

1. Verify the solution builds with `dotnet build`.
2. Remind the user:
   - Update `fabrcore.json` with real API keys before running.
   - Start the server first, then the client.
   - The server auto-discovers agents from the Agents assembly.
   - Add new agents by creating classes with `[AgentAlias]` in the Agents project.
   - Both server and client use `Localhost` Orleans clustering for development. For production, switch to `SqlServer` or `AzureStorage` and add a `ConnectionString`.
