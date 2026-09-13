namespace FabrCore.Services.Memory.Models;

/// <summary>
/// A content chunk belonging to a memory entity. Each entity has at least one chunk (ChunkIndex=0).
/// Chunks hold the actual knowledge content and vector embeddings — entities are concept nodes only.
/// </summary>
public class MemoryChunkEntry
{
    /// <summary>Unique chunk identifier.</summary>
    public Guid ChunkId { get; set; }

    /// <summary>The parent entity this chunk belongs to.</summary>
    public Guid EntityId { get; set; }

    /// <summary>The knowledge content stored in this chunk.</summary>
    public string Content { get; set; } = "";

    /// <summary>Vector embedding for semantic search (dimension set by AgentMemoryOptions.EmbeddingDimensions).</summary>
    public float[]? Embedding { get; set; }

    /// <summary>Ordinal position within the parent entity (0 = primary chunk).</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Extensible metadata (serialized as JSON).</summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>When this chunk was first created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When this chunk was last updated (content or embedding change).</summary>
    public DateTime UpdatedAt { get; set; }
}
