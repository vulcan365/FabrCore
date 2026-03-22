# Configuration Reference

## Configuration Files

FabrCore uses several configuration files:

| File | Location | Purpose | Git-tracked? |
|------|----------|---------|--------------|
| `appsettings.json` | Server & Client | Orleans clustering, logging, URLs | Yes |
| `fabrcore.json` | Server root | LLM providers and API keys | **No** (add to .gitignore) |
| `appsettings.Development.json` | Server & Client | Development overrides | Yes |

## fabrcore.json — Complete Schema

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
      "ContextWindowTokens": 128000  // Optional: total context window size
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

### Common Model Configurations

```json
{
  "ModelConfigurations": [
    {
      "Name": "default",
      "Provider": "OpenAI",
      "Model": "gpt-4o",
      "ApiKeyAlias": "openai",
      "TimeoutSeconds": 120,
      "MaxOutputTokens": 16384,
      "ContextWindowTokens": 128000
    },
    {
      "Name": "fast",
      "Provider": "OpenAI",
      "Model": "gpt-4o-mini",
      "ApiKeyAlias": "openai",
      "MaxOutputTokens": 4096,
      "ContextWindowTokens": 128000,
      "CompactionKeepLastN": 10,
      "CompactionThreshold": 0.6
    },
    {
      "Name": "embeddings",
      "Provider": "Azure",
      "Uri": "https://resource.openai.azure.com/",
      "Model": "text-embedding-ada-002",
      "ApiKeyAlias": "azure"
    },
    {
      "Name": "reasoning",
      "Provider": "OpenAI",
      "Model": "o1",
      "ApiKeyAlias": "openai",
      "TimeoutSeconds": 300,
      "MaxOutputTokens": 32768,
      "ContextWindowTokens": 200000,
      "CompactionEnabled": false
    }
  ]
}
```

## AgentConfiguration — Complete Schema

```csharp
public class AgentConfiguration
{
    public string Handle { get; set; }           // Agent instance identifier
    public string AgentType { get; set; }        // Must match [AgentAlias] value
    public List<string> Models { get; set; }     // Model names from fabrcore.json
    public List<string> Streams { get; set; }    // Orleans stream subscriptions
    public string SystemPrompt { get; set; }     // System instructions for the LLM
    public string Description { get; set; }      // Agent description
    public Dictionary<string, string> Args { get; set; }  // Key-value settings
    public List<string> Plugins { get; set; }    // Plugin aliases to enable
    public List<string> Tools { get; set; }      // Standalone tool aliases to enable
    public List<McpServerConfig> McpServers { get; set; } // MCP server configs
    public bool ForceReconfigure { get; set; }   // Force re-initialization
}
```

### Compaction Configuration Hierarchy

Compaction settings resolve in order: **hardcoded defaults → fabrcore.json model config → agent Args overrides**.

**Model-level settings** (in `fabrcore.json` ModelConfigurations):

| Field | Default | Description |
|-------|---------|-------------|
| `ContextWindowTokens` | `25000` | Total context window size in tokens |
| `CompactionEnabled` | `true` | Enable/disable automatic compaction |
| `CompactionKeepLastN` | `20` | Recent messages to preserve during compaction |
| `CompactionThreshold` | `0.75` | Trigger at this fraction of context window |

### Args — Built-in Keys

Agent Args override model-level settings. Compaction keys are prefixed with `_`:

| Key | Default | Description |
|-----|---------|-------------|
| `_CompactionEnabled` | `"true"` | Enable/disable automatic chat history compaction |
| `_CompactionKeepLastN` | `"20"` | Keep last N messages during compaction |
| `_CompactionThreshold` | `"0.75"` | Trigger compaction at this % of context window |
| `_CompactionMaxContextTokens` | `"25000"` | Override context window size for threshold calculation |
| `ModelConfig` | (from Models[0]) | Override model name for this agent |

### Args — Plugin Settings Convention

Use `"PluginAlias:Key"` format:
```json
{
  "Args": {
    "weather:ApiKey": "abc123",
    "weather:DefaultCity": "Seattle",
    "filesystem:RootPath": "C:\\data",
    "filesystem:ReadOnly": "true"
  }
}
```

## McpServerConfig — Complete Schema

```csharp
public class McpServerConfig
{
    public string Name { get; set; }                    // Server identifier
    public McpTransportType TransportType { get; set; } // Stdio or Http
    public string Command { get; set; }                 // Stdio: executable path
    public List<string> Arguments { get; set; }         // Stdio: command arguments
    public Dictionary<string, string> Env { get; set; } // Stdio: environment variables
    public string Url { get; set; }                     // Http: server URL
    public Dictionary<string, string> Headers { get; set; } // Http: request headers
}

public enum McpTransportType
{
    Stdio,
    Http
}
```

### MCP Examples

```json
{
  "McpServers": [
    {
      "Name": "filesystem",
      "TransportType": "Stdio",
      "Command": "npx",
      "Arguments": ["-y", "@anthropic/mcp-filesystem", "/data"],
      "Env": { "NODE_ENV": "production" }
    },
    {
      "Name": "database",
      "TransportType": "Http",
      "Url": "https://mcp.example.com/db",
      "Headers": { "Authorization": "Bearer token123" }
    }
  ]
}
```

## Orleans Clustering — Complete Schema

> **Note:** These settings are used by `AddFabrCoreServer()` (the simple path). If you use the advanced path (`AddFabrCoreServices()` + `UseOrleans()` + `AddFabrCore()`), you configure Orleans directly in code and these settings are not read. See [server-setup.md](server-setup.md#advanced-orleans-configuration) for details.

### appsettings.json (Server)

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

### appsettings.json (Client)

```json
{
  "Orleans": {
    "ClusterId": "string",              // Must match server
    "ServiceId": "string",              // Must match server
    "ClusteringMode": "string",         // Must match server
    "ConnectionString": "string",       // Must match server
    "ConnectionRetryCount": 5,          // Max connection retries
    "ConnectionRetryDelay": "00:00:03", // Base retry delay
    "GatewayListRefreshPeriod": "00:00:30" // Gateway refresh interval
  },
  "FabrCoreHostUrl": "http://localhost:5000" // Server REST API URL
}
```

## HandleUtilities

Centralized handle normalization used by AgentGrain, ChatDock, and TaskWorkingAgent:

```csharp
public static class HandleUtilities
{
    // Build the "owner:" prefix from an owner ID
    static string BuildPrefix(string ownerId);           // "user1" → "user1:"

    // Normalize a handle: bare alias gets prefixed, fully-qualified passes through
    static string EnsurePrefix(string handle, string ownerPrefix);
    // EnsurePrefix("assistant", "user1:")      → "user1:assistant"
    // EnsurePrefix("user2:assistant", "user1:") → "user2:assistant"

    // Strip the owner prefix from a handle
    static string StripPrefix(string handle, string ownerPrefix);
    // StripPrefix("user1:assistant", "user1:") → "assistant"
}
```

**Routing rules:**
- Bare alias (no colon) → auto-prefixed with caller's owner → routes to same-owner agent
- Fully-qualified handle (contains colon) → used as-is → enables cross-owner routing

## SystemMessageTypes — Reserved Message Types

All `MessageType` values starting with `_` are reserved for FabrCore internal/system use. They route through streams like normal messages but clients handle them differently.

```csharp
public static class SystemMessageTypes
{
    public const string Status = "_status";   // Thinking heartbeat (every 3s during OnMessage)
    public const string Error = "_error";     // Error notification (exception in OnMessage)

    public static bool IsSystemMessage(string? messageType)
        => messageType != null && messageType.StartsWith('_');
}
```

**Automatic behavior:**
- `AgentGrain.OnMessage` sends `_status` heartbeats every 3 seconds while processing. No heartbeat if response is under 3 seconds.
- The default status message is "Thinking..". Agents can change it via `SetStatusMessage("Searching..")` and revert with `SetStatusMessage(null)`. Compaction automatically sets "Compacting.." while running.
- On exception, `AgentGrain` sends `_error` with `ex.Message` to the original sender's stream, then rethrows.
- `ChatDock` shows `_status` as a thinking indicator and `_error` as an error message in the chat.
- System messages are NOT stored in chat history.

## LLM Usage Tracking — Response Args

FabrCore automatically tracks LLM usage metrics across all LLM calls within a single `OnMessage` invocation. Metrics are attached to the response `AgentMessage.Args` with `_`-prefixed keys.

| Args Key | Description |
|----------|-------------|
| `_tokens_input` | Total input tokens across all LLM calls |
| `_tokens_output` | Total output tokens across all LLM calls |
| `_tokens_reasoning` | Thinking/reasoning tokens (o1, Claude extended thinking) |
| `_tokens_cached_input` | Input tokens served from cache |
| `_llm_calls` | Number of LLM calls made (includes tool loops) |
| `_llm_duration_ms` | Total wall-clock time spent waiting on LLM responses |
| `_model` | Model ID from the last LLM call |
| `_finish_reason` | Finish reason from the last LLM call (`stop`, `length`, `tool_calls`, `content_filter`) |

**Automatic behavior:**
- All calls via `GetChatClient()`, `CreateChatClientAgent()` → `RunAsync()`, and compaction are tracked.
- Only keys with non-zero/non-null values are set.
- Uses `AsyncLocal` scoping — each `OnMessage` invocation has its own independent scope.
- Token counts from `FunctionInvokingChatClient` tool loops are pre-aggregated by the underlying client.

## AgentMessage — Complete Schema

```csharp
public class AgentMessage
{
    // Routing
    public string ToHandle { get; set; }           // Target handle (bare alias or "owner:agent")
    public string FromHandle { get; set; }         // Sender handle (auto-filled by AgentGrain if empty)
    public string OnBehalfOfHandle { get; set; }   // Original requester (for delegation)
    public string DeliverToHandle { get; set; }    // Final delivery target
    public string Channel { get; set; }            // Optional channel identifier

    // Content
    public string Message { get; set; }            // Text content
    public string MessageType { get; set; }        // Custom type identifier (values starting with '_' are reserved)
    public MessageKind MessageKind { get; set; }   // Request, OneWay, Response

    // Metadata
    public Dictionary<string, string> State { get; set; }  // Metadata key-values
    public Dictionary<string, string> Args { get; set; }   // Parameter key-values
    public Dictionary<string, string> Data { get; set; }   // Structured data
    public Dictionary<string, byte[]> Files { get; set; }  // File attachments
    public string TraceId { get; set; }            // Correlation ID

    // Helper method
    public AgentMessage Response();  // Creates a response with routing pre-filled
}

public enum MessageKind
{
    Request,   // Expects a response
    OneWay,    // Fire-and-forget
    Response   // Reply to a request
}
```

## Health Status — Complete Schema

```csharp
public record AgentHealthStatus
{
    // Basic (always included)
    public string Handle { get; init; }
    public HealthState State { get; init; }
    public DateTime Timestamp { get; init; }
    public bool IsConfigured { get; init; }
    public string? Message { get; init; }

    // Detailed (HealthDetailLevel.Detailed+)
    public string? AgentType { get; init; }
    public TimeSpan? Uptime { get; init; }
    public long MessagesProcessed { get; init; }
    public int ActiveTimers { get; init; }
    public int ActiveReminders { get; init; }
    public int ActiveStreams { get; init; }
    public AgentConfiguration? Configuration { get; init; }

    // Full (HealthDetailLevel.Full)
    public AgentHealthStatus? ProxyHealth { get; init; }
    public List<string>? ActiveStreamNames { get; init; }
    public Dictionary<string, object>? Diagnostics { get; init; }
}

public enum HealthState
{
    Healthy,
    Degraded,
    Unhealthy,
    NotConfigured
}

public enum HealthDetailLevel
{
    Basic,
    Detailed,
    Full
}
```
