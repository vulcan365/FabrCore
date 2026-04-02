---
name: fabrcore-messaging
description: >
  FabrCore messaging, handle routing, inter-agent communication, orchestration patterns, and access control (ACL).
  Covers AgentMessage, EventMessage, HandleUtilities, SendMessage vs SendAndReceiveMessage vs SendEvent,
  fan-out/gather, pipeline, supervisor patterns, ACL rules, shared agents, and cross-owner routing.
  Triggers on: "AgentMessage", "EventMessage", "handle routing", "HandleUtilities", "SendMessage",
  "SendAndReceiveMessage", "SendEvent", "inter-agent", "agent-to-agent", "multi-agent", "orchestration",
  "ACL", "shared agent", "access control", "MessageKind", "cross-owner", "fan-out", "pipeline",
  "supervisor", "delegator", "AclRule", "AclPermission", "IAclProvider", "SystemMessageTypes",
  "FromHandle", "ToHandle", "OnBehalfOfHandle", "TraceId", "message routing".
  Do NOT use for: agent lifecycle — use fabrcore-agent.
  Do NOT use for: ChatDock — use fabrcore-chatdock.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# FabrCore Messaging & Access Control

## AgentMessage Structure

All agent communication uses `AgentMessage`:

```csharp
public class AgentMessage
{
    // Routing
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? ToHandle { get; set; }           // Target handle (bare alias or "owner:agent")
    public string? FromHandle { get; set; }         // Sender (auto-filled by AgentGrain/ClientContext)
    public string? OnBehalfOfHandle { get; set; }   // Original requester (for delegation)
    public string? DeliverToHandle { get; set; }    // Final delivery target
    public string? Channel { get; set; }            // Optional channel identifier

    // Content
    public string? Message { get; set; }            // Text content
    public string? MessageType { get; set; }        // Custom type ("_" prefix reserved for system)
    public MessageKind Kind { get; set; } = MessageKind.Request;

    // Payload
    public string? DataType { get; set; }
    public byte[]? Data { get; set; }
    public List<string> Files = new();

    // Metadata
    public Dictionary<string, string>? State { get; set; } = new();
    public Dictionary<string, string>? Args { get; set; } = new();
    public string? TraceId { get; set; } = Guid.NewGuid().ToString();

    // Create response with routing pre-filled
    public AgentMessage Response();
}

public enum MessageKind
{
    Request = 0,   // Expects a response
    OneWay = 1,    // Fire-and-forget
    Response = 2   // Reply to a request
}
```

## EventMessage Structure

CloudEvents-inspired message for fire-and-forget event delivery:

```csharp
public class EventMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; }               // Event type (e.g. "order.created")
    public string Source { get; set; }              // Producer handle
    public string? Subject { get; set; }
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;

    // Routing
    public string Namespace { get; set; }           // Stream namespace
    public string Channel { get; set; }             // Channel within namespace

    // Payload
    public string? Data { get; set; }
    public string? DataContentType { get; set; }
    public byte[]? BinaryData { get; set; }

    // Extensions
    public Dictionary<string, string>? Args { get; set; }
    public string? TraceId { get; set; } = Guid.NewGuid().ToString();
}
```

## System Message Types

All `MessageType` values starting with `_` are reserved for FabrCore internal use:

```csharp
public static class SystemMessageTypes
{
    public const string Status = "_status";   // Thinking heartbeat (every 3s)
    public const string Error = "_error";     // Error notification

    public static bool IsSystemMessage(string? messageType)
        => messageType != null && messageType.StartsWith('_');
}
```

**Automatic behavior:**
- `AgentGrain` sends `_status` heartbeats every 3 seconds during processing
- On exception, sends `_error` to original sender then rethrows
- `ChatDock` shows `_status` as thinking indicator, `_error` as error message
- System messages are NOT stored in chat history

## Handle Routing

Handles use the format `"owner:agentAlias"` (e.g., `"user1:assistant"`).

### HandleUtilities API

```csharp
HandleUtilities.BuildPrefix("user1");                    // "user1:"
HandleUtilities.EnsurePrefix("assistant", "user1:");     // "user1:assistant"
HandleUtilities.EnsurePrefix("user2:assistant", "user1:"); // "user2:assistant" (unchanged)
HandleUtilities.StripPrefix("user1:assistant", "user1:"); // "assistant"
HandleUtilities.ParseHandle("user1:assistant");           // ("user1", "assistant")
HandleUtilities.ParseHandle("assistant");                 // ("", "assistant")
```

### IFabrCoreAgentHost Handle Methods

Agents and plugins can access their own handle components directly:

```csharp
var full   = fabrcoreAgentHost.GetHandle();        // "user1:assistant"
var owner  = fabrcoreAgentHost.GetOwnerHandle();   // "user1"
var agent  = fabrcoreAgentHost.GetAgentHandle();   // "assistant"
var (o, a) = fabrcoreAgentHost.GetParsedHandle();  // ("user1", "assistant")
if (fabrcoreAgentHost.HasOwner()) { /* ... */ }
```

### Routing Rules

- **Bare alias** (no colon, e.g., `"assistant"`) — auto-prefixed with caller's owner
- **Fully-qualified handle** (contains colon, e.g., `"user2:assistant"`) — used as-is for cross-owner routing

### Where Resolution Happens

- **AgentGrain** — `ResolveTargetHandle()` normalizes `ToHandle` in all messaging methods
- **ChatDock** — Uses `HandleUtilities.EnsurePrefix` to build expected `FromHandle`
- **DirectMessageSender** — Requires fully-qualified handles (throws on bare alias)

## Messaging Patterns

Two messaging methods available on both `IClientContext` and `IFabrCoreAgentHost`:

| Method | Behavior | Preferred For |
|--------|----------|---------------|
| `SendMessage(AgentMessage)` | Fire-and-forget. Response via stream/observer. | **Client-to-agent** |
| `SendAndReceiveMessage(AgentMessage)` | Async RPC. Blocks until response. | **Agent-to-agent** |
| `SendEvent(EventMessage)` | Fire-and-forget event. No response. | Event broadcasting |

### Client-to-Agent

```csharp
var request = new AgentMessage { ToHandle = "my-agent", Message = "Hello!" };
await context.SendMessage(request);
// Response arrives via AgentMessageReceived event
context.AgentMessageReceived += (sender, response) => { /* process */ };
```

### Agent-to-Agent (Request-Response)

```csharp
var request = new AgentMessage { ToHandle = "analyst", Message = "Analyze this data" };
var reply = await fabrcoreAgentHost.SendAndReceiveMessage(request);
// Cross-owner:
var crossOwnerRequest = new AgentMessage { ToHandle = "user2:analyst", Message = "Analyze this data" };
var reply = await fabrcoreAgentHost.SendAndReceiveMessage(crossOwnerRequest);
```

### Events

```csharp
var eventMsg = new EventMessage
{
    Type = "status-changed",
    Channel = "listener-agent",
    Data = "Agent status updated"
};
await fabrcoreAgentHost.SendEvent(eventMsg);
```

## Orchestration Patterns

### Delegator / Router

Routes incoming requests to specialized agents:

```csharp
[AgentAlias("router")]
public class RouterAgent : FabrCoreAgentProxy
{
    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        string target = message.MessageType switch
        {
            "code-review" => "code-reviewer",
            "writing" => "writer",
            _ => "general-assistant"
        };
        var reply = await fabrcoreAgentHost.SendAndReceiveMessage(target, message);
        response.Message = reply.Message;
        return response;
    }
}
```

### Fan-Out / Gather

Send to multiple agents in parallel and aggregate:

```csharp
[AgentAlias("aggregator")]
public class AggregatorAgent : FabrCoreAgentProxy
{
    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var tasks = new[]
        {
            fabrcoreAgentHost.SendAndReceiveMessage("analyst-1", message),
            fabrcoreAgentHost.SendAndReceiveMessage("analyst-2", message),
            fabrcoreAgentHost.SendAndReceiveMessage("analyst-3", message)
        };
        var replies = await Task.WhenAll(tasks);
        response.Message = string.Join("\n\n", replies.Select(r => r.Message));
        return response;
    }
}
```

### Chain / Pipeline

Process through a sequence of agents:

```csharp
[AgentAlias("pipeline")]
public class PipelineAgent : FabrCoreAgentProxy
{
    private readonly string[] _stages = ["extract", "transform", "validate", "load"];

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var current = message;
        foreach (var stage in _stages)
            current = await fabrcoreAgentHost.SendAndReceiveMessage($"{stage}-agent", current);
        response.Message = current.Message;
        return response;
    }
}
```

### Progress Updates

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    await fabrcoreAgentHost.SendMessage(new AgentMessage
    {
        ToHandle = message.FromHandle,
        Message = "Starting analysis...",
        Kind = MessageKind.OneWay
    });
    // Do work...
    var response = message.Response();
    response.Message = "Analysis complete";
    return response;
}
```

## Message Metadata

```csharp
var message = new AgentMessage
{
    Message = "Process order",
    State = new() { ["priority"] = "high", ["source"] = "web-api" },
    Args = new() { ["orderId"] = "ORD-123" },
};
```

## Correlation and Tracing

```csharp
var message = new AgentMessage
{
    Message = "Process this",
    TraceId = Activity.Current?.Id ?? Guid.NewGuid().ToString()
};
```

FabrCore automatically instruments agent calls with OpenTelemetry.

## Access Control (ACL)

### Overview

The ACL system controls which clients can access agents owned by other users. By default, agents are scoped to their owner.

### Implicit Rules

- **Own-agent access is always allowed** — zero-overhead short-circuit
- **Default seed rule:** If no rules configured, `system:* -> * -> Message,Read` is seeded

### ACL Rule Structure

```csharp
public class AclRule
{
    public string OwnerPattern { get; set; }   // Target agent's owner
    public string AgentPattern { get; set; }   // Target agent's alias
    public string CallerPattern { get; set; }  // Who is allowed
    public AclPermission Permission { get; set; }
}
```

### Pattern Matching

| Pattern | Matches | Example |
|---------|---------|---------|
| `"*"` | Anything | All owners/agents/callers |
| `"prefix*"` | Starts-with | `"automation_*"` matches `"automation_agent-123"` |
| `"group:name"` | Group members (CallerPattern only) | `"group:admins"` |
| `"exact"` | Case-insensitive literal | `"system"` |

### Permissions

```csharp
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
```

### Configuration in fabrcore.json

```json
{
  "Acl": {
    "Rules": [
      {
        "OwnerPattern": "system",
        "AgentPattern": "*",
        "CallerPattern": "*",
        "Permission": "Message,Read"
      },
      {
        "OwnerPattern": "shared",
        "AgentPattern": "analytics_*",
        "CallerPattern": "group:premium",
        "Permission": "Message,Read"
      }
    ],
    "Groups": {
      "admins": ["alice", "bob"],
      "premium": ["alice", "charlie"]
    }
  }
}
```

### Evaluation Order

1. **Own-agent check** — caller == target owner → allow with `All` permissions
2. **Rule scan** — first match wins
3. **No match** → deny

### Enforcement Points

| Method | Permission Required | Notes |
|--------|-------------------|-------|
| `SendAndReceiveMessage` | `Message` | Checked in ClientGrain |
| `SendMessage` | `Message` | Checked in ClientGrain |
| `CreateAgent` | `Configure` | Cross-owner only |

Agent-to-agent communication within the cluster is **trusted** and bypasses ACL.

### Custom ACL Provider

```csharp
public interface IAclProvider
{
    Task<AclEvaluationResult> EvaluateAsync(
        string callerOwner, string targetOwner, string agentAlias, AclPermission required);
    Task<List<AclRule>> GetRulesAsync();
    Task AddRuleAsync(AclRule rule);
    Task RemoveRuleAsync(AclRule rule);
    Task<Dictionary<string, HashSet<string>>> GetGroupsAsync();
    Task AddToGroupAsync(string groupName, string member);
    Task RemoveFromGroupAsync(string groupName, string member);
}
```

Register: `options.UseAclProvider<SqlAclProvider>()`

### Runtime Rule Management

```csharp
var aclProvider = serviceProvider.GetRequiredService<IAclProvider>();
await aclProvider.AddRuleAsync(new AclRule
{
    OwnerPattern = "system",
    AgentPattern = "premium_*",
    CallerPattern = "group:premium",
    Permission = AclPermission.Message | AclPermission.Read
});
await aclProvider.AddToGroupAsync("premium", "newuser123");
```

Note: Default `InMemoryAclProvider` does not persist runtime changes.
