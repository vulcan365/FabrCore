---
name: fabrcore-messaging
description: >
  FabrCore messages, handle routing, inter-agent communication, tracing, verifiable-execution
  lineage, and multi-agent orchestration. Use for AgentMessage, EventMessage, HandleUtilities,
  SendMessage, SendAndReceiveMessage, SendEvent, MessageKind, FromHandle, ToHandle,
  OnBehalfOfHandle, DeliverToHandle, Channel, system messages, shared/cross-principal agents,
  fan-out, pipeline, supervisor/delegator patterns, TraceId, and signed event/effect evidence.
  Use fabrcore-agent for lifecycle; fabrcore-acl for grants, roles/groups, enforcement, or audit;
  and fabrcore-principal-delivery for SendToUserAsync, durable proactive/out-of-turn
  agent-to-principal delivery, external relay providers, endpoint context, or delivery outboxes.
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
    public string? ToHandle { get; set; }           // Target handle (bare agent handle or "principalHandle:agentHandle")
    public string? FromHandle { get; set; }         // Sender (auto-filled by AgentGrain/PrincipalGrain ingress)
    public string? OnBehalfOfHandle { get; set; }   // Original requester (for delegation)
    public string? DeliverToHandle { get; set; }    // Final delivery target
    public string? Channel { get; set; }            // Optional channel identifier
    public PrincipalDeliveryTarget? DeliveryTarget { get; set; } // Optional external relay/endpoint

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

    // True when MessageType is a reserved FabrCore system/control message type.
    public bool IsSystemMessage => SystemMessageTypes.IsSystemMessage(MessageType);

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

Use `message.IsSystemMessage` when you have an `AgentMessage` instance. It is the canonical, intention-revealing check used by `AgentGrain` before normal chat delivery:

```csharp
if (message.IsSystemMessage)
{
    // Render as progress/control UI, record separately, or skip normal chat handling.
}
```

Use `SystemMessageTypes.IsSystemMessage(messageType)` when you only have a `MessageType` string, such as from monitor snapshots, logs, database rows, or DTOs that are not `AgentMessage`.

**Automatic behavior:**
- `AgentGrain` sends `_status` heartbeats every 3 seconds during processing
- Agents and tools should use `_thinking` for progress updates intended for users
- On exception, sends `_error` to original sender then rethrows
- WebSocket clients can show `_status` and `_thinking` as thinking/progress indicators, `_error` as an error message
- Agent chat stream delivery ignores underscore-prefixed system messages before `OnMessage`/`OnMessageBusy`
- System messages are NOT stored in chat history
- Do not use non-prefixed message types such as `thinking` for FabrCore system/control traffic
- **Busy routing:** `OnMessage` is marked `[AlwaysInterleave]`, so a second message can enter the grain while the first is processing. The grain routes the concurrent message to `OnMessageBusy` instead of `OnMessage`. No heartbeat, compaction, or chat history flush occurs for busy-routed messages. The monitor records busy-routed messages with `BusyRouted = true`.

## Handle Routing

Handles use the format `"principalHandle:agentHandle"` (e.g., `"principal1:assistant"`).

### HandleUtilities API

```csharp
HandleUtilities.BuildPrefix("principal1");                         // "principal1:"
HandleUtilities.EnsurePrefix("assistant", "principal1:");          // "principal1:assistant"
HandleUtilities.EnsurePrefix("principal2:assistant", "principal1:"); // "principal2:assistant" (unchanged)
HandleUtilities.StripPrefix("principal1:assistant", "principal1:"); // "assistant"
HandleUtilities.ParseHandle("principal1:assistant");                // ("principal1", "assistant")
HandleUtilities.ParseHandle("assistant");                 // ("", "assistant")
```

### IFabrCoreAgentHost Handle Methods

Agents and plugins can access their own handle components directly:

Compatibility naming: `GetUserHandle()` and `HasUserHandle()` are legacy method names. They return/check the principal handle prefix used for routing and ACL boundaries.

```csharp
var full   = fabrcoreAgentHost.GetHandle();             // "principal1:assistant"
var principalHandle = fabrcoreAgentHost.GetUserHandle(); // "principal1" (principal handle)
var agent  = fabrcoreAgentHost.GetAgentHandle();   // "assistant"
var (principalHandle, agentHandle) = fabrcoreAgentHost.GetParsedHandle(); // ("principal1", "assistant")
if (fabrcoreAgentHost.HasUserHandle()) { /* ... */ }
```

### Routing Rules

- **Bare agent handle** (no colon, e.g., `"assistant"`) — auto-prefixed with caller's principal handle
- **Fully-qualified handle** (contains colon, e.g., `"principal2:assistant"`) — used as-is for cross-principal routing

### Where Resolution Happens

- **AgentGrain** — `ResolveTargetHandle()` normalizes `ToHandle` in all messaging methods
- **Host HTTP/WebSocket ingress** — Uses `HandleUtilities.EnsurePrefix` to build expected `FromHandle`

## Messaging Patterns

Two messaging methods are available through `IFabrCoreAgentHost` and host ingress paths:

| Method | Behavior | Preferred For |
|--------|----------|---------------|
| `SendMessage(AgentMessage)` | Fire-and-forget. Response via stream/observer. | **Client-to-agent** |
| `SendAndReceiveMessage(AgentMessage)` | Async RPC. Blocks until response. | **Agent-to-agent** |
| `SendEvent(EventMessage)` | Fire-and-forget event. No response. | Event broadcasting |

These methods route ordinary client, agent, and event traffic. When an agent must notify its
own principal while no user turn is active, use the protected `SendToUserAsync(...)` helper.
That path preserves the message durably and selects an installed external relay; it is not a
substitute for `DeliverToHandle` or `Channel`. See **fabrcore-principal-delivery** for agent API,
provider contracts, delivery semantics, and M365/SMS/email/push/webhook implementations.

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
// Cross-principal:
var crossPrincipalHandleRequest = new AgentMessage { ToHandle = "principal2:analyst", Message = "Analyze this data" };
var reply = await fabrcoreAgentHost.SendAndReceiveMessage(crossPrincipalHandleRequest);
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
        MessageType = SystemMessageTypes.Thinking,
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

### Viewing spans (exporter setup)

FabrCore depends only on `OpenTelemetry.Api` — no exporter is bundled. Register your own TracerProvider to see spans in Jaeger / OTLP / Console. See **fabrcore-server → OpenTelemetry exporter setup** for the wiring.

For message-level observability (who sent what to whom, without needing an external exporter), see **fabrcore-agentmonitor** — `MonitoredMessage.TraceId` is the same `TraceId` stamped here, so the in-process monitor and your external trace viewer are joinable by `TraceId`.

## Access Control (ACL)

Access control is documented in **fabrcore-acl** — principals, roles, groups, permission grants
in 3-dot notation (`agent.message.allow` / `agent.create.deny`), enforcement modes
(Disabled/AuditOnly/Enforce), the ACL management API, and the security audit provider. Summary
of what matters for messaging:

- **Same-principal traffic is implicitly allowed**; cross-principal traffic is **denied by
  default** until a `PermissionGrant` allows it (deny overrides allow).
- **Principal-initiated sends** (`PrincipalGrain.SendAndReceiveMessage`/`SendMessage`/`SendEvent`)
  require `agent.message.allow` on the target; `CreateAgent`/`ResetAgent`/`UntrackAgent` require
  `agent.create`/`agent.reconfigure`/`agent.destroy`.
- **Agent-to-agent hops within a principal are trusted; cross-principal a2a hops are
  ACL-checked sender-side** (the acting principal derives from the sending grain's key, never
  from the spoofable `FromHandle`). Unauthorized sends throw `AclDeniedException` in Enforce mode.
- **Transitive fan-out is audited, not blocked**: the first cross-principal hop stamps
  `AgentMessage.CrossPrincipalOrigin`/`CrossPrincipalHops`; chains that cross further principal
  boundaries emit `BoundaryCrossing` audit events and log warnings.
- `Kind == Response` and system messages (`_status`/`_error`) are exempt from the a2a check so
  request/reply round-trips can't be broken.

Grant shapes for cross-talk (see fabrcore-acl for the full model):

```json
{ "Subject": "agent:p1:agent1", "Permission": "agent.message.allow", "Resource": "p2:agent3" }
{ "Subject": "principal:p1",    "Permission": "agent.message.allow", "Resource": "p2:*" }
{ "Subject": "principal:p1",    "Permission": "agent.message.allow", "Resource": "*:agent5" }
```

### Storage principal handle partitioning

Typed entity storage is not message routing, but it uses the same principal handle discipline. The Storage API requires `x-user-handle`; that legacy header name carries the principal handle partition for `container/entityKey`. Treat it as an ACL boundary:

- Principal data should use the principal handle as `x-user-handle`.
- Agent-associated shared data should deliberately use the owning agent or principal partition.
- The same `container/entityKey` can exist independently under different principal handles.
- Do not use principal-handle-free Host `IFabrCoreStorageProvider` calls for principal-scoped data; those are system-scoped.
