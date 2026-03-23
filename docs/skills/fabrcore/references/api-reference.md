# FabrCore API Reference

Complete type definitions and method signatures from the FabrCore source code.

## Required Using Directives

```csharp
using FabrCore.Core;          // AgentMessage, AgentConfiguration, MessageKind
using FabrCore.Core.Acl;      // IAclProvider, AclRule, AclPermission, AclEvaluationResult, FabrCoreAclOptions
using FabrCore.Sdk;           // FabrCoreAgentProxy, IFabrCoreAgentHost, IFabrCorePlugin, AgentAlias, PluginAlias
using FabrCore.Client;        // IClientContextFactory, IClientContext, AddFabrCoreClient, UseFabrCoreClient
using FabrCore.Host;          // AddFabrCoreServer, UseFabrCoreServer, FabrCoreServerOptions
using Microsoft.Agents.AI;    // AIAgent, AgentSession, ChatClientAgent (REQUIRED for agent development)
using Microsoft.Extensions.AI; // ChatMessage, ChatRole, IChatClient, AITool, AIFunction
```

## FabrCoreAgentProxy (Base Class)

**Namespace:** `FabrCore.Sdk`

### Protected Fields

```csharp
protected readonly AgentConfiguration config;
protected readonly IFabrCoreAgentHost fabrcoreAgentHost;  // NOTE: "fabrcoreAgentHost" not "fabrAgentHost"
protected readonly IServiceProvider serviceProvider;
protected readonly ILoggerFactory loggerFactory;
protected readonly ILogger<FabrCoreAgentProxy> logger;
protected readonly IConfiguration configuration;
protected readonly IFabrCoreChatClientService chatClientService;
```

**CRITICAL:** The field is `fabrcoreAgentHost` (with "fabrcore" prefix), NOT `fabrAgentHost`.

### Constructor

```csharp
public FabrCoreAgentProxy(
    AgentConfiguration config,
    IServiceProvider serviceProvider,
    IFabrCoreAgentHost fabrcoreAgentHost)
```

### Key Methods

```csharp
// Resolve all configured tools (plugins, standalone, MCP)
protected async Task<List<AITool>> ResolveConfiguredToolsAsync()

// Create a chat client agent with LLM, tools, and history
protected async Task<ChatClientAgentResult> CreateChatClientAgent(
    string chatClientConfigName,    // Model config name from fabrcore.json
    string threadId,                // Thread ID for chat history persistence
    IList<AITool>? tools = null,
    Action<ChatClientAgentOptions>? configureOptions = null)

// Override to customize compaction logic (runs automatically after each OnMessage)
// Default implementation uses CompactionService with LLM summarization
public virtual async Task<CompactionResult?> OnCompaction(
    FabrCoreChatHistoryProvider chatHistoryProvider,
    CompactionConfig compactionConfig)

// Access compaction internals from OnCompaction overrides
protected CompactionService? CompactionServiceInstance { get; }
protected string? CompactionChatClientConfigName { get; }

// Set the heartbeat status message (default "Thinking..", null reverts to default)
protected void SetStatusMessage(string? message)
```

### ChatClientAgentResult

```csharp
public record ChatClientAgentResult(
    AIAgent Agent,
    AgentSession Session,
    FabrCoreChatHistoryProvider? ChatHistoryProvider = null);
```

## AgentConfiguration

**Namespace:** `FabrCore.Core`

```csharp
public class AgentConfiguration
{
    public string? Handle { get; set; }
    public string? AgentType { get; set; }
    public string? Models { get; set; }          // Single string, NOT List<string>
    public List<string> Streams { get; set; } = new();
    public string? SystemPrompt { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string> Args { get; set; } = new();
    public List<string> Plugins { get; set; } = new();
    public List<string> Tools { get; set; } = new();
    public List<McpServerConfig> McpServers { get; set; } = new();
    public bool ForceReconfigure { get; set; }
}
```

## AgentMessage

**Namespace:** `FabrCore.Core`

```csharp
public class AgentMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? ToHandle { get; set; }
    public string? FromHandle { get; set; }
    public string? OnBehalfOfHandle { get; set; }
    public string? DeliverToHandle { get; set; }
    public string? Channel { get; set; }
    public string? MessageType { get; set; }
    public string? Message { get; set; }
    public MessageKind Kind { get; set; } = MessageKind.Request;
    public string? DataType { get; set; }
    public byte[]? Data { get; set; }
    public List<string> Files = new List<string>();
    public Dictionary<string, string>? State { get; set; } = new();
    public Dictionary<string, string>? Args { get; set; } = new();
    public string? TraceId { get; set; } = Guid.NewGuid().ToString();

    public AgentMessage Response() { /* creates response message */ }
}

public enum MessageKind
{
    Request = 0,
    OneWay = 1,
    Response = 2
}
```

## EventMessage

**Namespace:** `FabrCore.Core`

CloudEvents-inspired message for fire-and-forget event delivery. Used by `SendEvent()` and `OnEvent()`.

```csharp
public class EventMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; }               // Event type (e.g. "order.created")
    public string Source { get; set; }              // Producer handle
    public string? Subject { get; set; }            // Optional subject/topic
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

    // Routing
    public string Namespace { get; set; }           // Stream namespace for delivery
    public string Channel { get; set; }             // Channel within namespace

    // Payload
    public string? Data { get; set; }               // String payload
    public string? DataContentType { get; set; }    // MIME type (e.g. "application/json")
    public byte[]? BinaryData { get; set; }         // Binary payload

    // Extensions
    public Dictionary<string, string>? Args { get; set; }
    public string? TraceId { get; set; } = Guid.NewGuid().ToString();
}
```

## IAgentManagementProvider

**Namespace:** `FabrCore.Core`

Pluggable provider for agent/client registration, tracking, and lifecycle. The default implementation (`OrleansAgentManagementProvider`) delegates to the `AgentManagementGrain`. Override via `FabrCoreServerOptions.UseAgentManagementProvider<T>()`.

```csharp
public interface IAgentManagementProvider
{
    // Registration
    Task RegisterAgentAsync(string key, string agentType, string handle);
    Task DeactivateAgentAsync(string key, string reason);
    Task RegisterClientAsync(string clientId);
    Task DeactivateClientAsync(string clientId, string reason);

    // Queries
    Task<List<AgentInfo>> GetAllAsync();
    Task<List<AgentInfo>> GetByStatusAsync(AgentStatus status);
    Task<AgentInfo?> GetByKeyAsync(string key);
    Task<List<AgentInfo>> GetByEntityTypeAsync(EntityType entityType, AgentStatus? status = null);

    // Maintenance
    Task<int> PurgeDeactivatedAsync(TimeSpan olderThan);
    Task<Dictionary<string, int>> GetStatisticsAsync();
}
```

### System Message Detection

Messages with `MessageType` starting with `_` are FabrCore system messages (heartbeats, errors).

```csharp
// Inline check (works with all versions):
if (message.MessageType?.StartsWith('_') == true)
    return; // skip system messages

// Or use SystemMessageTypes (if available in your NuGet version):
if (SystemMessageTypes.IsSystemMessage(message.MessageType))
    return;
```

System message type constants:
- `"_status"` - Periodic heartbeat while agent processes
- `"_error"` - Agent encountered an error

## IAclProvider

**Namespace:** `FabrCore.Core.Acl`

Pluggable provider for agent access control. The default implementation (`InMemoryAclProvider`) loads rules from the `Acl` section in `fabrcore.json`. Override via `FabrCoreServerOptions.UseAclProvider<T>()`.

```csharp
public interface IAclProvider
{
    // Evaluation
    Task<AclEvaluationResult> EvaluateAsync(
        string callerOwner, string targetOwner, string agentAlias, AclPermission required);

    // Rule management
    Task<List<AclRule>> GetRulesAsync();
    Task AddRuleAsync(AclRule rule);
    Task RemoveRuleAsync(AclRule rule);

    // Group management
    Task<Dictionary<string, HashSet<string>>> GetGroupsAsync();
    Task AddToGroupAsync(string groupName, string member);
    Task RemoveFromGroupAsync(string groupName, string member);
}

public class AclRule
{
    public string OwnerPattern { get; set; }    // Target agent's owner ("system", "*", "alice")
    public string AgentPattern { get; set; }    // Target agent's alias ("*", "automation_*")
    public string CallerPattern { get; set; }   // Who is allowed ("*", "group:admins", "alice")
    public AclPermission Permission { get; set; }
}

[Flags]
public enum AclPermission
{
    None      = 0,
    Message   = 1,   // Send messages
    Configure = 2,   // Create/reconfigure
    Read      = 4,   // Read threads, state, health
    Admin     = 8,   // Modify ACL rules
    All       = Message | Configure | Read | Admin
}

public record AclEvaluationResult(
    bool Allowed,
    AclPermission GrantedPermissions,
    string? DeniedReason = null);

public class FabrCoreAclOptions
{
    public List<AclRule> Rules { get; set; } = new();
    public Dictionary<string, List<string>> Groups { get; set; } = new();
}
```

See [acl-shared-agents.md](acl-shared-agents.md) for pattern matching rules, configuration, and usage examples.

## IClientContext

**Namespace:** `FabrCore.Client`

```csharp
public interface IClientContext
{
    string Handle { get; }
    bool IsDisposed { get; }

    event EventHandler<AgentMessage>? AgentMessageReceived;

    Task<AgentMessage> SendAndReceiveMessage(AgentMessage request);
    Task SendMessage(AgentMessage request);
    Task SendEvent(EventMessage request, string? streamName = null);

    // CreateAgent takes AgentConfiguration object (NOT individual params)
    Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration);

    Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic);
    Task<List<TrackedAgentInfo>> GetTrackedAgents();
    Task<bool> IsAgentTracked(string handle);

    // Discover shared agents this client has permission to access (via ACL)
    Task<List<AgentInfo>> GetAccessibleSharedAgents();
}
```

**CRITICAL:** `CreateAgent` takes a single `AgentConfiguration` object, NOT named parameters like `(handle, agentType, systemPrompt)`.

### Usage Example

```csharp
await _context.CreateAgent(new AgentConfiguration
{
    Handle = "my-agent",
    AgentType = "my-agent",
    SystemPrompt = "You are a helpful assistant."
});
```

## IClientContextFactory

**Namespace:** `FabrCore.Client`

```csharp
public interface IClientContextFactory
{
    Task<IClientContext> GetOrCreateAsync(string handle);  // Cached, factory-managed
    Task<IClientContext> CreateAsync(string handle);       // New each time, caller manages
    Task ReleaseAsync(string handle);                      // Dispose cached context
    bool HasContext(string handle);                        // Check cache
}
```

## IFabrCoreAgentHost

**Namespace:** `FabrCore.Sdk`

```csharp
public interface IFabrCoreAgentHost
{
    string GetHandle();
    Task<AgentMessage> SendAndReceiveMessage(AgentMessage request);
    Task SendMessage(AgentMessage request);
    Task<AgentHealthStatus> GetAgentHealth(string? handle = null, HealthDetailLevel detailLevel = HealthDetailLevel.Detailed);
    Task SendEvent(EventMessage request, string? streamName = null);
    void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period);
    void UnregisterTimer(string timerName);
    Task RegisterReminder(string reminderName, string messageType, string? message, TimeSpan dueTime, TimeSpan period);
    Task UnregisterReminder(string reminderName);
    FabrCoreChatHistoryProvider GetChatHistoryProvider(string threadId);
    void TrackChatHistoryProvider(FabrCoreChatHistoryProvider provider);
    Task<List<StoredChatMessage>> GetThreadMessagesAsync(string threadId);
    Task AddThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages);
    Task ClearThreadAsync(string threadId);
    Task ReplaceThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages);
    Task<Dictionary<string, JsonElement>> GetCustomStateAsync();
    Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes);
}
```

## Extension Methods

### FabrCore.Host

```csharp
// Server setup - called on WebApplicationBuilder
public static WebApplicationBuilder AddFabrCoreServer(
    this WebApplicationBuilder builder,
    FabrCoreServerOptions? options = null)

// Server middleware - called on WebApplication
public static WebApplication UseFabrCoreServer(
    this WebApplication app,
    FabrCoreServerOptions? options = null)

public class FabrCoreServerOptions
{
    public List<Assembly> AdditionalAssemblies { get; set; } = new();

    // Configure a custom agent management provider (default: OrleansAgentManagementProvider)
    public FabrCoreServerOptions UseAgentManagementProvider<T>()
        where T : class, IAgentManagementProvider;

    // Configure a custom ACL provider (default: InMemoryAclProvider)
    public FabrCoreServerOptions UseAclProvider<T>()
        where T : class, IAclProvider;
}
```

### FabrCore.Client

```csharp
// Client setup - called on IHostApplicationBuilder
public static IHostApplicationBuilder AddFabrCoreClient(
    this IHostApplicationBuilder builder)

// Client startup - returns IHost, NOT awaitable
public static IHost UseFabrCoreClient(this IHost app)

// Register ChatDock and other UI components
public static IServiceCollection AddFabrCoreClientComponents(
    this IServiceCollection services)
```

**CRITICAL:** `UseFabrCoreClient()` returns `IHost`, NOT `Task`. Do NOT use `await`:
```csharp
// CORRECT:
app.UseFabrCoreClient();

// WRONG:
await app.UseFabrCoreClient(); // This won't compile
```

## Microsoft.Agents.AI Types

These types come from the `Microsoft.Agents.AI` NuGet package (transitive dependency of FabrCore.Sdk).

### AIAgent

**Namespace:** `Microsoft.Agents.AI`

Abstract base class for all agents. `ChatClientAgent` is the concrete implementation used by FabrCore.

```csharp
public abstract partial class AIAgent
{
    public string Id { get; }
    public virtual string? Name { get; }
    public virtual string? Description { get; }

    // Run with different input types
    public Task<AgentResponse> RunAsync(AgentSession? session = null, ...);
    public Task<AgentResponse> RunAsync(string message, AgentSession? session = null, ...);
    public Task<AgentResponse> RunAsync(ChatMessage message, AgentSession? session = null, ...);
    public Task<AgentResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, ...);

    // Streaming variants
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(AgentSession? session = null, ...);
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(string message, AgentSession? session = null, ...);
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(ChatMessage message, AgentSession? session = null, ...);
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, ...);

    public ValueTask<AgentSession> CreateSessionAsync(CancellationToken ct = default);
}
```

### AgentSession

**Namespace:** `Microsoft.Agents.AI`

Abstract base for conversation sessions. Stores history and state.

```csharp
public abstract class AgentSession
{
    public AgentSessionStateBag StateBag { get; protected set; }
    public virtual object? GetService(Type serviceType, object? serviceKey = null);
}
```

### AgentResponseUpdate

**Namespace:** `Microsoft.Agents.AI`

Streaming response chunk:

```csharp
// Key property for reading streamed text:
update.Text  // string? - the text content of this chunk
```

### ChatClientAgent

**Namespace:** `Microsoft.Agents.AI`

Sealed concrete implementation of `AIAgent` that wraps an `IChatClient`:

```csharp
public sealed partial class ChatClientAgent : AIAgent
{
    public ChatClientAgent(
        IChatClient chatClient,
        string? instructions = null,
        string? name = null,
        string? description = null,
        IList<AITool>? tools = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null);

    public IChatClient ChatClient { get; }
}
```

Note: FabrCore's `CreateChatClientAgent()` helper creates this internally. You typically don't construct it directly.
