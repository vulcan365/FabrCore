# Server Setup (FabrCore.Host)

## Overview

The FabrCore server hosts the Orleans silo, REST API, and WebSocket endpoints. It is the backbone of a FabrCore system.

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

## Project File

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

The `AdditionalAssemblies` property tells FabrCore which assemblies to scan for `[AgentAlias]`, `[PluginAlias]`, and `[ToolAlias]` types.

### Custom Agent Management Provider

By default, FabrCore tracks agents and clients using an Orleans grain (`OrleansAgentManagementProvider`). To use a custom storage backend (MSSQL, Azure Table Storage, etc.), implement `IAgentManagementProvider` and register it via options:

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}.UseAgentManagementProvider<SqlAgentManagementProvider>());
```

The `IAgentManagementProvider` interface defines registration, query, and maintenance methods for agent/client lifecycle tracking. See [api-reference.md](api-reference.md#iagentmanagementprovider) for the full interface.

### Custom ACL Provider

By default, FabrCore evaluates agent access control rules in memory using `InMemoryAclProvider`, which loads rules from the `FabrCore:Acl` configuration section. To use a custom storage backend (database, distributed cache, etc.), implement `IAclProvider` and register it via options:

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}.UseAclProvider<SqlAclProvider>());
```

Both providers can be chained:

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseAgentManagementProvider<SqlAgentManagementProvider>()
.UseAclProvider<SqlAclProvider>());
```

See [acl-shared-agents.md](acl-shared-agents.md) for the full `IAclProvider` interface and configuration details.

### System Agents

System agents are shared agents owned by `"system"` that multiple users can access. Create them server-side using `IFabrCoreAgentService`:

```csharp
// In a hosted service, controller, or startup code
await agentService.ConfigureSystemAgentAsync(new AgentConfiguration
{
    Handle = "automation_agent-123",
    AgentType = "automation-agent",
    SystemPrompt = "You are an automation assistant."
});
```

This creates an agent with grain key `"system:automation_agent-123"`. Any user can message it (ACL permitting) using the full handle:

```csharp
var response = await clientContext.SendAndReceiveMessage(new AgentMessage
{
    ToHandle = "system:automation_agent-123",
    Message = "Run the report"
});
```

See [acl-shared-agents.md](acl-shared-agents.md) for complete shared agent patterns.

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

## Orleans Clustering Configuration

Configure in `appsettings.json`:

### Localhost (Development)

```json
{
  "Orleans": {
    "ClusterId": "dev",
    "ServiceId": "fabrcore",
    "ClusteringMode": "Localhost"
  }
}
```

- In-memory persistence (data lost on restart)
- Single-silo only
- No external dependencies

### SqlServer (Production)

```json
{
  "Orleans": {
    "ClusterId": "prod",
    "ServiceId": "fabrcore",
    "ClusteringMode": "SqlServer",
    "ConnectionString": "Server=localhost;Database=FabrCore;Trusted_Connection=True;TrustServerCertificate=True;",
    "StorageConnectionString": "Server=localhost;Database=FabrCoreStorage;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
```

- Requires SQL Server instance
- Persistent state survives restarts
- Multi-silo clustering supported
- `StorageConnectionString` optional (falls back to `ConnectionString`)

### AzureStorage (Cloud)

```json
{
  "Orleans": {
    "ClusterId": "prod",
    "ServiceId": "fabrcore",
    "ClusteringMode": "AzureStorage",
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net",
    "StorageConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net"
  }
}
```

- Azure Tables for clustering
- Azure Blobs for grain persistence
- Azure Tables for reminders
- Multi-silo clustering supported

## fabrcore.json — LLM Provider Configuration

Place `fabrcore.json` in the server project root. **Add to .gitignore** (contains API keys).

### OpenAI

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

### Azure OpenAI

```json
{
  "ModelConfigurations": [
    {
      "Name": "default",
      "Provider": "Azure",
      "Uri": "https://your-resource.openai.azure.com/",
      "Model": "gpt-4o",
      "ApiKeyAlias": "azure-key",
      "TimeoutSeconds": 120,
      "MaxOutputTokens": 16384,
      "ContextWindowTokens": 128000
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
      "ApiKeyAlias": "openai-key"
    },
    {
      "Name": "fast",
      "Provider": "OpenAI",
      "Model": "gpt-4o-mini",
      "ApiKeyAlias": "openai-key"
    },
    {
      "Name": "embeddings",
      "Provider": "Azure",
      "Uri": "https://your-resource.openai.azure.com/",
      "Model": "text-embedding-ada-002",
      "ApiKeyAlias": "azure-key"
    }
  ],
  "ApiKeys": [
    { "Alias": "openai-key", "Value": "sk-..." },
    { "Alias": "azure-key", "Value": "..." }
  ]
}
```

Agents reference models by name: `config.Models = ["default"]` or `await GetChatClient("fast")`.

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

Connect at `/ws` for real-time bidirectional communication. The WebSocket middleware handles session management and message routing.

## Advanced Orleans Configuration

For full control over Orleans (custom providers, Event Hubs, Cosmos DB, Redis, etc.), use the decomposed API instead of `AddFabrCoreServer`:

```csharp
using FabrCore.Host;
using FabrCore.Host.Configuration;

var builder = WebApplication.CreateBuilder(args);

// 1. Register FabrCore's non-Orleans services (DI, controllers, background services)
builder.AddFabrCoreServices(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
});

// 2. Configure Orleans with full control
builder.UseOrleans(siloBuilder =>
{
    // Clustering — pick any provider
    siloBuilder.UseAzureStorageClustering(o => o.ConfigureTableServiceClient(connStr));
    siloBuilder.Configure<ClusterOptions>(o => { o.ClusterId = "my-cluster"; o.ServiceId = "my-svc"; });

    // Storage — must register these two named providers
    siloBuilder.AddAzureTableGrainStorage(FabrCoreOrleansConstants.StorageProviderName, o =>
        o.ConfigureTableServiceClient(storageConnStr));
    siloBuilder.AddAzureTableGrainStorage(FabrCoreOrleansConstants.PubSubStoreName, o =>
        o.ConfigureTableServiceClient(storageConnStr));

    // Streams — must register with this name
    siloBuilder.AddEventHubStreams(FabrCoreOrleansConstants.StreamProviderName, o => { /* ... */ });

    // Reminders — must register a reminder service
    siloBuilder.UseAzureTableReminderService(o => o.ConfigureTableServiceClient(connStr));

    // Register FabrCore grains
    siloBuilder.AddFabrCore([typeof(MyAgent).Assembly]);
});

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
```

### Required Orleans Providers

When using the advanced path, you must register these providers:

| Constant | Value | Purpose |
|----------|-------|---------|
| `FabrCoreOrleansConstants.StorageProviderName` | `"fabrcoreStorage"` | Grain state for agents, clients, and management |
| `FabrCoreOrleansConstants.PubSubStoreName` | `"PubSubStore"` | Orleans streaming pub/sub state |
| `FabrCoreOrleansConstants.StreamProviderName` | `"fabrcoreStreams"` | Agent-to-agent and agent-to-client messaging |
| Reminder service | (any) | Required for agent timers and reminders |

### Simple vs Advanced Path

- **`AddFabrCoreServer()`** — calls `AddFabrCoreServices()` + `UseOrleans()` + `AddFabrCore()` internally. Orleans is configured from `OrleansClusterOptions` in `appsettings.json`. Use this when Localhost/SqlServer/AzureStorage modes are sufficient.
- **`AddFabrCoreServices()` + `UseOrleans()` + `AddFabrCore()`** — you own the `UseOrleans()` call and configure any Orleans providers you want. Use this for Event Hubs, Cosmos DB, Redis, custom dashboards, placement strategies, etc.

**Do not combine both paths.** `UseOrleans()` can only be called once per host.

## Deployment Considerations

1. **Single Server (Dev)** — Use `Localhost` clustering, runs on any machine
2. **Single Server (Prod)** — Use `SqlServer` clustering for persistent state
3. **Multi-Server** — Use `SqlServer` or `AzureStorage` clustering, deploy multiple silos
4. **Azure** — Use `AzureStorage` clustering, deploy as Azure App Service or Container App
5. **Docker** — Standard ASP.NET Core containerization applies
