using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Fabr.Core;

namespace Fabr.Sdk;

/// <summary>
/// A memory-efficient chat history provider for forked conversations.
///
/// - Original messages are referenced (read-only, not copied)
/// - New messages are stored in memory
/// - Persistence is on-demand via PersistAsync()
/// - Original session is completely unaffected
/// </summary>
public class ForkedChatHistoryProvider : ChatHistoryProvider
{
    private readonly IReadOnlyList<ChatMessage> _originalMessages;
    private readonly List<ChatMessage> _newMessages = new();
    private readonly string? _originalThreadId;
    private readonly object _syncLock = new();
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a forked chat history provider from existing messages.
    /// The original messages are referenced (not copied) and treated as read-only.
    /// New messages are stored in memory until PersistAsync() is called.
    /// </summary>
    /// <param name="originalMessages">The messages to reference (snapshot at fork time).</param>
    /// <param name="originalThreadId">Optional thread ID of the original conversation (for tracking).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public ForkedChatHistoryProvider(
        IReadOnlyList<ChatMessage> originalMessages,
        string? originalThreadId = null,
        ILogger? logger = null)
    {
        _originalMessages = originalMessages ?? throw new ArgumentNullException(nameof(originalMessages));
        _originalThreadId = originalThreadId;
        _logger = logger;

        _logger?.LogDebug(
            "Created ForkedChatHistoryProvider: OriginalThreadId={OriginalThreadId}, OriginalMessageCount={Count}",
            _originalThreadId ?? "(none)", _originalMessages.Count);
    }

    /// <summary>
    /// Gets the original thread ID (for reference/debugging). May be null.
    /// </summary>
    public string? OriginalThreadId => _originalThreadId;

    /// <summary>
    /// Gets the count of original messages (from fork point, read-only).
    /// </summary>
    public int OriginalMessageCount => _originalMessages.Count;

    /// <summary>
    /// Gets the count of new messages added after the fork.
    /// </summary>
    public int NewMessageCount
    {
        get { lock (_syncLock) { return _newMessages.Count; } }
    }

    /// <summary>
    /// Gets the total message count (original + new).
    /// </summary>
    public int TotalMessageCount => OriginalMessageCount + NewMessageCount;

    /// <summary>
    /// Gets the new messages added after the fork (for inspection/persistence).
    /// </summary>
    public IReadOnlyList<ChatMessage> NewMessages
    {
        get { lock (_syncLock) { return _newMessages.ToList(); } }
    }

    /// <inheritdoc/>
    public override ValueTask InvokedAsync(ChatHistoryProvider.InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null)
            return ValueTask.CompletedTask;

        // Only store NEW messages in memory - original messages are read-only
        var allNewMessages = context.RequestMessages
            .Concat(context.AIContextProviderMessages ?? [])
            .Concat(context.ResponseMessages ?? []);

        lock (_syncLock)
        {
            _newMessages.AddRange(allNewMessages);
        }

        _logger?.LogDebug("Added {Count} new messages to forked provider (total new: {Total})",
            allNewMessages.Count(), _newMessages.Count);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public override ValueTask<IEnumerable<ChatMessage>> InvokingAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Return combined view: original messages (read-only) + new messages
        IEnumerable<ChatMessage> allMessages;

        lock (_syncLock)
        {
            allMessages = _originalMessages.Concat(_newMessages).ToList();
        }

        _logger?.LogDebug("InvokingAsync: Returning {OriginalCount} original + {NewCount} new = {Total} total messages",
            _originalMessages.Count, _newMessages.Count, allMessages.Count());

        return ValueTask.FromResult(allMessages);
    }

    /// <summary>
    /// Persists the forked conversation to Orleans storage.
    /// This saves BOTH original messages and new messages to a new thread.
    /// </summary>
    /// <param name="agentHost">The agent host for persistence.</param>
    /// <param name="threadId">The thread ID to persist to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PersistAsync(
        IFabrAgentHost agentHost,
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentHost);
        ArgumentNullException.ThrowIfNull(threadId);

        List<ChatMessage> allMessages;
        lock (_syncLock)
        {
            allMessages = _originalMessages.Concat(_newMessages).ToList();
        }

        // Convert to StoredChatMessage format
        var storedMessages = allMessages.Select(m => new StoredChatMessage
        {
            Role = m.Role.Value,
            AuthorName = m.AuthorName,
            Timestamp = DateTime.UtcNow,
            ContentsJson = JsonSerializer.Serialize(m.Contents, ChatMessageSerializerOptions.Instance)
        }).ToList();

        await agentHost.AddThreadMessagesAsync(threadId, storedMessages);

        _logger?.LogDebug("Persisted {Count} messages to thread {ThreadId}", storedMessages.Count, threadId);
    }

    /// <summary>
    /// Persists only the NEW messages (after fork point) to Orleans storage.
    /// Use this when the original messages are already persisted elsewhere.
    /// </summary>
    /// <param name="agentHost">The agent host for persistence.</param>
    /// <param name="threadId">The thread ID to persist to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PersistNewMessagesOnlyAsync(
        IFabrAgentHost agentHost,
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentHost);
        ArgumentNullException.ThrowIfNull(threadId);

        List<ChatMessage> newMessages;
        lock (_syncLock)
        {
            newMessages = _newMessages.ToList();
        }

        if (newMessages.Count == 0)
        {
            _logger?.LogDebug("No new messages to persist for thread {ThreadId}", threadId);
            return;
        }

        var storedMessages = newMessages.Select(m => new StoredChatMessage
        {
            Role = m.Role.Value,
            AuthorName = m.AuthorName,
            Timestamp = DateTime.UtcNow,
            ContentsJson = JsonSerializer.Serialize(m.Contents, ChatMessageSerializerOptions.Instance)
        }).ToList();

        await agentHost.AddThreadMessagesAsync(threadId, storedMessages);

        _logger?.LogDebug("Persisted {Count} new messages to thread {ThreadId}", storedMessages.Count, threadId);
    }

    /// <summary>
    /// Creates a ChatHistoryProviderFactory that returns this ForkedChatHistoryProvider.
    /// Use this factory when creating a new ChatClientAgent for forked conversations.
    /// </summary>
    /// <returns>A factory function compatible with ChatClientAgentOptions.ChatHistoryProviderFactory.</returns>
    public Func<ChatClientAgentOptions.ChatHistoryProviderFactoryContext, CancellationToken, ValueTask<ChatHistoryProvider>> CreateFactory()
    {
        return (_, _) => ValueTask.FromResult<ChatHistoryProvider>(this);
    }

    /// <inheritdoc/>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var jso = jsonSerializerOptions ?? ChatMessageSerializerOptions.Instance;

        List<ChatMessage> newMessages;
        lock (_syncLock)
        {
            newMessages = _newMessages.ToList();
        }

        var state = new
        {
            OriginalThreadId = _originalThreadId,
            OriginalMessages = _originalMessages,
            NewMessages = newMessages
        };

        return JsonSerializer.SerializeToElement(state, jso);
    }
}

// Keep old name as alias for backward compatibility during migration
[Obsolete("Use ForkedChatHistoryProvider instead")]
public class ForkedChatMessageStore : ForkedChatHistoryProvider
{
    public ForkedChatMessageStore(IReadOnlyList<ChatMessage> originalMessages, string? originalThreadId = null, ILogger? logger = null)
        : base(originalMessages, originalThreadId, logger) { }
}
