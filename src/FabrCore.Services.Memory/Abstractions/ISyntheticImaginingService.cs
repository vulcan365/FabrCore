using FabrCore.Services.Memory.Models;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Analyzes conversation context to generate targeted memory search queries,
/// then runs them through the agent memory system and returns aggregated results.
/// Provides richer recall than a single-query lookup by considering the full
/// conversation when deciding what memories are relevant.
/// </summary>
public interface ISyntheticImaginingService
{
    /// <summary>
    /// Analyze conversation context, generate memory search queries, and return
    /// aggregated memory results. Forks the chat history internally — the original
    /// is never modified.
    /// </summary>
    /// <param name="chatHistoryProvider">The chat history provider for the current conversation.</param>
    /// <param name="lastUserMessage">The most recent user message (used as the primary search anchor).</param>
    /// <param name="scopeKey">The agent handle to scope memory searches to.</param>
    /// <param name="alreadySurfacedIds">IDs of memories already shown in the current conversation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SyntheticImaginingResult> ImagineAsync(
        FabrCoreChatHistoryProvider chatHistoryProvider,
        string lastUserMessage,
        string scopeKey,
        IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// Overload accepting an explicit message list (no fork needed).
    /// Use when you already have the messages and don't need fork isolation.
    /// </summary>
    /// <param name="messages">The conversation messages to analyze.</param>
    /// <param name="lastUserMessage">The most recent user message (used as the primary search anchor).</param>
    /// <param name="scopeKey">The agent handle to scope memory searches to.</param>
    /// <param name="alreadySurfacedIds">IDs of memories already shown in the current conversation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SyntheticImaginingResult> ImagineAsync(
        IList<ChatMessage> messages,
        string lastUserMessage,
        string scopeKey,
        IReadOnlySet<Guid>? alreadySurfacedIds = null,
        CancellationToken ct = default);
}
