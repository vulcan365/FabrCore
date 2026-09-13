namespace FabrCore.Services.Memory.Models;

/// <summary>
/// The hot layer memory index. A bounded list of one-line pointers to warm memories.
/// Always injected into agent context. Capped by entry count and token budget.
/// Stored as JSON in a single AgentMemoryEntity row (Name="__MEMORY_INDEX__").
/// </summary>
public class MemoryIndex
{
    /// <summary>Ordered list of index entries (newest first).</summary>
    public List<MemoryIndexEntry> Entries { get; set; } = [];

    /// <summary>Estimated total tokens consumed by this index.</summary>
    public int TotalEstimatedTokens { get; set; }

    /// <summary>
    /// Estimates the token count for a description hook.
    /// Uses a conservative 1 token per 4 characters.
    /// </summary>
    public static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    /// <summary>Recalculates the total estimated tokens from all entries.</summary>
    public void RecalculateTokens()
    {
        TotalEstimatedTokens = 0;
        foreach (var entry in Entries)
        {
            // Title + type label + hook + overhead for formatting
            TotalEstimatedTokens += EstimateTokens(entry.Title)
                                  + EstimateTokens(entry.Type.ToString())
                                  + EstimateTokens(entry.DescriptionHook)
                                  + 5; // formatting overhead per entry
        }
    }
}
