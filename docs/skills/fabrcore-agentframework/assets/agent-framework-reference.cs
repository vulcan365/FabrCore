// Microsoft Agent Framework — .NET API Quick Reference
// Namespace: Microsoft.Agents.AI (from Microsoft.Agents.AI and Microsoft.Agents.AI.Abstractions)
//
// This file is a reference guide, not executable code.
// Use alongside the fabrcore-agentframework skill for exact signatures.

// ============================================================================
// AIAgent (abstract base class)
// ============================================================================

public abstract partial class AIAgent
{
    // Properties
    public string Id { get; }
    public virtual string? Name { get; }
    public virtual string? Description { get; }
    public static AgentRunContext? CurrentRunContext { get; protected set; }

    // Session management
    public ValueTask<AgentSession> CreateSessionAsync(CancellationToken ct = default);
    public ValueTask<JsonElement> SerializeSessionAsync(AgentSession session, JsonSerializerOptions? opts = null, CancellationToken ct = default);
    public ValueTask<AgentSession> DeserializeSessionAsync(JsonElement state, JsonSerializerOptions? opts = null, CancellationToken ct = default);

    // Non-streaming (4 overloads)
    public Task<AgentResponse> RunAsync(AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default);
    public Task<AgentResponse> RunAsync(string message, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default);
    public Task<AgentResponse> RunAsync(ChatMessage message, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default);
    public Task<AgentResponse> RunAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default);

    // Streaming (4 overloads)
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default);
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(string message, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default);
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(ChatMessage message, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default);
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken ct = default);

    // Service resolution
    public virtual object? GetService(Type serviceType, object? serviceKey = null);
    public TService? GetService<TService>(object? serviceKey = null);
}

// ============================================================================
// ChatClientAgent (concrete implementation)
// ============================================================================

public sealed partial class ChatClientAgent : AIAgent
{
    // Constructor: individual parameters
    public ChatClientAgent(
        IChatClient chatClient,
        string? instructions = null,
        string? name = null,
        string? description = null,
        IList<AITool>? tools = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null);

    // Constructor: options object
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

// ============================================================================
// AgentSession (abstract base)
// ============================================================================

public abstract class AgentSession
{
    public AgentSessionStateBag StateBag { get; protected set; } = new();
    public virtual object? GetService(Type serviceType, object? serviceKey = null);
    public TService? GetService<TService>(object? serviceKey = null);
}

// ============================================================================
// AgentResponse
// ============================================================================

public class AgentResponse
{
    public IList<ChatMessage> Messages { get; set; }
    public string Text { get; }                             // Concatenated text
    public string? AgentId { get; set; }
    public string? ResponseId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public ChatFinishReason? FinishReason { get; set; }
    public UsageDetails? Usage { get; set; }
    public object? RawRepresentation { get; set; }

    // Constructors
    public AgentResponse();
    public AgentResponse(ChatMessage message);
    public AgentResponse(ChatResponse response);
    public AgentResponse(IList<ChatMessage>? messages);

    public override string ToString() => Text;
    public AgentResponseUpdate[] ToAgentResponseUpdates();
}

// ============================================================================
// AgentResponseUpdate
// ============================================================================

public class AgentResponseUpdate
{
    public string Text { get; }                             // Text of this chunk
    public ChatRole? Role { get; set; }
    public IList<AIContent> Contents { get; set; }
    public string? AuthorName { get; set; }
    public string? AgentId { get; set; }
    public string? ResponseId { get; set; }
    public string? MessageId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public ChatFinishReason? FinishReason { get; set; }

    // Constructors
    public AgentResponseUpdate();
    public AgentResponseUpdate(ChatRole? role, string? content);
    public AgentResponseUpdate(ChatResponseUpdate chatResponseUpdate);

    public override string ToString() => Text;
}

// ============================================================================
// ChatClientAgentOptions
// ============================================================================

public class ChatClientAgentOptions
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public ChatOptions? ChatOptions { get; set; }
    public ChatHistoryProvider? ChatHistoryProvider { get; set; }
    public IEnumerable<AIContextProvider>? AIContextProviders { get; set; }
    public bool UseProvidedChatClientAsIs { get; set; }

    public ChatClientAgentOptions Clone();
}

// ============================================================================
// AgentRunOptions / ChatClientAgentRunOptions
// ============================================================================

public class AgentRunOptions
{
    public ChatResponseFormat? ResponseFormat { get; set; }
    public bool? AllowBackgroundResponses { get; set; }
    public virtual AgentRunOptions Clone();
}

public class ChatClientAgentRunOptions : AgentRunOptions
{
    public ChatOptions? ChatOptions { get; set; }
    public Func<IChatClient, IChatClient>? ChatClientFactory { get; set; }
}

// ============================================================================
// AgentSessionStateBag
// ============================================================================

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

// ============================================================================
// Extension Methods
// ============================================================================

public static class AgentExtensions
{
    // Convert agent to builder for adding middleware
    public static AIAgentBuilder AsBuilder(this AIAgent innerAgent);

    // Convert agent to a function tool for another agent
    public static AIFunction AsAIFunction(this AIAgent agent, AIFunctionFactoryOptions? options = null, AgentSession? session = null);
}

public static class AgentResponseExtensions
{
    public static ChatResponse AsChatResponse(this AgentResponse response);
    public static ChatResponseUpdate AsChatResponseUpdate(this AgentResponseUpdate update);
    public static async IAsyncEnumerable<ChatResponseUpdate> AsChatResponseUpdatesAsync(this IAsyncEnumerable<AgentResponseUpdate> updates);
    public static AgentResponse ToAgentResponse(this IEnumerable<AgentResponseUpdate> updates);
    public static Task<AgentResponse> ToAgentResponseAsync(this IAsyncEnumerable<AgentResponseUpdate> updates, CancellationToken ct = default);
}

// ============================================================================
// AIAgentBuilder (middleware pipeline)
// ============================================================================

public class AIAgentBuilder
{
    public AIAgentBuilder(AIAgent innerAgent);
    public AIAgent Build(IServiceProvider? services = null);

    // Add agent run middleware
    public AIAgentBuilder Use(
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, Task<AgentResponse>>? runFunc,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken, IAsyncEnumerable<AgentResponseUpdate>>? runStreamingFunc);

    // Add shared middleware (inspect input only, does not block streaming)
    public AIAgentBuilder Use(
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?,
             Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, CancellationToken, Task>,
             CancellationToken, Task> sharedFunc);
}
