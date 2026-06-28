---
name: fabrcore-messaging
description: >
  FabrCore messaging, handle routing, inter-agent communication, orchestration patterns, and access control (ACL).
  Covers AgentMessage, EventMessage, HandleUtilities, SendMessage vs SendAndReceiveMessage vs SendEvent,
  fan-out/gather, pipeline, supervisor patterns, ACL rules, shared agents, and cross-user routing.
  Triggers on: "AgentMessage", "EventMessage", "handle routing", "HandleUtilities", "SendMessage",
  "SendAndReceiveMessage", "SendEvent", "inter-agent", "agent-to-agent", "multi-agent", "orchestration",
  "ACL", "shared agent", "access control", "MessageKind", "cross-user", "fan-out", "pipeline",
  "supervisor", "delegator", "AclRule", "AclPermission", "IAclProvider", "SystemMessageTypes",
  "FromHandle", "ToHandle", "OnBehalfOfHandle", "TraceId", "message routing", "storage user handle",
  "VerifiableExecutionEnvelope", "signed evidence", "EventPublished", "EventDelivered", "EventHandled",
  "RecordDbEffectAsync", "RecordHttpCallAsync", "RecordStorageEffectAsync", "RecordLibraryCallAsync".
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
    public string? ToHandle { get; set; }           // Target handle (bare agent handle or "userHandle:agentHandle")
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

    // W3C TraceContext — null until stamped at an ingress/send boundary.
    // See "Correlation and Tracing" below. Use AgentMessageTelemetry helpers
    // (StampFromActivity / StartIngressActivity) rather than setting these by hand.
    public string? TraceId { get; set; }        // 32-char lowercase hex
    public string? SpanId { get; set; }         // 16-char lowercase hex — publisher span
    public string? ParentSpanId { get; set; }   // 16-char lowercase hex — publisher's parent

    // Compact verifiable execution pointer/lineage. Full bundles live in the evidence store/API.
    public VerifiableExecutionEnvelope? VerifiableExecution { get; set; }

    // Create response with routing pre-filled. Copies TraceId but NOT SpanId/ParentSpanId —
    // the response is a new span; stamp it from its own Activity before returning.
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
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public VerifiableExecutionEnvelope? VerifiableExecution { get; set; }
}
```

## System Message Types

All `MessageType` values starting with `_` are reserved for FabrCore internal use:

```csharp
public static class SystemMessageTypes
{
    public const string Status = "_status";     // Heartbeat while an agent is processing
    public const string Thinking = "_thinking"; // Progress/thinking update for user-facing clients
    public const string Error = "_error";       // Error notification

    public static bool IsSystemMessage(string? messageType)
        => messageType != null && messageType.StartsWith('_');
}
```

**Automatic behavior:**
- `AgentGrain` sends `_status` heartbeats every 3 seconds during processing
- Agents and tools should use `_thinking` for progress updates intended for users
- On exception, sends `_error` to original sender then rethrows
- `ChatDock` shows `_status` and `_thinking` as thinking/progress indicators, `_error` as an error message
- Agent chat stream delivery ignores underscore-prefixed system messages before `OnMessage`/`OnMessageBusy`
- System messages are NOT stored in chat history
- Do not use non-prefixed message types such as `thinking` for FabrCore system/control traffic
- **Busy routing:** `OnMessage` is marked `[AlwaysInterleave]`, so a second message can enter the grain while the first is processing. The grain routes the concurrent message to `OnMessageBusy` instead of `OnMessage`. No heartbeat, compaction, or chat history flush occurs for busy-routed messages. The monitor records busy-routed messages with `BusyRouted = true`.

## Handle Routing

Handles use the format `"userHandle:agentHandle"` (e.g., `"user1:assistant"`).

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
var userHandle  = fabrcoreAgentHost.GetUserHandle();   // "user1"
var agent  = fabrcoreAgentHost.GetAgentHandle();   // "assistant"
var (o, a) = fabrcoreAgentHost.GetParsedHandle();  // ("user1", "assistant")
if (fabrcoreAgentHost.HasUserHandle()) { /* ... */ }
```

### Routing Rules

- **Bare agent handle** (no colon, e.g., `"assistant"`) — auto-prefixed with caller's user handle
- **Fully-qualified handle** (contains colon, e.g., `"user2:assistant"`) — used as-is for cross-user routing

### Where Resolution Happens

- **AgentGrain** — `ResolveTargetHandle()` normalizes `ToHandle` in all messaging methods
- **ChatDock** — Uses `HandleUtilities.EnsurePrefix` to build expected `FromHandle`
- **DirectMessageSender** — Requires fully-qualified handles (throws on bare agent handle)

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
// Cross-user:
var crossUserHandleRequest = new AgentMessage { ToHandle = "user2:analyst", Message = "Analyze this data" };
var reply = await fabrcoreAgentHost.SendAndReceiveMessage(crossUserHandleRequest);
```

### Events

```csharp
var eventMsg = new EventMessage
{
    Type = "status-changed",
    Namespace = "agent-status",
    Channel = "listener-agent",
    Data = "Agent status updated"
};
await fabrcoreAgentHost.SendEvent(eventMsg);
```

Agents subscribe to custom event streams with the same namespace/channel pair:

```csharp
var ns = "agent-status";
var channel = "listener-agent";

var config = new AgentConfiguration
{
    Streams = [EventStreamSubscription.For(ns, channel)]
};

await fabrcoreAgentHost.SendEvent(new EventMessage
{
    Namespace = ns,
    Channel = channel,
    Type = "status-changed"
});
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

## Correlation and Tracing (OpenTelemetry / W3C TraceContext)

`AgentMessage` and `EventMessage` carry W3C TraceContext fields so every hop (client → grain → downstream agent/event handler → response) stays in one trace:

| Field | Format | Meaning |
|---|---|---|
| `TraceId` | 32-char lowercase hex | The whole trace — stable across every hop |
| `SpanId` | 16-char lowercase hex | The span that published *this* message |
| `ParentSpanId` | 16-char lowercase hex | The publisher span's parent (null if root) |

All three are null until a boundary stamps them. Do not hand-roll GUIDs into these fields — the helpers in `FabrCore.Core.AgentMessageTelemetry` do the right thing and keep the values W3C-valid.

## Verifiable Execution Envelope

`AgentMessage.VerifiableExecution` and `EventMessage.VerifiableExecution` carry compact evidence pointers, not full audit history. The full signed bundle is stored through `IVerifiableExecutionStore` and exported through `/fabrcoreapi/monitor/verifiable-execution`.

Current host capture points:

- client/agent dispatch (`AgentDispatch`)
- agent inbound/outbound messages (`MessageInbound`, `MessageOutbound`, `AgentResponse`)
- event publish/delivery/handler completion (`EventPublished`, `EventDelivered`, `EventHandled`)
- LLM calls (`LlmCall`)
- plugin/tool invocations automatically, plus DB/API/storage/library side effects when recorded with `FabrCore.Sdk.VerifiableExecution` helpers

Use the `fabrcore-spiffe` skill for the full trust model, setup, cross-cluster flow, and pitfalls.

### Helpers (`AgentMessageTelemetry`)

```csharp
using FabrCore.Core;   // brings the extension methods into scope

// 1. Stamp outbound — before sending a message, copy IDs from the current Activity
message.StampFromActivity(Activity.Current);

// 2. Parse inbound — rebuild an ActivityContext from a message's trace fields
if (message.TryGetParentContext(out var parentCtx)) { /* parentCtx.IsRemote == true */ }

// 3. Ingress boundary — start an Activity that parents on the message, with fallback
using var activity = message.StartIngressActivity(
    MyActivitySource,
    name: "OnMessage",
    kind: ActivityKind.Server,
    outerParent: default);   // optional: supply e.g. a traceparent header context
```

**`StartIngressActivity` parent precedence:** (1) the message's own `TraceId`/`SpanId` → (2) `outerParent` (e.g. extracted from a `traceparent` HTTP header) → (3) new root. If the message was unstamped, the new Activity's IDs are stamped back onto it so downstream hops can parent on it automatically.

### Response semantics

`AgentMessage.Response()` copies `TraceId` so the response stays in the same trace, but it does **not** copy `SpanId`/`ParentSpanId` — a response is conceptually its own span. If you return a response from a scope with an active `Activity`, stamp it:

```csharp
var response = request.Response();
response.Message = "done";
response.StampFromActivity(Activity.Current);
return response;
```

`AgentGrain` already does this for you at its `OnMessage`/`OnMessageBusy` ingress — see `src/FabrCore.Host/Grains/AgentGrain.cs:569,673,724,795`.

### Where FabrCore already stamps for you

| Component | ActivitySource | Behavior |
|---|---|---|
| `AgentGrain.OnMessage` / `OnMessageBusy` | `FabrCore.Host.AgentGrain` | Calls `StartIngressActivity` on inbound, `StampFromActivity` on outbound response |
| `FabrCoreAgentProxy.InternalOnMessage` (your agent) | `FabrCore.Sdk.AgentProxy` | Runs inside the grain's Activity; your `ActivitySource.StartActivity(...)` auto-parents |
| `WebSocketSession` | `FabrCore.Host.WebSocketSession` | Accepts upstream `traceparent` header, passes as `outerParent`, stamps responses |
| `FabrCoreAgentService` (host-side fire-and-forget) | `FabrCore.Host.*` | Stamps outbound messages before `stream.OnNextAsync` |
| `ClientContext.SendAndReceiveMessage` / `SendMessage` | `FabrCore.Client.ClientContext` | Wraps the call in an Activity (tags + metrics). **Does NOT auto-stamp the outbound `AgentMessage`** — if you need the receiving grain to parent its Activity on your client span via the message, call `message.StampFromActivity(Activity.Current)` yourself before sending. |

### Viewing spans (exporter setup)

FabrCore depends only on `OpenTelemetry.Api` — no exporter is bundled. Register your own TracerProvider to see spans in Jaeger / OTLP / Console. See **fabrcore-server → OpenTelemetry exporter setup** for the wiring.

For message-level observability (who sent what to whom, without needing an external exporter), see **fabrcore-agentmonitor** — `MonitoredMessage.TraceId` is the same `TraceId` stamped here, so the in-process monitor and your external trace viewer are joinable by `TraceId`.

## Access Control (ACL)

### Overview

The ACL system controls which clients can access agents under other user handles. By default, agents are scoped to their user handle.

### Implicit Rules

- **Same-user agent access is always allowed** — zero-overhead short-circuit
- **Default seed rule:** If no rules configured, `system:* -> * -> Message,Read` is seeded

### ACL Rule Structure

```csharp
public class AclRule
{
    public string UserHandlePattern { get; set; }   // Target agent's user handle
    public string AgentPattern { get; set; }   // Target agent's alias
    public string CallerPattern { get; set; }  // Who is allowed
    public AclPermission Permission { get; set; }
}
```

### Pattern Matching

| Pattern | Matches | Example |
|---------|---------|---------|
| `"*"` | Anything | All user handles/agents/callers |
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
        "UserHandlePattern": "system",
        "AgentPattern": "*",
        "CallerPattern": "*",
        "Permission": "Message,Read"
      },
      {
        "UserHandlePattern": "shared",
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

1. **Same-user agent check** — caller == target user handle → allow with `All` permissions
2. **Rule scan** — first match wins
3. **No match** → deny

### Enforcement Points

| Method | Permission Required | Notes |
|--------|-------------------|-------|
| `SendAndReceiveMessage` | `Message` | Checked in ClientGrain |
| `SendMessage` | `Message` | Checked in ClientGrain |
| `CreateAgent` | `Configure` | Cross-user only |

Agent-to-agent communication within the cluster is **trusted** and bypasses ACL.

### Storage user handle partitioning

Typed entity storage is not message routing, but it uses the same user handle discipline. The Storage API requires `x-user-handle`; that value is the user handle partition for `container/entityKey`. Treat it as an ACL boundary:

- User data should use the user handle as `x-user-handle`.
- Agent-associated shared data should use the owning agent/user partition deliberately.
- The same `container/entityKey` can exist independently under different user handles.
- Do not use user-handle-free Host `IFabrCoreStorageProvider` calls for user data; those are system-scoped.

### Custom ACL Provider

```csharp
public interface IAclProvider
{
    Task<AclEvaluationResult> EvaluateAsync(
        string callerUserHandle, string targetUserHandle, string agentHandle, AclPermission required);
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
    UserHandlePattern = "system",
    AgentPattern = "premium_*",
    CallerPattern = "group:premium",
    Permission = AclPermission.Message | AclPermission.Read
});
await aclProvider.AddToGroupAsync("premium", "newuser123");
```

Note: Default `InMemoryAclProvider` does not persist runtime changes.
