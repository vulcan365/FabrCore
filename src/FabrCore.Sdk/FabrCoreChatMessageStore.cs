using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Fabr.Core;

namespace Fabr.Sdk
{
    /// <summary>
    /// JSON serialization options for AIContent serialization.
    /// Uses AgentAbstractionsJsonUtilities.DefaultOptions which properly chains:
    /// 1. AIJsonUtilities.DefaultOptions.TypeInfoResolver (for M.E.AI types like AIContent)
    /// 2. Microsoft.Agents.AI source-generated context
    /// This ensures correct polymorphic type handling for all AIContent subtypes
    /// (TextContent, FunctionCallContent, FunctionResultContent, etc.)
    /// </summary>
    internal static class ChatMessageSerializerOptions
    {
        // Use AgentAbstractionsJsonUtilities.DefaultOptions - it properly chains
        // AIJsonUtilities resolver with the agent framework's source-generated context
        public static JsonSerializerOptions Instance => AgentAbstractionsJsonUtilities.DefaultOptions;
    }

    /// <summary>
    /// A ChatHistoryProvider implementation that persists messages through Orleans grain state.
    /// Messages are buffered in memory until FlushAsync() is called.
    /// </summary>
    public class FabrChatHistoryProvider : ChatHistoryProvider
    {
        private readonly IFabrAgentHost _agentHost;
        private readonly string _threadId;
        private readonly List<StoredChatMessage> _pendingMessages = new();
        private List<StoredChatMessage>? _cachedMessages;
        private readonly object _syncLock = new();
        private readonly ILogger? _logger;

        public FabrChatHistoryProvider(IFabrAgentHost agentHost, string threadId, ILogger? logger = null)
        {
            _agentHost = agentHost;
            _threadId = threadId;
            _logger = logger;
        }

        /// <summary>
        /// Creates a FabrChatHistoryProvider from serialized state.
        /// Used for session resumption via ChatHistoryProviderFactory.
        /// </summary>
        /// <param name="agentHost">The agent host for persistence operations.</param>
        /// <param name="serializedState">Previously serialized state containing ThreadId.</param>
        /// <param name="jsonSerializerOptions">JSON serialization options.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public FabrChatHistoryProvider(
            IFabrAgentHost agentHost,
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            ILogger? logger = null)
        {
            _agentHost = agentHost;
            _logger = logger;

            // Extract ThreadId from serialized state
            if (serializedState.TryGetProperty("ThreadId", out var threadIdElement))
            {
                _threadId = threadIdElement.GetString()
                    ?? throw new ArgumentException("ThreadId in serialized state is null", nameof(serializedState));
            }
            else
            {
                throw new ArgumentException("Serialized state does not contain ThreadId", nameof(serializedState));
            }
        }

        /// <summary>
        /// Factory method for use with ChatClientAgentOptions.ChatHistoryProviderFactory.
        /// Creates a new provider with the specified threadId, or restores from serialized state if available.
        /// The created provider is automatically registered with the agent host for tracking and auto-flush on deactivation.
        /// </summary>
        /// <param name="agentHost">The agent host for persistence operations.</param>
        /// <param name="threadId">The thread ID to use for new sessions.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        /// <returns>A factory function compatible with ChatHistoryProviderFactory.</returns>
        public static Func<ChatClientAgentOptions.ChatHistoryProviderFactoryContext, CancellationToken, ValueTask<ChatHistoryProvider>> CreateFactory(
            IFabrAgentHost agentHost,
            string threadId,
            ILogger? logger = null)
        {
            return (ctx, ct) =>
            {
                FabrChatHistoryProvider provider;

                // If we have serialized state, restore from it (session resumption)
                if (ctx.SerializedState.ValueKind != JsonValueKind.Undefined &&
                    ctx.SerializedState.ValueKind != JsonValueKind.Null)
                {
                    provider = new FabrChatHistoryProvider(agentHost, ctx.SerializedState, ctx.JsonSerializerOptions, logger);
                }
                else
                {
                    // Create new provider with provided threadId
                    provider = new FabrChatHistoryProvider(agentHost, threadId, logger);
                }

                // Register with agent host for tracking (enables auto-flush on deactivation)
                agentHost.TrackChatHistoryProvider(provider);

                return ValueTask.FromResult<ChatHistoryProvider>(provider);
            };
        }

        /// <summary>
        /// Gets the thread identifier for this history provider.
        /// </summary>
        public string ThreadId => _threadId;


        public override ValueTask InvokedAsync(ChatHistoryProvider.InvokedContext context, CancellationToken cancellationToken = default)
        {
            // Don't store anything if there was an exception
            if (context.InvokeException is not null)
            {
                return ValueTask.CompletedTask;
            }

            // Store request messages, AI context provider messages, and response messages
            // This matches Microsoft's InMemoryChatHistoryProvider behavior
            var allNewMessages = context.RequestMessages
                .Concat(context.AIContextProviderMessages ?? [])
                .Concat(context.ResponseMessages ?? []);

            var storedMessages = allNewMessages.Select(m => new StoredChatMessage
            {
                Role = m.Role.Value,
                AuthorName = m.AuthorName,
                Timestamp = DateTime.UtcNow,
                ContentsJson = JsonSerializer.Serialize(m.Contents, ChatMessageSerializerOptions.Instance)
            }).ToList();

            lock (_syncLock)
            {
                _pendingMessages.AddRange(storedMessages);

                // Also add to cache so GetMessagesAsync returns complete history
                _cachedMessages?.AddRange(storedMessages);
            }
            return ValueTask.CompletedTask;  // No persistence yet - will be flushed later
        }

        /// <summary>
        /// Gets all messages (persisted + pending).
        /// Thread-safe with fallback to pending messages on failure.
        /// Reconstructs full ChatMessage with Contents (including FunctionCallContent, FunctionResultContent).
        /// </summary>
        public override async ValueTask<IEnumerable<ChatMessage>> InvokingAsync(ChatHistoryProvider.InvokingContext context, CancellationToken cancellationToken = default)
        {
            _logger?.LogDebug("InvokingAsync called for thread {ThreadId}", _threadId);

            // If we have cached messages, return from cache (no grain call needed)
            if (_cachedMessages != null)
            {
                lock (_syncLock)
                {
                    var cachedResult = _cachedMessages.Select(DeserializeChatMessage).ToList();
                    _logger?.LogDebug("Returning {Count} cached messages for thread {ThreadId}", cachedResult.Count, _threadId);
                    foreach (var msg in cachedResult)
                    {
                        _logger?.LogDebug("  Cached message: Role={Role}, Contents={ContentCount} items, FirstContent={FirstContent}",
                            msg.Role.Value,
                            msg.Contents.Count,
                            msg.Contents.FirstOrDefault()?.GetType().Name ?? "none");
                    }
                    return cachedResult;
                }
            }

            try
            {
                // Load from storage
                _cachedMessages = await _agentHost.GetThreadMessagesAsync(_threadId);
                _logger?.LogDebug("Loaded {Count} messages from storage for thread {ThreadId}", _cachedMessages.Count, _threadId);

                foreach (var stored in _cachedMessages)
                {
                    _logger?.LogDebug("  Stored message: Role={Role}, ContentsJson length={Length}",
                        stored.Role,
                        stored.ContentsJson?.Length ?? 0);
                }

                lock (_syncLock)
                {
                    // Add any pending messages not yet persisted
                    _cachedMessages.AddRange(_pendingMessages);
                    _logger?.LogDebug("Added {PendingCount} pending messages, total now {Total}",
                        _pendingMessages.Count, _cachedMessages.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load messages from storage for thread {ThreadId}, using pending only", _threadId);
                // On failure (e.g., activation access violation), return pending messages only
                lock (_syncLock)
                {
                    return _pendingMessages.Select(DeserializeChatMessage).ToList();
                }
            }

            var result = _cachedMessages.Select(DeserializeChatMessage).ToList();
            _logger?.LogDebug("Returning {Count} total messages for thread {ThreadId}", result.Count, _threadId);
            return result;
        }

        /// <summary>
        /// Converts a StoredChatMessage back to a ChatMessage.
        /// Deserializes full content including tool calls from ContentsJson.
        /// </summary>
        private ChatMessage DeserializeChatMessage(StoredChatMessage m)
        {
            List<AIContent> contents;

            // Handle null or empty ContentsJson gracefully
            if (string.IsNullOrEmpty(m.ContentsJson) || m.ContentsJson == "[]")
            {
                contents = new List<AIContent>();
                _logger?.LogDebug("DeserializeChatMessage: Empty ContentsJson for Role={Role}", m.Role);
            }
            else
            {
                try
                {
                    contents = JsonSerializer.Deserialize<List<AIContent>>(
                        m.ContentsJson,
                        ChatMessageSerializerOptions.Instance) ?? new List<AIContent>();

                    _logger?.LogDebug("DeserializeChatMessage: Role={Role}, Deserialized {Count} contents from JSON (length={Length})",
                        m.Role, contents.Count, m.ContentsJson.Length);

                    foreach (var content in contents)
                    {
                        _logger?.LogDebug("  Content type: {Type}", content.GetType().Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "DeserializeChatMessage: Failed to deserialize ContentsJson for Role={Role}, JSON={Json}",
                        m.Role, m.ContentsJson);
                    contents = new List<AIContent>();
                }
            }

            return new ChatMessage(new ChatRole(m.Role), contents)
            {
                AuthorName = m.AuthorName
            };
        }

        /// <summary>
        /// Gets all persisted messages from grain state (bypasses in-memory cache).
        /// </summary>
        public Task<List<StoredChatMessage>> GetStoredMessagesAsync()
            => _agentHost.GetThreadMessagesAsync(_threadId);

        /// <summary>
        /// Replaces all messages in the thread and resets the local cache.
        /// Caller must flush pending messages first.
        /// </summary>
        public async Task ReplaceAndResetCacheAsync(List<StoredChatMessage> newMessages)
        {
            await _agentHost.ReplaceThreadMessagesAsync(_threadId, newMessages);
            lock (_syncLock)
            {
                _cachedMessages = new List<StoredChatMessage>(newMessages);
                _pendingMessages.Clear();
            }
        }

        /// <summary>
        /// Persists all pending messages to Orleans grain state.
        /// Thread-safe with re-queue on failure to prevent message loss.
        /// </summary>
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            List<StoredChatMessage> messagesToFlush;

            lock (_syncLock)
            {
                if (_pendingMessages.Count == 0)
                    return;

                messagesToFlush = _pendingMessages.ToList();
                _pendingMessages.Clear();
            }

            try
            {
                await _agentHost.AddThreadMessagesAsync(_threadId, messagesToFlush);
            }
            catch (Exception)
            {
                // Re-add messages on failure to prevent data loss
                lock (_syncLock)
                {
                    _pendingMessages.InsertRange(0, messagesToFlush);
                }
                throw;
            }
        }

        /// <summary>
        /// Returns true if there are unsaved messages.
        /// Thread-safe property.
        /// </summary>
        public bool HasPendingMessages
        {
            get
            {
                lock (_syncLock)
                {
                    return _pendingMessages.Count > 0;
                }
            }
        }

        /// <summary>
        /// Serializes the provider state to JSON.
        /// </summary>
        public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
        {
            var state = new { ThreadId = _threadId };
            return JsonSerializer.SerializeToElement(state, jsonSerializerOptions);
        }
    }

    // Keep old name as alias for backward compatibility during migration
    [Obsolete("Use FabrChatHistoryProvider instead")]
    public class FabrChatMessageStore : FabrChatHistoryProvider
    {
        public FabrChatMessageStore(IFabrAgentHost agentHost, string threadId, ILogger? logger = null)
            : base(agentHost, threadId, logger) { }

        public FabrChatMessageStore(IFabrAgentHost agentHost, JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, ILogger? logger = null)
            : base(agentHost, serializedState, jsonSerializerOptions, logger) { }
    }
}
