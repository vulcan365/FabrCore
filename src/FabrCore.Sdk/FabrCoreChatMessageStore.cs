using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using FabrCore.Core;

namespace FabrCore.Sdk
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
    public class FabrCoreChatHistoryProvider : ChatHistoryProvider
    {
        private readonly IFabrCoreAgentHost _agentHost;
        private readonly string _threadId;
        private readonly List<StoredChatMessage> _pendingMessages = new();
        private List<StoredChatMessage>? _cachedMessages;
        private readonly object _syncLock = new();
        private readonly ILogger? _logger;

        /// <summary>
        /// Sliding-window projection applied to <see cref="ProvideChatHistoryAsync"/>.
        /// When non-null, the provider returns only the most recent messages that fit
        /// under the token ceiling — storage is untouched. When null, the full history
        /// is returned (backward-compatible behavior).
        /// </summary>
        public ProjectionConfig? ActiveProjection { get; set; }

        public FabrCoreChatHistoryProvider(IFabrCoreAgentHost agentHost, string threadId, ILogger? logger = null)
        {
            _agentHost = agentHost;
            _threadId = threadId;
            _logger = logger;
        }

        /// <summary>
        /// Creates a FabrCoreChatHistoryProvider from serialized state.
        /// </summary>
        /// <param name="agentHost">The agent host for persistence operations.</param>
        /// <param name="serializedState">Previously serialized state containing ThreadId.</param>
        /// <param name="jsonSerializerOptions">JSON serialization options.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public FabrCoreChatHistoryProvider(
            IFabrCoreAgentHost agentHost,
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
        /// Creates a new FabrCoreChatHistoryProvider and registers it with the agent host.
        /// </summary>
        /// <param name="agentHost">The agent host for persistence operations.</param>
        /// <param name="threadId">The thread ID to use for new sessions.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        /// <returns>A configured FabrCoreChatHistoryProvider instance.</returns>
        public static FabrCoreChatHistoryProvider Create(
            IFabrCoreAgentHost agentHost,
            string threadId,
            ILogger? logger = null)
        {
            var provider = new FabrCoreChatHistoryProvider(agentHost, threadId, logger);
            agentHost.TrackChatHistoryProvider(provider);
            return provider;
        }

        /// <summary>
        /// Gets the thread identifier for this history provider.
        /// </summary>
        public string ThreadId => _threadId;


        protected override ValueTask StoreChatHistoryAsync(ChatHistoryProvider.InvokedContext context, CancellationToken cancellationToken = default)
        {
            // Store request messages and response messages
            var allNewMessages = context.RequestMessages
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

                // Also add to cache so subsequent reads return complete history
                _cachedMessages?.AddRange(storedMessages);
            }
            return ValueTask.CompletedTask;  // No persistence yet - will be flushed later
        }

        /// <summary>
        /// Provides chat history messages for the current invocation.
        /// Messages are loaded from Orleans storage on first access, then served from cache.
        /// </summary>
        protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(ChatHistoryProvider.InvokingContext context, CancellationToken cancellationToken = default)
        {
            _logger?.LogDebug("ProvideChatHistoryAsync called for thread {ThreadId}", _threadId);

            // If we have cached messages, return from cache (no grain call needed)
            if (_cachedMessages != null)
            {
                lock (_syncLock)
                {
                    var cachedResult = ProjectAndDeserialize(_cachedMessages);
                    _logger?.LogDebug("Returning {Count} cached messages for thread {ThreadId}", cachedResult.Count, _threadId);
                    return cachedResult;
                }
            }

            try
            {
                // Load from storage
                _cachedMessages = await _agentHost.GetThreadMessagesAsync(_threadId);
                _logger?.LogDebug("Loaded {Count} messages from storage for thread {ThreadId}", _cachedMessages.Count, _threadId);

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
                    return ProjectAndDeserialize(_pendingMessages);
                }
            }

            var result = ProjectAndDeserialize(_cachedMessages);
            _logger?.LogDebug("Returning {Count} total messages for thread {ThreadId}", result.Count, _threadId);
            return result;
        }

        /// <summary>
        /// Applies <see cref="ActiveProjection"/> (if set) to the given stored messages and
        /// deserializes the result. When projection is null, every message is returned
        /// unchanged — preserving backward-compatible behavior for callers that have not
        /// opted in to projection.
        /// </summary>
        private List<ChatMessage> ProjectAndDeserialize(IReadOnlyList<StoredChatMessage> all)
        {
            var projection = ActiveProjection;
            if (projection is null || !projection.Enabled || all.Count == 0)
            {
                return all.Select(DeserializeChatMessage).ToList();
            }

            var projected = ProjectForLlm(all, projection);

            if (projected.Count < all.Count)
            {
                var totalTokens = CompactionService.EstimateTokens(all.ToList());
                var keptTokens = CompactionService.EstimateTokens(projected);
                _logger?.LogInformation(
                    "Projection dropped {Dropped}/{Total} messages (~{KeptTokens}/{TotalTokens} estimated tokens) for thread {ThreadId}",
                    all.Count - projected.Count, all.Count, keptTokens, totalTokens, _threadId);
            }

            return projected.Select(DeserializeChatMessage).ToList();
        }

        /// <summary>
        /// Sliding-window projection: keep all leading system messages, plus as many of
        /// the newest messages as fit under <see cref="ProjectionConfig.MaxContextTokens"/>
        /// * <see cref="ProjectionConfig.Threshold"/>. Guarantees at least
        /// <see cref="ProjectionConfig.MinKeepLastN"/> of the most recent messages. Keeps
        /// tool-call groups paired (never drops an orphaned tool result).
        /// </summary>
        internal static List<StoredChatMessage> ProjectForLlm(IReadOnlyList<StoredChatMessage> all, ProjectionConfig cfg)
        {
            if (all.Count == 0)
                return new List<StoredChatMessage>();

            var budget = (int)(cfg.MaxContextTokens * cfg.Threshold);
            if (budget <= 0)
                return all.ToList();

            // Separate leading system messages — these are always kept (system prompt,
            // compaction summary stub). They also count against the budget.
            var leadingSystemCount = 0;
            for (var i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Role, "system", StringComparison.OrdinalIgnoreCase))
                    leadingSystemCount++;
                else
                    break;
            }

            var leadingSystem = all.Take(leadingSystemCount).ToList();
            var nonSystem = all.Skip(leadingSystemCount).ToList();
            if (nonSystem.Count == 0)
                return leadingSystem;

            // Tokens already consumed by required leading system messages.
            var consumed = CompactionService.EstimateTokens(leadingSystem);

            // Walk backwards through non-system messages, accumulating until budget.
            var keptIndex = nonSystem.Count; // exclusive start index of the kept window
            for (var i = nonSystem.Count - 1; i >= 0; i--)
            {
                var msgTokens = CompactionService.EstimateTokens(new List<StoredChatMessage> { nonSystem[i] });
                if (consumed + msgTokens > budget && (nonSystem.Count - i) > cfg.MinKeepLastN)
                {
                    break;
                }
                consumed += msgTokens;
                keptIndex = i;
            }

            // Expand window forward past orphaned tool/function-result messages at the
            // start — they must stay paired with their preceding assistant function-call.
            // Specifically: if the first kept message is "tool" role, walk back one more
            // (but we already walked back as far as we could). The safer fix: walk FORWARD
            // from keptIndex while the message at keptIndex is "tool", since we cannot
            // send an orphaned tool result without its assistant function-call.
            while (keptIndex < nonSystem.Count &&
                   string.Equals(nonSystem[keptIndex].Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                keptIndex++;
            }

            // Pathological case: budget too tight, everything got dropped. Force-keep
            // at least MinKeepLastN most recent messages even if they blow the budget —
            // better to overshoot than to send the LLM an empty history for the current turn.
            if (keptIndex >= nonSystem.Count)
            {
                var force = Math.Min(cfg.MinKeepLastN, nonSystem.Count);
                keptIndex = nonSystem.Count - force;
                while (keptIndex < nonSystem.Count &&
                       string.Equals(nonSystem[keptIndex].Role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    keptIndex++;
                }
            }

            var result = new List<StoredChatMessage>(leadingSystem.Count + (nonSystem.Count - keptIndex));
            result.AddRange(leadingSystem);
            for (var i = keptIndex; i < nonSystem.Count; i++)
                result.Add(nonSystem[i]);
            return result;
        }

        /// <summary>
        /// Gets all messages (persisted + pending) as ChatMessage objects.
        /// Used by ForkAsync to get a snapshot of the conversation.
        /// </summary>
        public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
        {
            // If we have cached messages, return from cache
            if (_cachedMessages != null)
            {
                lock (_syncLock)
                {
                    return _cachedMessages.Select(DeserializeChatMessage).ToList();
                }
            }

            try
            {
                _cachedMessages = await _agentHost.GetThreadMessagesAsync(_threadId);
                lock (_syncLock)
                {
                    _cachedMessages.AddRange(_pendingMessages);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load messages from storage for thread {ThreadId}, using pending only", _threadId);
                lock (_syncLock)
                {
                    return _pendingMessages.Select(DeserializeChatMessage).ToList();
                }
            }

            return _cachedMessages.Select(DeserializeChatMessage).ToList();
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
    }

    // Keep old name as alias for backward compatibility during migration
    [Obsolete("Use FabrCoreChatHistoryProvider instead")]
    public class FabrCoreChatMessageStore : FabrCoreChatHistoryProvider
    {
        public FabrCoreChatMessageStore(IFabrCoreAgentHost agentHost, string threadId, ILogger? logger = null)
            : base(agentHost, threadId, logger) { }

        public FabrCoreChatMessageStore(IFabrCoreAgentHost agentHost, JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, ILogger? logger = null)
            : base(agentHost, serializedState, jsonSerializerOptions, logger) { }
    }
}
