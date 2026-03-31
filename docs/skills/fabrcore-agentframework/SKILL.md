---
name: fabrcore-agentframework
description: >
  Microsoft Agent Framework usage within FabrCore — AIAgent, AgentSession, ChatClientAgent, ChatClientAgentOptions,
  AgentResponse, AgentResponseUpdate, thread management patterns, RunAsync vs RunStreamingAsync,
  session serialization, agent-as-tool composition, middleware (AIAgentBuilder),
  and Microsoft.Extensions.AI abstractions (IChatClient, AITool, AIFunctionFactory, ChatMessage).
  Triggers on: "AIAgent", "AgentSession", "ChatClientAgent", "ChatClientAgentOptions", "ChatClientAgentResult",
  "RunStreamingAsync", "RunAsync", "AgentResponse", "AgentResponseUpdate", "AgentRunOptions",
  "ChatMessage", "ChatRole", "Microsoft Agent Framework", "IChatClient", "AIFunctionFactory",
  "AITool", "thread pattern", "per-user session", "per-message session",
  "Microsoft.Extensions.AI", "Microsoft.Agents.AI", "AsAIAgent", "AsAIFunction",
  "AIAgentBuilder", "agent middleware", "session serialization", "ChatHistoryProvider",
  "AgentSessionStateBag", "CreateSessionAsync".
  Do NOT use for: FabrCore-specific agent lifecycle (OnInitialize, OnMessage) — use fabrcore-agent.
  Do NOT use for: plugin or tool development — use fabrcore-plugins-tools.
allowed-tools: "Bash(dotnet:*) Bash(mkdir:*) Bash(ls:*) Bash(pwsh:*) Bash(powershell:*) Bash(git:*) Bash(dir:*)"
---

# Microsoft Agent Framework in FabrCore

FabrCore wraps the Microsoft Agent Framework (`Microsoft.Agents.AI`) and `Microsoft.Extensions.AI` to provide LLM-powered agents. Understanding these underlying types helps you customize agent behavior beyond the defaults.

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.Agents.AI.Abstractions` | Core abstractions: `AIAgent`, `AgentSession`, `AgentResponse`, `AgentResponseUpdate` |
| `Microsoft.Agents.AI` | Concrete implementations: `ChatClientAgent`, `AIAgentBuilder`, extensions |
| `Microsoft.Extensions.AI` | AI abstractions: `IChatClient`, `AITool`, `AIFunctionFactory`, `ChatMessage` |
| `Microsoft.Extensions.AI.Abstractions` | Core AI interfaces |

FabrCore.Sdk includes these as transitive dependencies — you don't need to add them separately.

## Required Using Directives

```csharp
using Microsoft.Agents.AI;     // AIAgent, AgentSession, ChatClientAgent, AgentResponse
using Microsoft.Extensions.AI;  // ChatMessage, ChatRole, IChatClient, AITool, AIFunction
using FabrCore.Sdk;            // FabrCoreAgentProxy, CreateChatClientAgent
```

## Core Types

### AIAgent

Abstract base class for all agents. `ChatClientAgent` is the concrete implementation used by FabrCore.

**Namespace:** `Microsoft.Agents.AI`

```csharp
public abstract partial class AIAgent
{
    public string Id { get; }
    public virtual string? Name { get; }
    public virtual string? Description { get; }

    // Current run context (static, set during RunAsync/RunStreamingAsync)
    public static AgentRunContext? CurrentRunContext { get; protected set; }

    // Session management
    public ValueTask<AgentSession> CreateSessionAsync(CancellationToken ct = default);
    public ValueTask<JsonElement> SerializeSessionAsync(AgentSession session, ...);
    public ValueTask<AgentSession> DeserializeSessionAsync(JsonElement serializedState, ...);

    // Non-streaming execution
    public Task<AgentResponse> RunAsync(
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default);
    public Task<AgentResponse> RunAsync(
        string message,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default);
    public Task<AgentResponse> RunAsync(
        ChatMessage message,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default);
    public Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default);

    // Streaming execution
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default);
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        string message,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default);
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        ChatMessage message,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default);
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default);

    // Service resolution
    public virtual object? GetService(Type serviceType, object? serviceKey = null);
    public TService? GetService<TService>(object? serviceKey = null);
}
```

### AgentSession

Conversation state container used across agent runs.

**Namespace:** `Microsoft.Agents.AI`

```csharp
public abstract class AgentSession
{
    public AgentSessionStateBag StateBag { get; protected set; } = new();

    public virtual object? GetService(Type serviceType, object? serviceKey = null);
    public TService? GetService<TService>(object? serviceKey = null);
}
```

### AgentSessionStateBag

Arbitrary key-value state container within a session:

```csharp
public class AgentSessionStateBag
{
    public int Count { get; }

    public T? GetValue<T>(string key, ...) where T : class;
    public bool TryGetValue<T>(string key, out T? value, ...) where T : class;
    public void SetValue<T>(string key, T? value, ...) where T : class;
    public bool TryRemoveValue(string key);

    public JsonElement Serialize();
    public static AgentSessionStateBag Deserialize(JsonElement json);
}
```

### ChatClientAgent

Sealed concrete implementation wrapping an `IChatClient`:

**Namespace:** `Microsoft.Agents.AI`

```csharp
public sealed partial class ChatClientAgent : AIAgent
{
    // Constructor with individual parameters
    public ChatClientAgent(
        IChatClient chatClient,
        string? instructions = null,
        string? name = null,
        string? description = null,
        IList<AITool>? tools = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null);

    // Constructor with options object
    public ChatClientAgent(
        IChatClient chatClient,
        ChatClientAgentOptions? options,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null);

    public IChatClient ChatClient { get; }
    public ChatHistoryProvider? ChatHistoryProvider { get; }
    public IReadOnlyList<AIContextProvider>? AIContextProviders { get; }
    public string? Instructions { get; }
}
```

FabrCore's `CreateChatClientAgent()` creates this internally. You typically don't construct it directly.

### ChatClientAgentOptions

Configuration options for `ChatClientAgent`:

```csharp
public class ChatClientAgentOptions
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public ChatOptions? ChatOptions { get; set; }           // Instructions, tools, response format
    public ChatHistoryProvider? ChatHistoryProvider { get; set; }
    public IEnumerable<AIContextProvider>? AIContextProviders { get; set; }
    public bool UseProvidedChatClientAsIs { get; set; }     // Skip FunctionInvokingChatClient wrapper

    public ChatClientAgentOptions Clone();
}
```

### AgentResponse

Complete response from a non-streaming agent run:

```csharp
public class AgentResponse
{
    public IList<ChatMessage> Messages { get; set; }
    public string Text { get; }                             // Concatenated text from all messages
    public string? AgentId { get; set; }
    public string? ResponseId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public ChatFinishReason? FinishReason { get; set; }
    public UsageDetails? Usage { get; set; }                // Token counts
    public object? RawRepresentation { get; set; }

    public override string ToString() => Text;

    // Constructors
    public AgentResponse();
    public AgentResponse(ChatMessage message);
    public AgentResponse(ChatResponse response);
    public AgentResponse(IList<ChatMessage>? messages);
}
```

### AgentResponseUpdate

Streaming response chunk:

```csharp
public class AgentResponseUpdate
{
    public string Text { get; }                             // Text content of this chunk
    public ChatRole? Role { get; set; }
    public IList<AIContent> Contents { get; set; }
    public string? AuthorName { get; set; }
    public string? AgentId { get; set; }
    public string? ResponseId { get; set; }
    public string? MessageId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public ChatFinishReason? FinishReason { get; set; }

    public override string ToString() => Text;

    // Constructors
    public AgentResponseUpdate();
    public AgentResponseUpdate(ChatRole? role, string? content);
    public AgentResponseUpdate(ChatResponseUpdate chatResponseUpdate);
}
```

### AgentRunOptions

Options passed to individual `RunAsync`/`RunStreamingAsync` calls:

```csharp
public class AgentRunOptions
{
    public ChatResponseFormat? ResponseFormat { get; set; }
    public bool? AllowBackgroundResponses { get; set; }
}
```

For `ChatClientAgent`, use `ChatClientAgentRunOptions` for per-run overrides:

```csharp
public class ChatClientAgentRunOptions : AgentRunOptions
{
    public ChatOptions? ChatOptions { get; set; }
    public Func<IChatClient, IChatClient>? ChatClientFactory { get; set; }
}
```

## How FabrCore Wraps the Agent Framework

`FabrCoreAgentProxy.CreateChatClientAgent()` bridges FabrCore and the Microsoft Agent Framework:

1. Creates an `IChatClient` for the specified model via `FabrCoreChatClientService`
2. Wraps it in a `ChatClientAgent` with system prompt and tools
3. Creates an `AgentSession` backed by `FabrCoreChatHistoryProvider` for auto-persisted chat history
4. Returns `ChatClientAgentResult(Agent, Session, ChatHistoryProvider)`

```csharp
// Inside your agent's OnInitialize:
var result = await CreateChatClientAgent(
    chatClientConfigName: "default",     // model from fabrcore.json
    threadId: config.Handle ?? "default", // history persistence key
    tools: tools);

_agent = result.Agent;     // AIAgent (actually ChatClientAgent)
_session = result.Session; // AgentSession with auto-persisted history
```

## Running Agents

### Non-Streaming (RunAsync)

Waits for the complete response. Returns `AgentResponse` with all messages:

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var response = message.Response();
    var chatMessage = new ChatMessage(ChatRole.User, message.Message);

    var result = await _agent!.RunAsync(chatMessage, _session!);

    // AgentResponse.Text concatenates all assistant message text
    response.Message = result.Text;

    // Or access individual messages
    // response.Message = result.Messages.Last().Text;

    return response;
}
```

### Streaming (RunStreamingAsync)

Receives response chunks as they arrive:

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var response = message.Response();
    var chatMessage = new ChatMessage(ChatRole.User, message.Message);

    await foreach (var update in _agent!.RunStreamingAsync(chatMessage, _session!))
    {
        response.Message += update.Text;
    }

    return response;
}
```

### String Shortcut

You can pass a string directly instead of creating a `ChatMessage`:

```csharp
var result = await _agent!.RunAsync(message.Message ?? "", _session!);
```

### Converting Between Response Types

```csharp
// AgentResponse → ChatResponse
ChatResponse chatResponse = result.AsChatResponse();

// AgentResponseUpdate → ChatResponseUpdate
ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

// Collect streaming updates into a complete response
AgentResponse complete = await _agent!.RunStreamingAsync(msg, _session!)
    .ToAgentResponseAsync();
```

## Session Management

### Creating Sessions

```csharp
// Create a new session
AgentSession session = await agent.CreateSessionAsync();

// Multi-turn conversation with the same session
var first = await agent.RunAsync("My name is Alice.", session);
var second = await agent.RunAsync("What is my name?", session);
// second.Text contains "Alice" because the session retains history
```

### Session State (StateBag)

Store arbitrary data within a session:

```csharp
// Set custom state
session.StateBag.SetValue("userPreferences", new UserPrefs { Theme = "dark" });

// Read custom state
var prefs = session.StateBag.GetValue<UserPrefs>("userPreferences");

// Check and remove
if (session.StateBag.TryGetValue<string>("tempKey", out var value))
    session.StateBag.TryRemoveValue("tempKey");
```

### Session Serialization

Persist and restore sessions across restarts:

```csharp
// Serialize to JSON
var serialized = await agent.SerializeSessionAsync(session);

// Later: restore the session
AgentSession resumed = await agent.DeserializeSessionAsync(serialized);
var result = await agent.RunAsync("Continue where we left off.", resumed);
```

## Thread Management Patterns

### Single Session (Default)

One conversation per agent instance. Simplest and most common pattern.

```csharp
private AIAgent? _agent;
private AgentSession? _session;

public override async Task OnInitialize()
{
    var tools = await ResolveConfiguredToolsAsync();
    var result = await CreateChatClientAgent(
        config.Models ?? "default",
        threadId: config.Handle ?? fabrcoreAgentHost.GetHandle(),
        tools: tools);
    _agent = result.Agent;
    _session = result.Session;
}

public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var response = message.Response();
    await foreach (var update in _agent!.RunStreamingAsync(
        new ChatMessage(ChatRole.User, message.Message), _session!))
    {
        response.Message += update.Text;
    }
    return response;
}
```

### Per-User Session

Separate conversation history per user. Use different `threadId` values:

```csharp
private readonly Dictionary<string, (AIAgent Agent, AgentSession Session)> _userSessions = new();

public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var userId = message.FromHandle ?? "anonymous";
    if (!_userSessions.TryGetValue(userId, out var session))
    {
        var result = await CreateChatClientAgent(
            config.Models ?? "default",
            threadId: $"{config.Handle}-{userId}",
            tools: await ResolveConfiguredToolsAsync());
        session = (result.Agent, result.Session);
        _userSessions[userId] = session;
    }

    var response = message.Response();
    await foreach (var update in session.Agent.RunStreamingAsync(
        new ChatMessage(ChatRole.User, message.Message), session.Session))
    {
        response.Message += update.Text;
    }
    return response;
}
```

### Per-Message Session (Stateless)

No conversation history — each message is independent:

```csharp
public override async Task<AgentMessage> OnMessage(AgentMessage message)
{
    var tools = await ResolveConfiguredToolsAsync();
    var result = await CreateChatClientAgent(
        config.Models ?? "default",
        threadId: Guid.NewGuid().ToString(),
        tools: tools);

    var response = message.Response();
    var aiResult = await result.Agent.RunAsync(
        new ChatMessage(ChatRole.User, message.Message), result.Session);
    response.Message = aiResult.Text;
    return response;
}
```

## Agent Composition (Agent-as-Tool)

Convert an agent into a function tool that another agent can call:

```csharp
// Create an inner agent
var weatherAgent = new ChatClientAgent(chatClient,
    instructions: "You answer weather questions.",
    name: "WeatherAgent",
    description: "An agent that answers weather questions.",
    tools: [AIFunctionFactory.Create(GetWeather)]);

// Convert to a function tool for a parent agent
AIFunction weatherTool = weatherAgent.AsAIFunction();

// Parent agent can now call the inner agent as a tool
var mainAgent = new ChatClientAgent(chatClient,
    instructions: "You are a helpful assistant.",
    tools: [weatherTool]);

var result = await mainAgent.RunAsync("What's the weather in Seattle?");
```

## Agent Middleware (AIAgentBuilder)

Middleware intercepts agent runs to add logging, security, modification, etc:

```csharp
// Create a middleware-enabled agent from an existing agent
var middlewareAgent = originalAgent
    .AsBuilder()
    .Use(
        runFunc: async (messages, session, options, innerAgent, ct) =>
        {
            // Pre-processing
            Console.WriteLine($"Input: {messages.Count()} messages");

            // Call the inner agent
            var response = await innerAgent.RunAsync(messages, session, options, ct);

            // Post-processing
            Console.WriteLine($"Output: {response.Messages.Count} messages");
            return response;
        },
        runStreamingFunc: null) // Omitting streaming middleware uses runFunc for both
    .Build();
```

### Function Calling Middleware

Intercept tool/function invocations:

```csharp
var agent = originalAgent
    .AsBuilder()
    .Use(async (agent, context, next, ct) =>
    {
        Console.WriteLine($"Calling: {context.Function.Name}");
        var result = await next(context, ct);
        Console.WriteLine($"Result: {result}");
        return result;
    })
    .Build();
```

## Microsoft.Extensions.AI Abstractions

### IChatClient

The core abstraction for LLM communication. FabrCore creates these via `FabrCoreChatClientService`:

```csharp
// Get a chat client directly (for custom usage outside of CreateChatClientAgent)
var chatClient = await chatClientService.GetChatClient("default");
```

### AITool / AIFunction

Tools that the LLM can call:

```csharp
// Create from a method reference
var tool = AIFunctionFactory.Create(MyMethod);

// Create from a delegate
var tool = AIFunctionFactory.Create(
    (string query) => $"Result for {query}",
    "search",
    "Searches for items");

// Create from an agent (agent composition)
var tool = innerAgent.AsAIFunction();
```

### ChatMessage / ChatRole

Messages in the conversation:

```csharp
var userMessage = new ChatMessage(ChatRole.User, "Hello!");
var systemMessage = new ChatMessage(ChatRole.System, "You are helpful.");
var assistantMessage = new ChatMessage(ChatRole.Assistant, "Hi there!");
```

### ChatOptions

Configure LLM behavior per-request:

```csharp
var chatOptions = new ChatOptions
{
    Temperature = 0.7f,
    MaxOutputTokens = 4096,
    ResponseFormat = ChatResponseFormat.Text,
    Instructions = "You are a helpful assistant.",  // System prompt
    Tools = [AIFunctionFactory.Create(MyTool)]
};
```

## LLM Usage Tracking

FabrCore automatically tracks LLM metrics across all calls within a single `OnMessage` invocation:

| Args Key | Description |
|----------|-------------|
| `_tokens_input` | Total input tokens |
| `_tokens_output` | Total output tokens |
| `_tokens_reasoning` | Thinking/reasoning tokens |
| `_tokens_cached_input` | Cached input tokens |
| `_llm_calls` | Number of LLM calls (includes tool loops) |
| `_llm_duration_ms` | Total LLM response time |
| `_model` | Model ID from last call |
| `_finish_reason` | Finish reason (`stop`, `length`, `tool_calls`, `content_filter`) |

These are attached to the response `AgentMessage.Args` automatically. Only non-zero/non-null values are set.

## References

- [Microsoft Agent Framework Overview](https://learn.microsoft.com/en-us/agent-framework/overview/?pivots=programming-language-csharp)
- [Agent Sessions](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/session?pivots=programming-language-csharp)
- [Agent Tools](https://learn.microsoft.com/en-us/agent-framework/agents/tools/?pivots=programming-language-csharp)
- [Agent Middleware](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/?pivots=programming-language-csharp)
- [GitHub: microsoft/agent-framework](https://github.com/microsoft/agent-framework)
- NuGet: [Microsoft.Agents.AI](https://www.nuget.org/packages/Microsoft.Agents.AI)
- NuGet: [Microsoft.Agents.AI.Abstractions](https://www.nuget.org/packages/Microsoft.Agents.AI.Abstractions)
