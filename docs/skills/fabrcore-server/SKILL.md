---
name: fabrcore-server
description: >
  Set up and configure FabrCore servers — AddFabrCoreServer, FabrCoreServerOptions, TimeProvider, fabrcore.json LLM provider
  configuration, REST API endpoints, WebSocket, system agents, custom providers, and deployment.
  Triggers on: "FabrCore server", "AddFabrCoreServer", "AddFabrCoreServices", "UseFabrCoreServer",
  "FabrCoreServerOptions", "TimeProvider", "UseTimeProvider", "fabrcore.json", "ModelConfigurations", "ApiKeys", "REST API", "/fabrcoreapi",
  "deploy FabrCore", "system agent", "ConfigureSystemAgentAsync", "IFabrCoreAgentService",
  "AgentManagementProvider", "UseAgentManagementProvider", "IAgentManagementProvider",
  "AdditionalAssemblies", "WebSocket", "server setup", "LLM provider", "Storage API",
  "typed entity storage", "IFabrCoreStorageProvider", "UseVerifiableExecution",
  "IVerifiableExecutionStore", "IVerifiableExecutionSigner", "signed execution", "evidence bundle".
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

### Verifiable Execution Providers

Verifiable execution is off by default. Enable it when a host needs signed/tamper-evident evidence for messages, events, LLM calls, and plugin/tool side effects. SPIFFE is optional; the core feature is provider-neutral signing and evidence storage.

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseVerifiableExecution()
.UseLocalCertificateVerifiableExecutionSigner());
```

Production hosts should usually add a durable store and customer-managed signer:

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseVerifiableExecution(v =>
{
    v.RequireSignerForTrustedExecution = true;
    v.CapturePayloadBytes = false;
    v.FailOnVerificationError = true;
})
.UseVerifiableExecutionStore<SqlVerifiableExecutionStore>()
.UseVerifiableExecutionSigner<MyKmsOrCertificateSigner>());
```

For full model, setup, SPIFFE/SVID, cross-cluster trust, SQL schema, and pitfalls, use the `fabrcore-spiffe` skill.

### Custom TimeProvider for Orleans

Use `FabrCoreServerOptions.UseTimeProvider(...)` when a host needs Orleans scheduling, timers, and reminders to run against a custom clock. This is useful for demos and tests that need to fast-forward reminder due times.

```csharp
var demoClock = new DemoTimeProvider();

builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseTimeProvider(demoClock));
```

You can also let DI create the provider:

```csharp
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    AdditionalAssemblies = [typeof(MyAgent).Assembly]
}
.UseTimeProvider<DemoTimeProvider>());
```

Registration precedence:
- `UseTimeProvider(...)` wins over any existing `TimeProvider` registration.
- If no option is supplied, an app-level `TimeProvider` already registered in `builder.Services` is preserved.
- If neither exists, FabrCore registers `TimeProvider.System`.

For instance-based registration, FabrCore registers both `TimeProvider` and the concrete provider type, so a demo API can resolve `DemoTimeProvider` directly. The custom provider is intended for Orleans runtime scheduling only; FabrCore.Host timestamps, file TTLs, health timestamps, diagnostics timestamps, and registry cleanup still use real UTC time.

## What AddFabrCoreServer Configures

1. **Orleans Silo** — Clustering, persistence, reminders, streaming, and registered `TimeProvider` based on `OrleansClusterOptions`
2. **Services** — `FabrCoreChatClientService`, `FabrCoreToolRegistry`, `FabrCoreRegistry`, `FabrCoreAgentService`, `IAgentManagementProvider`, `IAclProvider`
3. **Typed Entity Storage** — `IFabrCoreStorageProvider` backed by the configured Orleans storage provider
4. **Verifiable Execution Services** — `IVerifiableExecutionStore`, `IVerifiableExecutionSigner`, `IVerifiableExecutionVerifier`, `IVerifiableExecutionContext` (disabled/no-op unless enabled)
5. **Background Services** — `AgentRegistryCleanupService`, `FileCleanupService`
6. **Assembly Discovery** — Scans `AdditionalAssemblies` for agent, plugin, and tool types
7. **ACL Configuration** — Loads `Acl` section from `fabrcore.json`, registers `IAclProvider`

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
      "CompactionThreshold": 0.75,   // Optional: trigger threshold (default 0.75)
      "PerTurnMaxInputTokens": 120000, // Optional: cumulative input budget per agent turn
      "MaxPromptInputTokens": 128000,  // Optional: hard ceiling for one prompt
      "MidTurnCompactionEnabled": true, // Optional: checkpoint compaction inside tool loops
      "RunawayBudgetBehavior": "StopWithDiagnostic"
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

System agents are shared agents under user handle `"system"` that multiple users can access. Create them server-side using `IFabrCoreAgentService`:

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

Base path: `/fabrcoreapi/`. All agent-scoped endpoints require the `x-user-handle` header to identify the caller.

> **Typed C# client:** `IFabrCoreHostApiClient` in `FabrCore.Sdk` wraps the common endpoint groups below (Agent, Storage, Discovery, Embeddings, File, ModelConfig, Diagnostics). Most agent-scoped methods take a fully-qualified `"userHandle:agentHandle"` handle and the client extracts the user handle into the `x-user-handle` header automatically. Blueprint ensure and storage methods take an explicit user handle. If a newly added endpoint is not yet surfaced by the typed client, call its REST route directly. Older `FabrCore.Client` Host API client types are obsolete. See the **FabrCoreHostApiClient** section in the `fabrcore-client` skill for usage.

---

### Agent API (`/fabrcoreapi/agent`)

Create agents, ensure blueprint agents, send messages, check health, and hard-evict agent instances. This is the primary API for programmatic agent interaction.

#### POST `/agent/create` — Create/configure agents (batch)

Creates one or more agents for a user. If the agent already exists it is reconfigured.

| Parameter | Source | Type | Required | Description |
|-----------|--------|------|----------|-------------|
| `x-user-handle` | Header | string | Yes | User handle |
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

#### POST `/agent/blueprint` — Ensure blueprint agents

Ensures the agents listed in an admin-authored blueprint exist for the target user from `x-user-handle`. This endpoint is idempotent for already configured agents: it uses the user's host-side `ClientGrain.CreateAgent` path, checks health first for tracked agents, and does not reconfigure healthy/degraded/unhealthy configured agents. Missing or not-configured agents are configured from the blueprint and added to that user's tracked-agent list.

Blueprint processing ignores incoming `ForceReconfigure = true`; use `/agent/create` when an intentional reconfigure is required.

| Parameter | Source | Type | Required | Description |
|-----------|--------|------|----------|-------------|
| `x-user-handle` | Header | string | Yes | Target user whose agents should be ensured |
| body | Body | `AgentBlueprintRequest` | Yes | Blueprint wrapper with `agents` list |
| `detailLevel` | Query | `HealthDetailLevel` | No | `Basic` (default), `Detailed`, or `Full` |

Bare handles are scoped to `x-user-handle`. Fully-qualified handles are accepted only when their user prefix matches `x-user-handle`; cross-user blueprint handles return `400 Bad Request`.

**Request**:
```json
{
  "name": "support-workspace",
  "version": "2026-06-06",
  "agents": [
    {
      "handle": "assistant",
      "agentType": "chat-agent",
      "models": "default",
      "systemPrompt": "You help the user triage support work.",
      "plugins": [ "Tickets" ],
      "args": {
        "Tickets:Queue": "support"
      }
    }
  ]
}
```

**Response** `200 OK`:
```json
{
  "Name": "support-workspace",
  "Version": "2026-06-06",
  "TotalRequested": 1,
  "SuccessCount": 1,
  "FailureCount": 0,
  "Results": [ /* AgentHealthStatus[] */ ]
}
```

#### GET `/agent/health/{handle}` — Get agent health

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user-handle` | Header | string | Yes |
| `handle` | Route | string | Yes |
| `detailLevel` | Query | `HealthDetailLevel` | No |

**Response** `200 OK`: `AgentHealthStatus` (see models below).

#### DELETE `/agent/{handle}` — Evict/delete an agent

Permanently removes an agent instance for the user handle. This is a hard-delete operation, not a reset: it unregisters timers and reminders, removes stream subscriptions, clears the `AgentGrain` persisted Orleans state, removes management registry and client tracking entries, and deactivates the grain.

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user-handle` | Header | string | Yes |
| `handle` | Route | string | Yes |

**Response** `200 OK`: `AgentEvictionResult` (see models below).

**Response** `409 Conflict`: agent is actively processing a message. Retry later; v1 does not force-delete active `OnMessage` work.

#### POST `/agent/chat/{handle}` — Send chat message

Simple request/response chat. Blocks until the agent responds.

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user-handle` | Header | string | Yes |
| `handle` | Route | string | Yes |
| body | Body | `string` | Yes |

**Response** `200 OK`: `AgentMessage` — the agent's reply.

#### POST `/agent/event/{handle}` — Send event (fire-and-forget)

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user-handle` | Header | string | Yes |
| `handle` | Route | string | Yes |
| body | Body | `EventMessage` | Yes |

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

#### DELETE `/file/{fileId}` — Delete a file

Deletes a temporary file before its TTL expires.

**Response** `204 No Content`: file existed and was deleted.

**Response** `404 Not Found`: no file existed for the supplied ID.

---

### Storage API (`/fabrcoreapi/storage`)

Typed user-handle-scoped entity storage for application data. Values are sent and returned as JSON over HTTP, but consumers use generic .NET values through `FabrCore.Sdk.IFabrCoreHostApiClient` or `IFabrCoreStorageProvider`.

The Host stores each value internally in an envelope:

| Field | Purpose |
|-------|---------|
| `ValueJson` | Serialized value JSON |
| `ValueType` | Metadata containing the original .NET type name |
| `CreatedUtc` | First write time |
| `UpdatedUtc` | Last write time |

Storage uses the configured Orleans grain storage provider named `FabrCoreOrleansConstants.StorageProviderName` (`"fabrcoreStorage"`). With the simple `AddFabrCoreServer` path, `ClusteringMode: Localhost` uses Orleans memory grain storage, so typed storage entities and grain state are lost when the process exits. Use `SqlServer`, `AzureStorage`, or custom Orleans storage for restart-safe persistence.

Addressing:
- `x-user-handle` is the user handle partition and ACL boundary.
- `container` is the logical bucket, mapped to the Orleans state name.
- `entityKey` is the key within the userHandle/container. The route is a catch-all, so slash-delimited keys are allowed.

#### GET `/storage/{container}/{entityKey}` — Read an entity

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user-handle` | Header | string | Yes |
| `container` | Route | string | Yes |
| `entityKey` | Route | string | Yes |

**Response** `200 OK`: the stored JSON value.

**Response** `404 Not Found`: no value exists.

#### PUT `/storage/{container}/{entityKey}` — Create or replace an entity

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user-handle` | Header | string | Yes |
| `container` | Route | string | Yes |
| `entityKey` | Route | string | Yes |
| body | Body | JSON value | Yes |

**Response** `204 No Content`.

If a PUT returns a non-success status, SDK callers receive an `HttpRequestException` whose message includes the status code, reason phrase, and response body. Log the exception message when diagnosing `400 Bad Request`; validation details are expected to be in the response body.

#### DELETE `/storage/{container}/{entityKey}` — Delete an entity

| Parameter | Source | Type | Required |
|-----------|--------|------|----------|
| `x-user-handle` | Header | string | Yes |
| `container` | Route | string | Yes |
| `entityKey` | Route | string | Yes |

**Response** `204 No Content`: a value existed and was deleted.

**Response** `404 Not Found`: no value existed.

Pitfalls:
- This is CRUD-only in v1: no list/query API, no partial updates, and no ETags.
- Upserts are last-writer-wins.
- `ValueType` is informational; reads deserialize into the caller-requested type.
- Do not resolve Orleans `IGrainStorage` directly from application code. Use the FabrCore SDK or Host services so Orleans does not leak into clients.
- Host-internal `IFabrCoreStorageProvider` calls are user-handle-free and use the system partition. For user data, go through the user-handle-scoped API/client or a Host service that explicitly supplies the user handle.

---

### Discovery API (`/fabrcoreapi/discovery`)

Introspect available agent types, plugins, and tools registered via `AdditionalAssemblies`. Returns full registry metadata including capabilities, notes, and method descriptions.

Discovery is type-level, not instance-level. Evicting an agent removes that created agent from diagnostics/client tracking, but it does not remove the agent class/alias from `/discovery` unless the assembly/type is removed from the host.

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

**Collision detection:** If two different types share the same alias, the response includes a `collisions` array (omitted when empty):
```json
{
  "collisions": [
    {
      "alias": "my-agent",
      "category": "agent",
      "types": ["ProjectA.Agents.MyAgent", "ProjectB.Agents.MyAgent"]
    }
  ]
}
```

Each collision lists the alias, the category (`agent`, `plugin`, or `tool`), and all competing type names. The last type scanned wins the alias. Collisions are also logged as warnings at startup.

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

### ChatCompletion API (`/fabrcoreapi/ChatCompletion`)

Send a chat completion request to the configured LLM. Uses `IFabrCoreChatClientService` to resolve the model by name from `fabrcore.json`. Designed for single-turn completions (no streaming, no tool calling).

#### POST `/ChatCompletion` — Chat completion

**Request**:
```json
{
  "Messages": [
    { "Role": "user", "Content": "Extract entities from this text..." }
  ],
  "Options": {
    "Model": "gpt-4o-mini",
    "MaxOutputTokens": 2048,
    "Temperature": 0.2
  }
}
```

`Options` is optional. All fields inside `Options` are optional. Supported options: `Model` (default: `"default"`), `MaxOutputTokens`, `Temperature`, `TopP`, `TopK`, `StopSequences`, `FrequencyPenalty`, `PresencePenalty`.

**Response** `200 OK`:
```json
{
  "Text": "The extracted response text...",
  "Model": "gpt-4o-mini",
  "Usage": { "InputTokens": 150, "OutputTokens": 80 }
}
```

**Client fallback pattern** — on a client host (`AddFabrCoreClient`), use `IFabrCoreHostApiClient.GetChatCompletionAsync()` which POSTs to this endpoint on the server host. On a server host (`AddFabrCoreServer`), resolve `IFabrCoreChatClientService` directly from DI.

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

Removes old deactivated entries from the diagnostics/management registry only. It does not clear `AgentGrain` persisted state, timers, reminders, or stream subscriptions. Use `DELETE /agent/{handle}` for hard eviction.

| Parameter | Source | Type | Required | Description |
|-----------|--------|------|----------|-------------|
| `olderThanHours` | Query | int | No | Hours threshold (default 24) |

**Response** `200 OK`: `{ "PurgedCount": 3, "Message": "Purged 3 agents deactivated more than 24 hours ago" }`

---

### Verifiable Execution API (`/fabrcoreapi/monitor/verifiable-execution`)

Export and verify signed execution evidence when verifiable execution is enabled.

| Method | Route | Purpose |
|--------|-------|---------|
| `GET` | `/monitor/verifiable-execution/operations/{traceId}` | Return the evidence bundle for an operation |
| `GET` | `/monitor/verifiable-execution/operations/{traceId}/bundle` | Alias for bundle export |
| `GET` | `/monitor/verifiable-execution/operations/{traceId}/verify` | Verify the stored bundle server-side |
| `POST` | `/monitor/verifiable-execution/operations/{traceId}/verify` | Verify the stored bundle server-side |

External systems should prefer bundle export plus local verification against their own trust roots. Use `fabrcore-spiffe` for signer/store/trust-bundle details.

---

### Response Models

**`AgentBlueprintRequest`** — posted to `/agent/blueprint`:

| Field | Type | Description |
|-------|------|-------------|
| `Name` | string? | Optional blueprint name for caller traceability |
| `Version` | string? | Optional blueprint version |
| `Agents` | List\<AgentConfiguration\> | Agent configurations to ensure for `x-user-handle` |

**`AgentBlueprintResponse`** — returned by `/agent/blueprint`:

| Field | Type | Description |
|-------|------|-------------|
| `Name` | string? | Echoed blueprint name |
| `Version` | string? | Echoed blueprint version |
| `TotalRequested` | int | Number of blueprint agents requested |
| `SuccessCount` | int | Number of healthy results |
| `FailureCount` | int | Number of non-healthy results |
| `Results` | List\<AgentHealthStatus\> | Health result for each requested agent |

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

**`AgentEvictionResult`** — returned by agent eviction:

| Field | Type | Description |
|-------|------|-------------|
| `Handle` | string | Full agent grain key (`userHandle:agentHandle`) |
| `Success` | bool | Whether eviction completed |
| `Existed` | bool | Whether any agent state/registry/tracking evidence existed |
| `Message` | string? | Human-readable result |
| `TimersDisposed` | int | Active Orleans timers disposed |
| `RemindersUnregistered` | int | Persistent Orleans reminders unregistered |
| `StreamSubscriptionsRemoved` | int | Stream subscription handles unsubscribed |
| `StateCleared` | bool | Whether `AgentGrain` persisted state was cleared |
| `RegistryRemoved` | bool | Whether diagnostics/management registry entry was removed |
| `ClientTrackingRemoved` | bool | Whether owning client tracked-agent entry was removed |
| `Timestamp` | DateTime | When eviction completed (UTC) |

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

Connect at `/ws` for real-time bidirectional communication. Requires user handle via `x-fabrcore-userhandle` header or `userhandle` query parameter.

The WebSocket ingress honors the W3C `traceparent` header — if a client sets it, the resulting `AgentMessage` ingress span parents on the caller's trace via `AgentMessageTelemetry.StartIngressActivity` (see `src/FabrCore.Host/WebSocket/WebSocketSession.cs:314`). Error responses are stamped from `Activity.Current` at lines 384, 413, 700.

## OpenTelemetry exporter setup

FabrCore depends only on **`OpenTelemetry.Api` v1.15.1** — no exporter is bundled. `UseFabrCoreServer` registers a process-wide `ActivityListener` for every `FabrCore.*` `ActivitySource` (see `src/FabrCore.Host/FabrCoreHostExtensions.cs:123-132`), so `Activity` instances materialize even without a full TracerProvider. To actually **see** spans, register your own exporter:

```csharp
// In your Program.cs, after AddFabrCoreServer / AddFabrCoreClient:
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("FabrCore.*")              // pick up every FabrCore ActivitySource
        .AddHttpClientInstrumentation()       // optional but recommended
        .AddAspNetCoreInstrumentation()       // optional for server
        .AddOtlpExporter(o =>                 // OR .AddJaegerExporter() / .AddConsoleExporter()
        {
            o.Endpoint = new Uri("http://localhost:4317");
        }))
    .WithMetrics(metrics => metrics
        .AddMeter("FabrCore.*")               // FabrCore meters (Host.Extensions, Client.ClientContext, ...)
        .AddOtlpExporter());
```

NuGet packages to add to your host project: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` (or the console/jaeger exporter of your choice).

### ActivitySource names emitted by FabrCore

| Source | Emitted by | When |
|---|---|---|
| `FabrCore.Host.AgentGrain` | `AgentGrain.ReceivedChatMessage` / `OnMessage` / `OnMessageBusy` | Inbound agent stream messages, including underscore-prefixed system messages that are recorded then ignored before `OnMessage`; ingress spans are parented on the message's `TraceId`/`SpanId` when present |
| `FabrCore.Host.Extensions` | `FabrCoreHostExtensions` | Server configuration / startup |
| `FabrCore.Host.WebSocketSession` | `WebSocketSession` | Every WebSocket frame ingress; honors `traceparent` |
| `FabrCore.Host.WebSocketMiddleware` | WebSocket middleware | Connection accept/reject |
| `FabrCore.Sdk.AgentProxy` | `FabrCoreAgentProxy.InternalOnMessage` | Inside every agent's lifecycle method |
| `FabrCore.Client.ClientContext` | `ClientContext.SendAndReceiveMessage` / `SendMessage` | Client-side sends (see fabrcore-client) |
| `FabrCore.Client.ClientContextFactory` | `ClientContextFactory` | Client lookup/creation |
| `FabrCore.Client.DirectMessageSender` | `DirectMessageSender` | Direct grain calls without ClientContext |

Filter by prefix (`AddSource("FabrCore.*")`) to pick all of them up in one line.

### Meters emitted by FabrCore

- `FabrCore.Host.Extensions` — `fabrcore.host.server.configured`, `fabrcore.host.errors`
- `FabrCore.Client.ClientContext` — `MessageProcessingDuration` (histogram), `MessagesProcessedCounter` (counter), error counters

### Correlating with the in-process monitor

`MonitoredMessage.TraceId` and `MonitoredLlmCall.TraceId` are the same W3C 32-char hex values stamped by `StartIngressActivity` and visible in your exporter. Join on `TraceId` to pivot between in-process monitor queries (agent/tool/LLM provenance) and external span timing views. See **fabrcore-agentmonitor → Correlating with OpenTelemetry traces** and **fabrcore-messaging → Correlation and Tracing**.

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

// Server options
public FabrCoreServerOptions UseTimeProvider(TimeProvider provider)
public FabrCoreServerOptions UseTimeProvider<TTimeProvider>()
    where TTimeProvider : TimeProvider
```

## Deployment Considerations

1. **Single Server (Dev)** — Use `Localhost` clustering for throwaway state, runs on any machine
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
