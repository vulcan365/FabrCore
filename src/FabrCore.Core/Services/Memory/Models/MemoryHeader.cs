namespace FabrCore.Services.Memory.Models;

/// <summary>
/// Lightweight memory scan result containing only metadata — no content or embeddings.
/// Used in the first stage of the retrieval pipeline for cheap header scanning.
/// </summary>
public class MemoryHeader
{
    /// <summary>Memory entity ID.</summary>
    public Guid MemoryId { get; set; }

    /// <summary>Short title (maps to Name column).</summary>
    public string Title { get; set; } = "";

    /// <summary>Memory taxonomy type.</summary>
    public MemoryType Type { get; set; }

    /// <summary>Brief description of the memory.</summary>
    public string? Description { get; set; }

    /// <summary>When the memory was last updated (for freshness checks).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Whether this memory is a point-in-time snapshot that is stale immediately.</summary>
    public bool IsPointInTime { get; set; }
}
