using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

/// <summary>
/// Result from forking a session, containing the new agent, session, and history provider.
/// </summary>
/// <param name="Agent">The new ChatClientAgent configured with the forked history provider.</param>
/// <param name="Session">The new AgentSession for the forked conversation.</param>
/// <param name="HistoryProvider">The ForkedChatHistoryProvider for on-demand persistence.</param>
public record ForkedSessionResult(
    ChatClientAgent Agent,
    AgentSession Session,
    ForkedChatHistoryProvider HistoryProvider);

/// <summary>
/// Extension methods for session forking and management.
/// </summary>
public static class ChatClientAgentExtensions
{
    /// <summary>
    /// Forks an AgentSession, creating a new in-memory session that references
    /// the original messages (read-only) and stores only new messages in memory.
    ///
    /// - Memory efficient: original messages are not copied
    /// - Original session is completely unaffected
    /// - New messages are in-memory until PersistAsync() is called on the provider
    /// </summary>
    /// <param name="session">The session to fork.</param>
    /// <param name="chatClient">The chat client to use for the forked agent.</param>
    /// <param name="options">Optional ChatOptions (instructions, tools, etc.).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A ForkedSessionResult containing the new agent and session.</returns>
    public static async Task<ForkedSessionResult> ForkAsync(
        this AgentSession session,
        IChatClient chatClient,
        ChatOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(chatClient);

        // Get the original history provider
        var originalProvider = session.GetService<FabrCoreChatHistoryProvider>()
            ?? throw new InvalidOperationException(
                "ForkAsync requires a session with FabrCoreChatHistoryProvider.");

        // Fork the provider (loads messages from Orleans if needed)
        var forkedProvider = await originalProvider.ForkAsync(logger, cancellationToken);

        // Create a new agent with the forked provider
        var agentOptions = new ChatClientAgentOptions
        {
            ChatOptions = options,
            ChatHistoryProviderFactory = forkedProvider.CreateFactory()
        };

        var forkedAgent = new ChatClientAgent(chatClient, agentOptions);
        var forkedSession = await forkedAgent.GetNewSessionAsync(cancellationToken);

        return new ForkedSessionResult(forkedAgent, forkedSession, forkedProvider);
    }

    /// <summary>
    /// Creates a ForkedChatHistoryProvider from an existing FabrCoreChatHistoryProvider.
    /// The forked provider references original messages (read-only) and stores new messages in memory.
    ///
    /// - Memory efficient: original messages are not copied
    /// - Original session is completely unaffected
    /// - New messages are in-memory until PersistAsync() is called on the provider
    /// </summary>
    /// <param name="originalProvider">The original history provider to fork from.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new ForkedChatHistoryProvider that branches from the original conversation.</returns>
    public static async Task<ForkedChatHistoryProvider> ForkAsync(
        this FabrCoreChatHistoryProvider originalProvider,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalProvider);

        // Get current messages from original provider (this loads from Orleans if needed)
        var dummyContext = new ChatHistoryProvider.InvokingContext(Array.Empty<ChatMessage>());
        var originalMessages = (await originalProvider.InvokingAsync(dummyContext, cancellationToken)).ToList();

        // Create forked provider with read-only reference to original messages
        return new ForkedChatHistoryProvider(
            originalMessages,
            originalProvider.ThreadId,
            logger);
    }
}

// Keep old names as aliases for backward compatibility during migration
[Obsolete("Use ForkedSessionResult instead")]
public record ForkedThreadResult(
    ChatClientAgent Agent,
    AgentSession Session,
    ForkedChatHistoryProvider HistoryProvider) : ForkedSessionResult(Agent, Session, HistoryProvider);
