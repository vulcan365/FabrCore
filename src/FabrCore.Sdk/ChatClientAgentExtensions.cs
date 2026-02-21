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
    /// Forks a FabrCoreChatHistoryProvider, creating a new in-memory provider that references
    /// the original messages (read-only) and stores only new messages in memory.
    ///
    /// - Memory efficient: original messages are not copied
    /// - Original session is completely unaffected
    /// - New messages are in-memory until PersistAsync() is called on the provider
    /// </summary>
    /// <param name="originalProvider">The history provider to fork from.</param>
    /// <param name="chatClient">The chat client to use for the forked agent.</param>
    /// <param name="options">Optional ChatOptions (instructions, tools, etc.).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A ForkedSessionResult containing the new agent and session.</returns>
    public static async Task<ForkedSessionResult> ForkAsync(
        this FabrCoreChatHistoryProvider originalProvider,
        IChatClient chatClient,
        ChatOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalProvider);
        ArgumentNullException.ThrowIfNull(chatClient);

        // Fork the provider (loads messages from Orleans if needed)
        var forkedProvider = await originalProvider.ForkAsync(logger, cancellationToken);

        // Create a new agent with the forked provider
        var agentOptions = new ChatClientAgentOptions
        {
            ChatOptions = options,
            ChatHistoryProvider = forkedProvider
        };

        var forkedAgent = new ChatClientAgent(chatClient, agentOptions);
        var forkedSession = await forkedAgent.CreateSessionAsync(cancellationToken);

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

        // Get current messages from original provider
        var originalMessages = await originalProvider.GetMessagesAsync(cancellationToken);

        // Create forked provider with read-only reference to original messages
        return new ForkedChatHistoryProvider(
            originalMessages.ToList(),
            originalProvider.ThreadId,
            logger);
    }
}
