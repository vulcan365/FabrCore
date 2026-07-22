---
name: fabrcore-orleans
description: >
  Configure Orleans for FabrCore: Host clustering providers, persistence, streams, reminders,
  TimeProvider, multi-silo deployment, provider-neutral client gateway discovery, Orleans mTLS,
  and connection resilience. Use for "Orleans", "UseOrleans", "AddFabrCoreServer",
  "FabrCoreOrleansConstants", "ClusteringMode", "SqlServer clustering", "AzureStorage clustering",
  "OrleansClusterOptions", "UseTimeProvider", "FabrCore.Client.Orleans",
  "AddFabrCoreOrleansClientAsync", "UseFabrCoreHostClustering", "FabrCoreGatewayDiscoveryClient",
  "FabrCoreHostGatewayListProvider", "FabrCoreHostUrl", "IGatewayListProvider", "IClusterClient",
  "Orleans TLS", or "typed entity storage".
  Do NOT use for: FabrCore server setup (AddFabrCoreServer, REST API) — use fabrcore-server.
  Do NOT use for: general Orleans unrelated to FabrCore.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# Orleans Configuration for FabrCore

FabrCore uses Microsoft Orleans as its distributed runtime. This skill covers clustering, persistence, streaming, and advanced Orleans configuration.

## Simple Path: AddFabrCoreServer

`AddFabrCoreServer()` configures Orleans automatically from `appsettings.json`. Use this when Localhost/SqlServer/AzureStorage modes are sufficient.

Localhost mode is built into `FabrCore.Host`. The other modes live in provider packages that are auto-discovered — reference the package and set `Orleans:ClusteringMode`; no code changes needed:

| Mode | NuGet package |
|------|---------------|
| `Localhost` | (built into FabrCore.Host) |
| `SqlServer` | `FabrCore.Host.SqlServer` |
| `AzureStorage` | `FabrCore.Host.AzureStorage` |

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
});
```

Explicit registration is also available (optional): `options.UseSqlServer()` or `options.UseAzureStorage()`:

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
});
```

Orleans settings are read from the `"Orleans"` section in `appsettings.json`.

### Custom TimeProvider

`AddFabrCoreServer()` can register a custom `System.TimeProvider` for Orleans scheduling, timers, and reminders:

```csharp
var demoClock = new DemoTimeProvider();

builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseTimeProvider(demoClock));
```

Use this for demo/test clocks that need to fast-forward Orleans reminders. `UseTimeProvider(...)` overrides prior `TimeProvider` registrations. Without it, FabrCore preserves an app-registered `TimeProvider`; if none exists, it registers `TimeProvider.System`.

The custom provider is for Orleans runtime scheduling only. FabrCore.Host timestamps, health timestamps, file TTLs, diagnostics timestamps, and cleanup services continue to use real UTC time.

### Post-Provider Orleans Configuration

Use `FabrCoreServerOptions.ConfigureOrleans(...)` when the simple Host path needs provider-neutral
Orleans customization after FabrCore configures Localhost, SQL Server, or Azure Storage. The main
use case is transport mTLS:

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.ConfigureOrleans(orleans =>
    orleans.UseTls(/* Host certificate and client-certificate validation */)));
```

Do not call `UseOrleans` separately when using `AddFabrCoreServer`; the callback exists so the
simple path still invokes `UseOrleans` exactly once.

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

- Requires the `FabrCore.Host.SqlServer` package and a SQL Server instance
- Orleans tables are created automatically on startup (`AutoInitDatabase`, default true)
- Persistent state survives restarts
- Multi-silo clustering supported
- Streams use the in-memory provider (Orleans has no SQL Server streaming provider)
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

- Requires the `FabrCore.Host.AzureStorage` package
- Azure Tables for clustering, reminders, and stream pub/sub state
- Azure Blobs for grain persistence (default — agent state can exceed the 1 MB table entity limit); table storage opt-in
- Azure Storage Queues for streams (default); memory streams opt-in
- Tables, the blob container, and stream queues are provisioned automatically on startup (`AutoInitDatabase`, default true)
- Multi-silo clustering supported
- Local development: run [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) and set `"ConnectionString": "UseDevelopmentStorage=true"`

Optional tuning via the `Orleans:AzureStorage` section (all defaults are sensible):

```json
{
  "Orleans": {
    "AzureStorage": {
      "GrainStorage": "Blob",              // Blob (default) | Table
      "ContainerName": "fabrcore-grainstate",
      "Streams": "AzureQueue",             // AzureQueue (default) | Memory
      "StreamQueueCount": 8                // must match across all silos
    }
  }
}
```

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

### Provider-Neutral Client Configuration

Trusted server-side clients that need grain references, object-reference observers, streams, and
the rest of Orleans use `FabrCore.Client.Orleans`. They remain normal Orleans clients, but they do
not reference the Host's clustering package or copy its cluster identity and connection string.

```xml
<PackageReference Include="FabrCore.Client.Orleans" Version="*" />
```

```json
{
  "FabrCoreHostUrl": "https://fabrcore.internal.example"
}
```

```csharp
using FabrCore.Client.Orleans;

var discoveryHttpClient = new HttpClient();

await builder.AddFabrCoreOrleansClientAsync(
    discoveryHttpClient,
    options =>
    {
        options.FabrCoreHostUrl = builder.Configuration["FabrCoreHostUrl"]!;
    },
    orleans =>
    {
        orleans.UseTls(/* application certificate configuration */);
    });
```

`AddFabrCoreOrleansClientAsync` performs discovery before Orleans registration,
configures `ClusterOptions`, installs `FabrCoreHostGatewayListProvider`, applies the caller's
Orleans configuration, and calls `UseOrleansClient`. Application services continue to inject the
standard `IClusterClient`:

```csharp
public sealed class ClientService(IClusterClient clusterClient)
{
    // Grain references, observers, streams, serialization, routing, and retries remain available.
}
```

For custom startup composition, fetch and validate the document explicitly, then pass it to the
lower-level extension:

```csharp
var clientOptions = new FabrCoreOrleansClientOptions
{
    FabrCoreHostUrl = builder.Configuration["FabrCoreHostUrl"]
};
var discoveryClient = new FabrCoreGatewayDiscoveryClient(
    discoveryHttpClient,
    clientOptions);
var discovery = await discoveryClient.GetGatewayDiscoveryAsync();

builder.UseOrleansClient(orleans =>
{
    orleans.UseFabrCoreHostClustering(discoveryClient, discovery);
    orleans.UseTls(/* application certificate configuration */);
});
```

The application owns the discovery `HttpClient`; keep it alive for periodic refreshes. Initial
retrieval, validation, or TLS-policy failures fail startup with a
`FabrCoreGatewayDiscoveryException`.

When the Host advertises `requireOrleansTls: false`, the client still rejects insecure Orleans
transport by default. Set `AllowInsecureOrleansTransport = true` only on a trusted Development
network. Browsers and untrusted public clients should use the Host HTTP/WebSocket APIs instead.

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

// Optional: register a custom clock before UseOrleans.
builder.Services.AddSingleton<TimeProvider>(new DemoTimeProvider());

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
| Clustering | Localhost, SqlServer, AzureStorage (provider packages), or custom `IFabrCoreOrleansProvider` | Any Orleans provider |
| Storage | Memory, ADO.NET, Azure Blob/Table | Any Orleans provider |
| Streams | Memory (Localhost/SqlServer), Azure Queue (AzureStorage) | Any Orleans provider |
| TimeProvider | `UseTimeProvider(...)`, app DI registration, or `TimeProvider.System` | Register `TimeProvider` in DI before Orleans starts |
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
- **Agent eviction** — FabrCore's hard-delete path unregisters timers/reminders, removes stream subscriptions, clears `AgentGrain` persistent state, removes registry/principal-tracking entries, and calls `DeactivateOnIdle()`.

### Deactivation vs Eviction

Normal Orleans deactivation only removes an activation from memory. FabrCore still flushes pending chat/custom state and marks the agent deactivated in management so the virtual actor can be restored from persisted configuration later.

Eviction is different: `IAgentGrain.EvictAgent()` is a destructive host operation exposed by `DELETE /fabrcoreapi/Agent/{handle}`. It uses `IPersistentState<AgentGrainState>.ClearStateAsync()`, unregisters all reminders via `GetReminders()`, unsubscribes stream handles created by the grain, skips normal deactivation flushes, removes management and principal-tracking records, then deactivates the activation. If `OnMessage` is currently active, v1 returns `409 Conflict` rather than deleting under active execution.

## Typed Entity Storage and Orleans

FabrCore typed entity storage is implemented by the Host using the configured Orleans grain storage provider. Consumers should use `FabrCore.Sdk.IFabrCoreStorageProvider` or `FabrCore.Sdk.IFabrCoreHostApiClient`; they should not reference Orleans storage types directly.

Internal mapping:

| FabrCore concept | Orleans storage mapping |
|------------------|-------------------------|
| Provider | Named provider `FabrCoreOrleansConstants.StorageProviderName` (`"fabrcoreStorage"`) |
| Principal handle | First segment of the internal grain key |
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
- Do not inject keyed `IGrainStorage` into SDK, client, agent, or plugin consumers. That leaks Orleans and bypasses FabrCore's principalHandle/container/entityKey abstraction.
- Orleans storage providers may enforce ETags, but FabrCore entity storage v1 is last-writer-wins and does not expose ETags.
- The storage API is not Azure Table Storage. It has similar principalHandle/container/entityKey semantics but no table query surface in v1.
- `container` becomes the Orleans state name, so keep it stable and use simple names such as `"preferences"`, `"workflow-checkpoints"`, or `"integration-cursors"`.

## Connection Resilience

`FabrCoreHostGatewayListProvider` refreshes through the Host endpoint at the interval
in the discovery document. A transient refresh failure logs a warning and returns the last valid
gateway list, so a Host API restart does not by itself disconnect an established Orleans client.
A later successful refresh replaces the cached list. Orleans continues to own gateway selection,
connection recovery, grain routing, observer references, serialization, and call retries.

The discovered `clusterId` and `serviceId` cannot change after client startup. A refresh that
reports different identity is rejected and the last-known-good gateways remain in use. Restart the
client intentionally when moving it to a different cluster.

The discovery endpoint does not secure the subsequent Orleans TCP connection. Direct Orleans
access is for trusted backend applications on private networking, with mTLS required in
production. The Host alone references `FabrCore.Host.SqlServer` or
`FabrCore.Host.AzureStorage`; client projects do not reference those packages or receive their
connection strings.

## Data Flow

### Message Flow (HTTP/WebSocket to Agent)

```
External application
  └─> Host HTTP/WebSocket endpoint
        └─> PrincipalGrain (Orleans)
              └─> AgentGrain.ReceivedChatMessage()
                    └─> underscore-prefixed system messages are recorded and ignored
              └─> AgentGrain.OnMessage()
                    └─> FabrCoreAgentProxy.OnMessage()
                          └─> ChatClientAgent → LLM API Call
                          └─> AgentMessage.Response()
              └─> Return to PrincipalGrain
        └─> Observer/WebSocket callback when applicable
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
