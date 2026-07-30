namespace FabrCore.Services.Memory.Models;

/// <summary>
/// A memory entry returned from vector search, with distance score and optional freshness warning.
/// </summary>
public class MemorySearchResult
{
    /// <summary>The full memory entry.</summary>
    public MemoryEntry Entry { get; set; } = new();

    /// <summary>Cosine distance from the query vector (lower = more relevant).</summary>
    public double Distance { get; set; }

    /// <summary>
    /// Staleness warning text, or null if the memory is fresh.
    /// Example: "[Stale: last updated 5 days ago] This is a point-in-time observation.
    /// Verify against current state before relying on it."
    /// </summary>
    public string? FreshnessWarning { get; set; }
}
