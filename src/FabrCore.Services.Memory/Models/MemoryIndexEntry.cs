namespace FabrCore.Services.Memory.Models;

/// <summary>
/// A one-line pointer in the hot layer memory index.
/// Contains just enough information to decide whether to load the full memory.
/// </summary>
public class MemoryIndexEntry
{
    /// <summary>The ID of the memory entity this entry points to.</summary>
    public Guid MemoryId { get; set; }

    /// <summary>Short title of the memory.</summary>
    public string Title { get; set; } = "";

    /// <summary>Memory taxonomy type.</summary>
    public MemoryType Type { get; set; }

    /// <summary>
    /// A concise one-line hook describing what this memory contains.
    /// Should be short enough that many entries fit within the hot layer token budget.
    /// </summary>
    public string DescriptionHook { get; set; } = "";

    /// <summary>When the underlying memory was last updated.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Whether this memory is a point-in-time snapshot.</summary>
    public bool IsPointInTime { get; set; }
}
