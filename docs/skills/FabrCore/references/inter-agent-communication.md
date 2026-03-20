# Inter-Agent Communication

## Overview

FabrCore agents communicate through `IFabrCoreAgentHost`, which provides three communication patterns:

| Method | Behavior | Preferred For |
|--------|----------|---------------|
| `SendAndReceiveMessage` | Async RPC — blocks until target agent responds | **Agent-to-agent** (recommended) |
| `SendMessage` | Fire-and-forget — response arrives via stream/observer | **Client-to-agent** |
| `SendEvent` | Fire-and-forget event — no response expected | Event broadcasting |

For agent-to-agent, prefer `SendAndReceiveMessage` because agents typically need the response to continue processing. For client-to-agent, prefer `SendMessage` because responses arrive asynchronously via the `AgentMessageReceived` event on `ClientContext`.

## Handle Routing

All messaging methods accept bare aliases or fully-qualified handles:

- **Bare alias** (e.g., `"analyst"`) — `AgentGrain.ResolveTargetHandle()` auto-prefixes with the caller's owner. An agent owned by `user1` sending to `"analyst"` routes to `"user1:analyst"`.
- **Fully-qualified** (e.g., `"user2:analyst"`) — Used as-is for cross-owner routing.

`FromHandle` is auto-filled by `AgentGrain` if not set.

```csharp
// Same-owner: bare alias resolves to "user1:analyst"
await fabrAgentHost.SendAndReceiveMessage("analyst", request);

// Cross-owner: fully-qualified handle used as-is
await fabrAgentHost.SendAndReceiveMessage("user2:analyst", request);
```

The `HandleUtilities` class centralizes this logic:
```csharp
HandleUtilities.EnsurePrefix("analyst", "user1:");       // "user1:analyst"
HandleUtilities.EnsurePrefix("user2:analyst", "user1:");  // "user2:analyst" (unchanged)
HandleUtilities.BuildPrefix("user1");                      // "user1:"
HandleUtilities.StripPrefix("user1:analyst", "user1:");    // "analyst"
```

## Request-Response

The most common pattern. The calling agent blocks until the target agent responds.

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var response = message.Response();

    // Call another agent — bare alias auto-resolves to same owner
    var request = new AgentMessage
    {
        Message = "Analyze this data: " + message.Message,
        MessageKind = MessageKind.Request,
        State = new Dictionary<string, string>
        {
            ["format"] = "json"
        }
    };

    var reply = await fabrAgentHost.SendAndReceiveMessage("analyst-agent", request);
    response.Message = "Analysis result: " + reply.Message;

    return response;
}
```

## Fire-and-Forget

Send a message without waiting for a response. Used for notifications, logging, or background tasks.

```csharp
// Notify another agent without blocking — bare alias auto-resolves
var notification = new AgentMessage
{
    Message = "Task completed successfully",
    MessageType = "task-complete",
    MessageKind = MessageKind.OneWay,
    Data = new Dictionary<string, string>
    {
        ["taskId"] = "12345",
        ["status"] = "completed"
    }
};

await fabrAgentHost.SendMessage("monitor-agent", notification);
```

## Events (Stream-Based)

Events are broadcast via Orleans streams. The target agent receives them in `OnEvent()`.

```csharp
// Send an event — bare alias auto-resolves
var eventMsg = new AgentMessage
{
    MessageType = "status-changed",
    Message = "Agent status updated",
    MessageKind = MessageKind.OneWay
};

await fabrAgentHost.SendEvent("listener-agent", eventMsg);
```

The target agent handles events in `OnEvent()`:

```csharp
public override Task OnEvent(AgentMessage eventMessage)
{
    switch (eventMessage.MessageType)
    {
        case "status-changed":
            // Handle status change
            break;
        case "data-updated":
            // Handle data update
            break;
    }
    return Task.CompletedTask;
}
```

## Orchestration Patterns

### Delegator Agent

Routes incoming requests to specialized agents:

```csharp
[AgentAlias("router")]
public class RouterAgent : FabrCoreAgentProxy
{
    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        // Determine which specialist to route to
        string target = DetermineTarget(message);

        // Forward the message
        var reply = await fabrAgentHost.SendAndReceiveMessage(target, message);
        response.Message = reply.Message;

        return response;
    }

    private string DetermineTarget(AgentMessage message)
    {
        // Route based on message type, content, or args
        return message.MessageType switch
        {
            "code-review" => "code-reviewer",
            "writing" => "writer",
            _ => "general-assistant"
        };
    }
}
```

### Fan-Out / Gather

Send to multiple agents in parallel and aggregate results:

```csharp
[AgentAlias("aggregator")]
public class AggregatorAgent : FabrCoreAgentProxy
{
    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        // Fan out to multiple agents
        var tasks = new[]
        {
            fabrAgentHost.SendAndReceiveMessage("analyst-1", message),
            fabrAgentHost.SendAndReceiveMessage("analyst-2", message),
            fabrAgentHost.SendAndReceiveMessage("analyst-3", message)
        };

        var replies = await Task.WhenAll(tasks);

        // Aggregate results
        var combined = string.Join("\n\n", replies.Select(r => r.Message));
        response.Message = $"Combined analysis:\n{combined}";

        return response;
    }
}
```

### Chain / Pipeline

Process a message through a sequence of agents:

```csharp
[AgentAlias("pipeline")]
public class PipelineAgent : FabrCoreAgentProxy
{
    private readonly string[] _stages = ["extract", "transform", "validate", "load"];

    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();
        var currentMessage = message;

        foreach (var stage in _stages)
        {
            currentMessage = await fabrAgentHost.SendAndReceiveMessage(
                $"{stage}-agent", currentMessage);
        }

        response.Message = currentMessage.Message;
        return response;
    }
}
```

### Supervisor / Worker

A supervisor creates and manages worker agents dynamically:

```csharp
[AgentAlias("supervisor")]
public class SupervisorAgent : FabrCoreAgentProxy
{
    public override async Task<AgentMessage> OnMessage(AgentMessage message)
    {
        var response = message.Response();

        // Create a worker agent dynamically
        var workerConfig = new AgentConfiguration
        {
            Handle = $"worker-{Guid.NewGuid():N}",
            AgentType = "worker",
            Models = ["default"],
            SystemPrompt = "Process this specific task."
        };

        // Use the host API to create the worker (via plugin or direct grain call)
        // Then send the task
        var result = await fabrAgentHost.SendAndReceiveMessage(
            workerConfig.Handle, message);

        response.Message = result.Message;
        return response;
    }
}
```

## Message Metadata

Use `State`, `Args`, and `Data` dictionaries to pass structured information:

```csharp
var message = new AgentMessage
{
    Message = "Process order",
    MessageKind = MessageKind.Request,

    // Metadata about the request
    State = new Dictionary<string, string>
    {
        ["priority"] = "high",
        ["source"] = "web-api"
    },

    // Parameters for the agent
    Args = new Dictionary<string, string>
    {
        ["orderId"] = "ORD-123",
        ["action"] = "fulfill"
    },

    // Structured data payload
    Data = new Dictionary<string, string>
    {
        ["items"] = "[{\"sku\":\"ABC\",\"qty\":2}]",
        ["customer"] = "{\"name\":\"John\",\"email\":\"john@example.com\"}"
    }
};
```

## File Attachments

Pass files between agents using the `Files` dictionary:

```csharp
var message = new AgentMessage
{
    Message = "Analyze this document",
    Files = new Dictionary<string, byte[]>
    {
        ["report.pdf"] = await File.ReadAllBytesAsync("report.pdf"),
        ["data.csv"] = Encoding.UTF8.GetBytes(csvContent)
    }
};

var reply = await fabrAgentHost.SendAndReceiveMessage("document-analyzer", message);
```

## Correlation and Tracing

Use `TraceId` for distributed tracing across agent calls:

```csharp
var message = new AgentMessage
{
    Message = "Process this",
    TraceId = Activity.Current?.Id ?? Guid.NewGuid().ToString()
};
```

FabrCore automatically instruments agent calls with OpenTelemetry when tracing is enabled.
