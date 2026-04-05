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

`AdditionalAssemblies` tells FabrCore which assemblies to scan for `[AgentAlias]`, `[PluginAlias]`, and `[ToolAlias]` types. Registry metadata attributes (`[Description]`, `[FabrCoreCapabilities]`, `[FabrCoreNote]`) are also read from these assemblies and surfaced in the discovery endpoint. Types decorated with `[FabrCoreHidden]` are excluded from discovery but remain usable.

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

### Embeddings Model

The `IEmbeddings` service (auto-registered by `AddFabrCoreServer()`) looks up a model named `"embeddings"` in `fabrcore.json`. Add this entry to enable embedding generation for agents and the `/fabrcoreapi/embeddings` API:

```json
{
  "ModelConfigurations": [
    {
      "Name": "embeddings",
      "Provider": "OpenAI",
      "Model": "text-embedding-3-small",
      "ApiKeyAlias": "openai"
    }
  ]
}
```

Supported providers for embeddings: `OpenAI`, `Azure`, `OpenRouter`, `Gemini`. Grok does not support embeddings.

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

Base path: `/fabrcoreapi/`. All agent-scoped endpoints require the `x-user` header to identify the caller.

---

### Agent API (`/fabrcoreapi/agent`)

Create agents, send messages, and check health. This is the primary API for programmatic agent interaction.

#### POST `/agent/create` — Create/configure agents (batch)

Creates one or more agents for a user. If the agent already exists it is reconfigured.

| Parameter | Source | Type | Required | Description |
|-----------|--------|------|----------|-------------|
| `x-user` | Header | string | Yes | User/owner ID |
| body | Body | `List<AgentConfiguration>` | Yes | Agent configs to create |
| `detailLevel` | Query | `HealthDetailLevel` | No | `Basic` (default), `Detailed`, or `Full` |

**Response** `200 OK`:
```json
{
  "TotalRequested": 2,
  "SuccessCount": 2,
  "FailureCount": 0,
  "Results": [ /* AgentHealthStatus[] */ ]
}
```

#### GET `/agent/health/{handle}` — Get agent health

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user` | Header | string | Yes |
| `handle` | Route | string | Yes |
| `detailLevel` | Query | `HealthDetailLevel` | No |

**Response** `200 OK`: `AgentHealthStatus` (see models below).

#### POST `/agent/chat/{handle}` — Send chat message

Simple request/response chat. Blocks until the agent responds.

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user` | Header | string | Yes |
| `handle` | Route | string | Yes |
| body | Body | `string` | Yes |

**Response** `200 OK`: `AgentMessage` — the agent's reply.

#### POST `/agent/event/{handle}` — Send event (fire-and-forget)

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user` | Header | string | Yes |
| `handle` | Route | string | Yes |
| body | Body | `EventMessage` | Yes |
| `streamName` | Query | string | No |

**Response** `202 Accepted`.

---

### File API (`/fabrcoreapi/file`)

Temporary file storage with TTL-based expiration. Agents and plugins use this to exchange files (images, documents, audio) via `AgentMessage.Files` which holds file IDs. A background `FileCleanupService` purges expired files automatically.

#### POST `/file/upload` — Upload a file

| Parameter | Source | Type | Required | Description |
|-----------|--------|------|----------|-------------|
| `file` | Form | `IFormFile` | Yes | The file to upload |
| `ttlSeconds` | Query | int | No | Seconds until expiration (default from `FileStorageSettings.DefaultTtlSeconds`, typically 300) |

**Response** `200 OK`: `string` — the file ID (use this in `AgentMessage.Files`).

#### GET `/file/{fileId}` — Download a file

Returns the file contents with the correct `Content-Type`. Supports HTTP range requests for large files.

**Response** `200 OK`: file stream with content type.

#### GET `/file/{fileId}/info` — Get file metadata

**Response** `200 OK`:
```json
{
  "FileId": "abc123",
  "OriginalFileName": "report.pdf",
  "ExpiresAt": "2026-04-02T12:00:00Z",
  "FileSize": 102400,
  "ContentType": "application/pdf"
}
```

---

### Discovery API (`/fabrcoreapi/discovery`)

Introspect available agent types, plugins, and tools registered via `AdditionalAssemblies`. Returns full registry metadata including capabilities, notes, and method descriptions.

#### GET `/discovery` — List all registered types

**Response** `200 OK`:
```json
{
  "agents": [
    {
      "typeName": "MyProject.Agents.JobAgent",
      "aliases": ["job-agent"],
      "description": "Manufacturing job management agent",
      "capabilities": "Manages manufacturing jobs — lookup, status tracking, priority changes.",
      "notes": [
        "Requires a job number in context before most tools will work.",
        "Do not use for quoting — use the quotes-agent instead."
      ],
      "methods": []
    }
  ],
  "plugins": [
    {
      "typeName": "MyProject.Plugins.WeatherPlugin",
      "aliases": ["weather"],
      "description": "Real-time weather data plugin",
      "capabilities": "Current conditions, forecasts, and severe weather alerts.",
      "notes": ["Requires weather:ApiKey in Args."],
      "methods": [
        { "name": "GetCurrentWeather", "description": "Get current weather conditions for a location" },
        { "name": "GetForecast", "description": "Get a 5-day weather forecast for a location" }
      ]
    }
  ],
  "tools": [
    {
      "typeName": "MyProject.Tools.UtilityTools.FormatJson",
      "aliases": ["format-json"],
      "description": "Format a JSON string with proper indentation",
      "capabilities": "JSON pretty-printing with standard indentation.",
      "notes": [],
      "methods": [
        { "name": "FormatJson", "description": "Format a JSON string with proper indentation" }
      ]
    }
  ]
}
```

Each entry contains:
- `typeName` — Full .NET type name (or `Type.Method` for standalone tools)
- `aliases` — Alias strings used in `AgentConfiguration` (`AgentType`, `Plugins`, `Tools`)
- `description` — From `[Description]` attribute on the class (agents/plugins) or method (tools); null if not set
- `capabilities` — From `[FabrCoreCapabilities]` attribute (null if not set)
- `notes` — From `[FabrCoreNote]` attributes (empty array if none)
- `methods` — Tool method names and `[Description]` text (plugins list all tool methods; standalone tools list the single method)

---

### Embeddings API (`/fabrcoreapi/embeddings`)

Generate vector embeddings for text using the configured embedding model.

#### POST `/embeddings` — Single text embedding

**Request**:
```json
{ "Text": "The quick brown fox" }
```

**Response** `200 OK`:
```json
{ "Vector": [0.012, -0.034, ...], "Dimensions": 1536 }
```

#### POST `/embeddings/batch` — Batch embeddings (max 2048 items)

**Request**:
```json
{
  "Items": [
    { "Id": "doc-1", "Text": "First document" },
    { "Id": "doc-2", "Text": "Second document" }
  ]
}
```

**Response** `200 OK`:
```json
{
  "Results": [
    { "Id": "doc-1", "Vector": [...], "Dimensions": 1536 },
    { "Id": "doc-2", "Vector": [...], "Dimensions": 1536 }
  ]
}
```

---

### Model Config API (`/fabrcoreapi/modelconfig`)

Read model configuration and API keys from `fabrcore.json`. Useful for clients that need to know available models.

#### GET `/modelconfig/model/{name}` — Get model configuration

**Response** `200 OK`:
```json
{
  "Name": "default",
  "Provider": "OpenAI",
  "Uri": null,
  "Model": "gpt-4o",
  "ApiKeyAlias": "openai-key",
  "TimeoutSeconds": 120,
  "MaxOutputTokens": 16384,
  "ContextWindowTokens": 128000
}
```

#### GET `/modelconfig/apikey/{alias}` — Get API key value

**Response** `200 OK`: `{ "Value": "sk-..." }`

---

### Diagnostics API (`/fabrcoreapi/diagnostics`)

Monitor and manage agent lifecycle across the cluster.

#### GET `/diagnostics/agents` — List all agents

| Parameter | Source | Type | Required | Description |
|-----------|--------|------|----------|-------------|
| `status` | Query | string | No | Filter by status (e.g., `"Active"`, `"Deactivated"`) |

**Response** `200 OK`:
```json
{
  "Count": 5,
  "Agents": [ /* AgentInfo[] */ ]
}
```

#### GET `/diagnostics/agents/{key}` — Get specific agent info

**Response** `200 OK`: `AgentInfo` or `404` if not found.

#### GET `/diagnostics/agents/statistics` — Cluster statistics

**Response** `200 OK`: `{ "Total": 10, "Active": 8, "Deactivated": 2 }`

#### POST `/diagnostics/agents/purge` — Purge old deactivated agents

| Parameter | Source | Type | Required | Description |
|-----------|--------|------|----------|-------------|
| `olderThanHours` | Query | int | No | Hours threshold (default 24) |

**Response** `200 OK`: `{ "PurgedCount": 3, "Message": "Purged 3 agents deactivated more than 24 hours ago" }`

---

### Response Models

**`AgentHealthStatus`** — returned by create and health endpoints:

| Field | Type | Level | Description |
|-------|------|-------|-------------|
| `Handle` | string | Basic | Agent handle |
| `State` | `HealthState` | Basic | `Healthy`, `Degraded`, `Unhealthy`, `NotConfigured` |
| `Timestamp` | DateTime | Basic | When health was collected (UTC) |
| `IsConfigured` | bool | Basic | Whether agent has been configured |
| `Message` | string? | Basic | Human-readable status message |
| `AgentType` | string? | Detailed | Agent type alias |
| `Uptime` | TimeSpan? | Detailed | Time since activation |
| `MessagesProcessed` | long? | Detailed | Total messages handled |
| `ActiveTimerCount` | int? | Detailed | Active timer count |
| `ActiveReminderCount` | int? | Detailed | Active reminder count |
| `StreamCount` | int? | Detailed | Stream subscription count |
| `Configuration` | AgentConfiguration? | Detailed | Full agent config |
| `ProxyHealth` | ProxyHealthStatus? | Full | Proxy-level health |
| `ActiveStreams` | List\<string\>? | Full | Stream names |
| `Diagnostics` | Dictionary\<string,string\>? | Full | Diagnostic key-value pairs |

**`AgentInfo`** — returned by diagnostics endpoints:

| Field | Type | Description |
|-------|------|-------------|
| `Key` | string | Grain key |
| `AgentType` | string | Agent type alias |
| `Handle` | string | Full handle |
| `Status` | `AgentStatus` | `Active` or `Deactivated` |
| `ActivatedAt` | DateTime | When activated |
| `DeactivatedAt` | DateTime? | When deactivated (if applicable) |
| `DeactivationReason` | string? | Reason for deactivation |
| `EntityType` | `EntityType` | `Agent` or `Client` |

**`FileMetadata`** — returned by file info endpoint:

| Field | Type | Description |
|-------|------|-------------|
| `FileId` | string | Unique file ID |
| `OriginalFileName` | string | Original upload filename |
| `ExpiresAt` | DateTime | When the file will be cleaned up |
| `FileSize` | long | File size in bytes |
| `ContentType` | string | MIME content type |

---

## WebSocket

Connect at `/ws` for real-time bidirectional communication. Requires user ID via `x-fabrcore-userid` header or `userid` query parameter.

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
