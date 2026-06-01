---
name: fabrcore-orleans
description: >
  Configure Orleans for FabrCore — clustering modes, advanced Orleans configuration with UseOrleans,
  required providers (FabrCoreOrleansConstants), persistence, streaming, reminders, multi-silo deployment,
  and connection resilience.
  Triggers on: "Orleans", "clustering", "silo", "grain", "UseOrleans", "AddFabrCore",
  "FabrCoreOrleansConstants", "ClusteringMode", "SqlServer clustering", "AzureStorage clustering",
  "Orleans configuration", "multi-silo", "Orleans persistence", "Orleans streaming",
  "AddFabrCoreServices", "StorageProviderName", "PubSubStoreName", "StreamProviderName",
  "OrleansClusterOptions", "ConnectionRetryCount", "GatewayListRefreshPeriod",
  "IFabrCoreStorageProvider", "typed entity storage".
  Do NOT use for: FabrCore server setup (AddFabrCoreServer, REST API) — use fabrcore-server.
  Do NOT use for: general Orleans unrelated to FabrCore.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# Orleans Configuration for FabrCore

FabrCore uses Microsoft Orleans as its distributed runtime. This skill covers clustering, persistence, streaming, and advanced Orleans configuration.

## Simple Path: AddFabrCoreServer

`AddFabrCoreServer()` configures Orleans automatically from `appsettings.json`. Use this when Localhost/SqlServer/AzureStorage modes are sufficient:

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
});
```

Orleans settings are read from the `"Orleans"` section in `appsettings.json`.

## Clustering Modes

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

- In-memory persistence (typed storage entities, agent state, client state, and management state are lost on restart)
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

- Azure Tables for clustering and reminders
- Azure Blobs for grain persistence
- Multi-silo clustering supported

## Orleans Configuration Schema

### Server appsettings.json

```json
{
  "Orleans": {
    "ClusterId": "string",              // Cluster identifier (must match across silos)
    "ServiceId": "string",              // Service identifier (must match across silos)
    "ClusteringMode": "string",         // Localhost | SqlServer | AzureStorage
    "ConnectionString": "string",       // Required for SqlServer/AzureStorage
    "StorageConnectionString": "string" // Optional: separate storage connection
  }
}
```

### Client appsettings.json

```json
{
  "Orleans": {
    "ClusterId": "string",              // Must match server
    "ServiceId": "string",              // Must match server
    "ClusteringMode": "string",         // Must match server
    "ConnectionString": "string",       // Must match server
    "ConnectionRetryCount": 5,          // Max connection retries (default 5)
    "ConnectionRetryDelay": "00:00:03", // Base retry delay (default 3s)
    "GatewayListRefreshPeriod": "00:00:30" // Gateway refresh interval (default 30s)
  }
}
```

**Important:** The `Orleans` section must match between server and client exactly (same ClusterId, ServiceId, and ClusteringMode).

## Advanced Path: Direct Orleans Configuration

For full control over Orleans (custom providers, Event Hubs, Cosmos DB, Redis, etc.), use the decomposed API:

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
    siloBuilder.Configure<ClusterOptions>(o =>
    {
        o.ClusterId = "my-cluster";
        o.ServiceId = "my-svc";
    });

    // Storage — must register these two named providers
    siloBuilder.AddAzureTableGrainStorage(
        FabrCoreOrleansConstants.StorageProviderName, o =>
            o.ConfigureTableServiceClient(storageConnStr));
    siloBuilder.AddAzureTableGrainStorage(
        FabrCoreOrleansConstants.PubSubStoreName, o =>
            o.ConfigureTableServiceClient(storageConnStr));

    // Streams — must register with this name
    siloBuilder.AddEventHubStreams(
        FabrCoreOrleansConstants.StreamProviderName, o => { /* ... */ });

    // Reminders — must register a reminder service
    siloBuilder.UseAzureTableReminderService(o =>
        o.ConfigureTableServiceClient(connStr));

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

| | Simple (`AddFabrCoreServer`) | Advanced (`AddFabrCoreServices` + `UseOrleans` + `AddFabrCore`) |
|---|---|---|
| Orleans config | From `appsettings.json` | You code it directly |
| Clustering | Localhost, SqlServer, AzureStorage | Any Orleans provider |
| Storage | Memory, ADO.NET, Azure | Any Orleans provider |
| Streams | Memory provider | Any Orleans provider |
| Use when | Standard modes are sufficient | Event Hubs, Cosmos DB, Redis, custom dashboards, etc. |

**Do not combine both paths.** `UseOrleans()` can only be called once per host.

## Orleans Key Concepts for FabrCore

- **Grains** — Virtual actors automatically activated/deactivated. Each agent is a grain identified by a string key (the handle).
- **Silos** — Server processes hosting grains. FabrCore.Host configures one or more silos.
- **Clustering** — How silos discover each other. Localhost for dev, SqlServer/Azure for production.
- **Persistence** — Grain state survives restarts only when the configured provider is durable. `Localhost` uses memory storage and loses state on process exit.
- **Streams** — Pub/sub messaging between grains. Used for chat and event delivery.
- **Reminders** — Persistent timers surviving grain deactivation and silo restarts.
- **Timers** — Non-persistent timers for periodic tasks within an active grain.

## Typed Entity Storage and Orleans

FabrCore typed entity storage is implemented by the Host using the configured Orleans grain storage provider. Consumers should use `FabrCore.Sdk.IFabrCoreStorageProvider` or `FabrCore.Sdk.IFabrCoreHostApiClient`; they should not reference Orleans storage types directly.

Internal mapping:

| FabrCore concept | Orleans storage mapping |
|------------------|-------------------------|
| Provider | Named provider `FabrCoreOrleansConstants.StorageProviderName` (`"fabrcoreStorage"`) |
| Owner | First segment of the internal grain key |
| Container | Orleans state name |
| Entity key | Second segment of the internal grain key |
| Value | FabrCore envelope containing `ValueJson`, `ValueType`, `CreatedUtc`, `UpdatedUtc` |

Persistence follows whatever Orleans storage is configured to do:

| Mode/provider | Entity storage behavior |
|---------------|-------------------------|
| Localhost with memory storage | Non-persistent; data is lost on restart |
| SQL storage | Persists in the configured Orleans storage tables/schema |
| Azure storage | Persists in the configured Azure storage provider |
| Custom provider | Persists according to that provider |

When using the advanced Orleans path, make sure `FabrCoreOrleansConstants.StorageProviderName` is registered. Entity storage, agent state, client state, and management state all depend on this provider existing.

Pitfalls:
- Do not inject keyed `IGrainStorage` into SDK, client, agent, or plugin consumers. That leaks Orleans and bypasses FabrCore's owner/container/key abstraction.
- Orleans storage providers may enforce ETags, but FabrCore entity storage v1 is last-writer-wins and does not expose ETags.
- The storage API is not Azure Table Storage. It has similar owner/container/key semantics but no table query surface in v1.
- `container` becomes the Orleans state name, so keep it stable and use simple names such as `"preferences"`, `"workflow-checkpoints"`, or `"integration-cursors"`.

## Connection Resilience

FabrCore.Client includes automatic retry with exponential backoff:

```json
{
  "Orleans": {
    "ConnectionRetryCount": 10,
    "ConnectionRetryDelay": "00:00:05",
    "GatewayListRefreshPeriod": "00:00:30"
  }
}
```

## Data Flow

### Message Flow (Client to Agent)

```
Client (Blazor)
  └─> ClientContext.SendMessage()
        └─> ClientGrain (Orleans)
              └─> AgentGrain.ReceivedChatMessage()
                    └─> underscore-prefixed system messages are recorded and ignored
              └─> AgentGrain.OnMessage()
                    └─> FabrCoreAgentProxy.OnMessage()
                          └─> ChatClientAgent → LLM API Call
                          └─> AgentMessage.Response()
              └─> Return to ClientGrain
        └─> Observer callback to client
  └─> UI Update
```

### Agent-to-Agent Communication

```
AgentA.OnMessage()
  └─> fabrcoreAgentHost.SendAndReceiveMessage("agentB", message)
        └─> AgentGrain.ResolveTargetHandle("agentB") → "user1:agentB"
        └─> AgentGrain(user1:agentB).OnMessage()
              └─> AgentB.OnMessage()
              └─> Response
        └─> Return to AgentA
```
