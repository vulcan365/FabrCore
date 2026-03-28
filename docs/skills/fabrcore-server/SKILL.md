---
name: fabrcore-server
description: >
  Set up and configure FabrCore servers — AddFabrCoreServer, FabrCoreServerOptions, fabrcore.json LLM provider
  configuration, REST API endpoints, WebSocket, system agents, custom providers, and deployment.
  Triggers on: "FabrCore server", "AddFabrCoreServer", "AddFabrCoreServices", "UseFabrCoreServer",
  "FabrCoreServerOptions", "fabrcore.json", "ModelConfigurations", "ApiKeys", "REST API", "/fabrcoreapi",
  "deploy FabrCore", "system agent", "ConfigureSystemAgentAsync", "IFabrCoreAgentService",
  "AgentManagementProvider", "UseAgentManagementProvider", "IAgentManagementProvider",
  "AdditionalAssemblies", "WebSocket", "server setup", "LLM provider".
  Do NOT use for: Orleans clustering/configuration — use fabrcore-orleans.
  Do NOT use for: client setup — use fabrcore-client.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Server Setup

The FabrCore server hosts the Orleans silo, REST API, and WebSocket endpoints.

## Minimal Server

```csharp
// Program.cs
using FabrCore.Host;

var builder = WebApplication.CreateBuilder(args);

builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
});

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
```

## Server Project File

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FabrCore.Host" Version="*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\MyProject.Agents\MyProject.Agents.csproj" />
  </ItemGroup>
</Project>
```

## FabrCoreServerOptions

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    // Required: assemblies containing agents, plugins, tools
    AdditionalAssemblies = [
        typeof(MyAgent).Assembly,
        typeof(MyPlugin).Assembly
    ]
});
```

`AdditionalAssemblies` tells FabrCore which assemblies to scan for `[AgentAlias]`, `[PluginAlias]`, and `[ToolAlias]` types.

### Custom Providers

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseAgentManagementProvider<SqlAgentManagementProvider>()
.UseAclProvider<SqlAclProvider>());
```

## What AddFabrCoreServer Configures

1. **Orleans Silo** — Clustering, persistence, reminders, streaming based on `OrleansClusterOptions`
2. **Services** — `FabrCoreChatClientService`, `FabrCoreToolRegistry`, `FabrCoreRegistry`, `FabrCoreAgentService`, `IAgentManagementProvider`, `IAclProvider`
3. **Background Services** — `AgentRegistryCleanupService`, `FileCleanupService`
4. **Assembly Discovery** — Scans `AdditionalAssemblies` for agent, plugin, and tool types
5. **ACL Configuration** — Loads `Acl` section from `fabrcore.json`, registers `IAclProvider`

## What UseFabrCoreServer Configures

1. **REST API** — Maps controllers at `/fabrcoreapi/`
2. **WebSocket** — Enables WebSocket middleware at `/ws`
3. **CORS** — Configures cross-origin policies

## fabrcore.json — LLM Provider Configuration

Place `fabrcore.json` in the server project root. **Add to .gitignore** (contains API keys).

### Complete Schema

```json
{
  "ModelConfigurations": [
    {
      "Name": "string",              // Required: unique model name
      "Provider": "string",          // Required: OpenAI | Azure | OpenRouter | Grok | Gemini
      "Uri": "string",               // Required for Azure, optional for others
      "Model": "string",             // Required: model identifier
      "ApiKeyAlias": "string",       // Required: references an entry in ApiKeys
      "TimeoutSeconds": 120,         // Optional: HTTP timeout (default 120)
      "MaxOutputTokens": 16384,      // Optional: max tokens in response
      "ContextWindowTokens": 128000, // Optional: total context window size
      "CompactionEnabled": true,     // Optional: enable compaction (default true)
      "CompactionKeepLastN": 20,     // Optional: messages to keep (default 20)
      "CompactionThreshold": 0.75    // Optional: trigger threshold (default 0.75)
    }
  ],
  "ApiKeys": [
    {
      "Alias": "string",             // Required: unique key alias
      "Value": "string"              // Required: the actual API key
    }
  ]
}
```

### Supported Providers

| Provider | Uri Required | Notes |
|----------|-------------|-------|
| `OpenAI` | No | Uses default OpenAI endpoint |
| `Azure` | Yes | Azure OpenAI resource URL |
| `OpenRouter` | No | Uses OpenRouter endpoint |
| `Grok` | No | xAI Grok models |
| `Gemini` | No | Google Gemini models |

Any OpenAI-compatible endpoint can be used by setting `Provider: "OpenAI"` and a custom `Uri`.

### OpenAI Configuration

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

### Azure OpenAI Configuration

```json
{
  "ModelConfigurations": [
    {
      "Name": "default",
      "Provider": "Azure",
      "Uri": "https://your-resource.openai.azure.com/",
      "Model": "gpt-4o",
      "ApiKeyAlias": "azure-key"
    }
  ],
  "ApiKeys": [
    { "Alias": "azure-key", "Value": "your-key-here" }
  ]
}
```

### Multiple Models

```json
{
  "ModelConfigurations": [
    {
      "Name": "default",
      "Provider": "OpenAI",
      "Model": "gpt-4o",
      "ApiKeyAlias": "openai"
    },
    {
      "Name": "fast",
      "Provider": "OpenAI",
      "Model": "gpt-4o-mini",
      "ApiKeyAlias": "openai",
      "CompactionKeepLastN": 10,
      "CompactionThreshold": 0.6
    },
    {
      "Name": "reasoning",
      "Provider": "OpenAI",
      "Model": "o1",
      "ApiKeyAlias": "openai",
      "TimeoutSeconds": 300,
      "CompactionEnabled": false
    }
  ]
}
```

Agents reference models by name: `config.Models = "default"`.

## System Agents

System agents are shared agents owned by `"system"` that multiple users can access. Create them server-side using `IFabrCoreAgentService`:

```csharp
public class MyStartupService : IHostedService
{
    private readonly IFabrCoreAgentService _agentService;

    public MyStartupService(IFabrCoreAgentService agentService)
    {
        _agentService = agentService;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _agentService.ConfigureSystemAgentAsync(new AgentConfiguration
        {
            Handle = "automation_agent-123",
            AgentType = "automation-agent",
            SystemPrompt = "You are an automation assistant.",
            Models = "default"
        });
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

The agent grain key becomes `"system:{config.Handle}"`. Any user can message it (ACL permitting) using the full handle `"system:automation_agent-123"`.

## REST API Endpoints

Base: `/fabrcoreapi/`

### Agent Operations

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/Agent/create` | POST | Create/configure agents (batch) |
| `/Agent/health/{userId}/{handle}` | GET | Get agent health |
| `/Agent/chat/{userId}/{handle}` | POST | Send chat message |
| `/Agent/event/{userId}/{handle}` | POST | Send event |

### Discovery

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/Discovery/agents` | GET | List registered agent types |
| `/Discovery/plugins` | GET | List registered plugins |
| `/Discovery/tools` | GET | List registered tools |

### Model Configuration

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/ModelConfig/model/{name}` | POST | Get model configuration |
| `/ModelConfig/apikey/{alias}` | GET | Get API key by alias |

### Diagnostics

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/Diagnostics/stats` | GET | System statistics |
| `/Diagnostics/agents` | GET | All registered agents |

## WebSocket

Connect at `/ws` for real-time bidirectional communication.

## IAgentManagementProvider

Pluggable provider for agent/client registration and lifecycle tracking:

```csharp
public interface IAgentManagementProvider
{
    Task RegisterAgentAsync(string key, string agentType, string handle);
    Task DeactivateAgentAsync(string key, string reason);
    Task RegisterClientAsync(string clientId);
    Task DeactivateClientAsync(string clientId, string reason);
    Task<List<AgentInfo>> GetAllAsync();
    Task<List<AgentInfo>> GetByStatusAsync(AgentStatus status);
    Task<AgentInfo?> GetByKeyAsync(string key);
    Task<List<AgentInfo>> GetByEntityTypeAsync(EntityType entityType, AgentStatus? status = null);
    Task<int> PurgeDeactivatedAsync(TimeSpan olderThan);
    Task<Dictionary<string, int>> GetStatisticsAsync();
}
```

Default: `OrleansAgentManagementProvider`. Override with `UseAgentManagementProvider<T>()`.

## Extension Methods

```csharp
// Server setup
public static WebApplicationBuilder AddFabrCoreServer(
    this WebApplicationBuilder builder, FabrCoreServerOptions? options = null)

// Server middleware
public static WebApplication UseFabrCoreServer(
    this WebApplication app, FabrCoreServerOptions? options = null)
```

## Deployment Considerations

1. **Single Server (Dev)** — Use `Localhost` clustering, runs on any machine
2. **Single Server (Prod)** — Use `SqlServer` clustering for persistent state
3. **Multi-Server** — Use `SqlServer` or `AzureStorage` clustering
4. **Azure** — Use `AzureStorage` clustering, deploy as App Service or Container App
5. **Docker** — Standard ASP.NET Core containerization applies

## Troubleshooting

**Agent not responding:**
1. Check `fabrcore.json` — ensure model name and API key are correct
2. Verify agent type matches `[AgentAlias]` value
3. Check server logs for Orleans activation errors

**Plugin tools not appearing:**
1. Verify `[PluginAlias]` matches the name in `AgentConfiguration.Plugins`
2. Ensure plugin assembly is included in `AdditionalAssemblies`
3. Check that tool methods have `[Description]` attributes
