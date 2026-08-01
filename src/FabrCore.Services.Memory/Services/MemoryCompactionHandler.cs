using FabrCore.Services.Memory.Abstractions;
using FabrCore.Services.Memory.Configuration;
using FabrCore.Services.Memory.Models;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Services.Memory.Services;

/// <summary>
/// Unified entry point for memory-aware compaction. Provides both the full tier cascade
/// (CompactAsync) and standalone memory extraction (ExtractMemoriesAsync).
/// </summary>
public class MemoryCompactionHandler
{
    private readonly IAgentMemoryService _memoryService;
    private readonly MemoryAwareCompactionService _compactionService;
    private readonly AgentMemoryOptions _memoryOptions;
    private readonly ILogger<MemoryCompactionHandler> _logger;

    public MemoryCompactionHandler(
        IAgentMemoryService memoryService,
        MemoryAwareCompactionService compactionService,
        AgentMemoryOptions memoryOptions,
        ILoggerFactory loggerFactory)
    {
        _memoryService = memoryService;
        _compactionService = compactionService;
        _memoryOptions = memoryOptions;
        _logger = loggerFactory.CreateLogger<MemoryCompactionHandler>();
    }

    /// <summary>
    /// Run the full memory-aware compaction cascade: tool result compression → memory extraction → structured summarization.
    /// <para>
    /// One-line usage from OnCompaction:
    /// <code>
    /// return await _compactionHandler.CompactAsync(chatHistoryProvider, compactionConfig);
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="chatHistoryProvider">The chat history provider from OnCompaction.</param>
    /// <param name="compactionConfig">The compaction config from OnCompaction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The compaction result, or null if compaction was not needed.</returns>
    public async Task<CompactionResult?> CompactAsync(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        CompactionConfig compactionConfig,
        CancellationToken ct = default)
    {
        try
        {
            return await _compactionService.CompactAsync(
                chatHistoryProvider,
                compactionConfig,
                _memoryService,
                _memoryOptions,
                _memoryOptions.Models.CompactionModelName,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory-aware compaction failed for agent '{Agent}'", _memoryService.ScopeKey);
            return null;
        }
    }

    /// <summary>
    /// Standalone memory extraction from the chat history provider.
    /// Use this when you want to extract memories without running the full compaction cascade.
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntry>> ExtractMemoriesAsync(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        int keepLastN = 20,
        CancellationToken ct = default)
    {
        try
        {
            var allMessages = await chatHistoryProvider.GetMessagesAsync(ct);

            if (allMessages.Count <= keepLastN)
                return [];

            var countToExtract = allMessages.Count - keepLastN;
            var olderMessages = allMessages.Take(countToExtract).ToList();

            if (olderMessages.Count == 0)
                return [];

            _logger.LogInformation("Extracting memories from {Count} older messages for agent '{Agent}'",
                olderMessages.Count, _memoryService.ScopeKey);

            var extracted = await _memoryService.ExtractMemoriesAsync(olderMessages, ct);

            _logger.LogInformation("Extracted {Count} memories for agent '{Agent}'",
                extracted.Count, _memoryService.ScopeKey);

            return extracted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory extraction failed for agent '{Agent}'", _memoryService.ScopeKey);
            return [];
        }
    }

    /// <summary>
    /// Standalone memory extraction from an explicit list of chat messages.
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntry>> ExtractMemoriesAsync(
        IList<ChatMessage> allMessages,
        int keepLastN = 20,
        CancellationToken ct = default)
    {
        try
        {
            if (allMessages.Count <= keepLastN)
                return [];

            var olderMessages = allMessages.Take(allMessages.Count - keepLastN).ToList();

            if (olderMessages.Count == 0)
                return [];

            _logger.LogInformation("Extracting memories from {Count} messages for agent '{Agent}'",
                olderMessages.Count, _memoryService.ScopeKey);

            var extracted = await _memoryService.ExtractMemoriesAsync(olderMessages, ct);

            _logger.LogInformation("Extracted {Count} memories for agent '{Agent}'",
                extracted.Count, _memoryService.ScopeKey);

            return extracted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory extraction failed for agent '{Agent}'", _memoryService.ScopeKey);
            return [];
        }
    }
}
