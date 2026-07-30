using System.Text;
using FabrCore.Services.Memory.Models;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Main memory service facade that agents consume. Scoped to a single memory scope —
/// by default an agent's own handle (isolated memory), or a named shared scope that
/// multiple agents read and write together.
/// Orchestrates the hot/warm/cold memory layers, taxonomy validation, retrieval pipeline,
/// and compaction.
/// </summary>
public interface IAgentMemoryService
{
    /// <summary>The memory scope this service instance is bound to.</summary>
    string ScopeKey { get; }

    /// <summary>
    /// Save a new memory. Validates against taxonomy rules, generates embedding,
    /// inserts as a warm memory, and adds a pointer to the hot layer index.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when taxonomy validation fails.</exception>
    Task<MemoryEntry> SaveMemoryAsync(
        string title, MemoryType type, string content,
        string? description = null, Dictionary<string, string>? metadata = null,
        bool isPointInTime = false,
        CancellationToken ct = default);

    /// <summary>
    /// Recall memories relevant to the current query. Returns the hot layer index
    /// plus selectively retrieved warm memories with freshness warnings.
    /// </summary>
    /// <param name="query">The current user query or context.</param>
    /// <param name="alreadySurfacedIds">IDs of memories already shown in the current conversation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MemoryRecallResult> RecallAsync(
        string query,
        IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default);

    /// <summary>Get the current hot layer memory index.</summary>
    Task<MemoryIndex> GetMemoryIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// Search the cold layer archive via vector similarity.
    /// </summary>
    Task<IReadOnlyList<MemorySearchResult>> SearchArchiveAsync(
        string query, int limit = 10, MemoryType? typeFilter = null,
        CancellationToken ct = default);

    /// <summary>Run memory consolidation (dedup, prune, resolve contradictions, truncate index).</summary>
    Task<MemoryConsolidationResult> ConsolidateAsync(CancellationToken ct = default);

    /// <summary>Delete a memory by ID, removing it from the store and the hot index.</summary>
    Task<bool> ForgetMemoryAsync(Guid memoryId, CancellationToken ct = default);

    /// <summary>
    /// Update an existing memory. Only the supplied (non-null) fields change.
    /// Re-generates the embedding when <paramref name="content"/> changes and keeps
    /// the hot index entry in sync when the title, type, or description changes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the memory does not exist.</exception>
    Task<MemoryEntry> UpdateMemoryAsync(
        Guid memoryId,
        string? title = null,
        MemoryType? type = null,
        string? content = null,
        string? description = null,
        MemoryTemperature? temperature = null,
        CancellationToken ct = default);

    /// <summary>
    /// Format a <see cref="MemoryRecallResult"/> as a context block with markers that the
    /// memory extraction system recognizes and skips during compaction. Inject the returned
    /// string into the conversation so recalled memories don't get re-extracted as duplicates.
    /// </summary>
    /// <param name="recall">The recall result from <see cref="RecallAsync"/>.</param>
    /// <returns>A formatted string with memory system markers, ready to append to a ChatMessage.</returns>
    string FormatRecallContext(MemoryRecallResult recall);

    /// <summary>
    /// Extract durable memories from a list of chat messages (typically called during OnCompaction
    /// before older messages are summarized/removed). Uses an LLM to identify what's worth
    /// persisting, then saves each extracted memory to the store.
    /// Content inside memory system markers is automatically excluded from extraction.
    /// </summary>
    /// <param name="messages">The chat messages to extract memories from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The list of memories that were extracted and saved.</returns>
    Task<IReadOnlyList<MemoryEntry>> ExtractMemoriesAsync(
        IList<ChatMessage> messages,
        CancellationToken ct = default);
}
