namespace FabrCore.Services.Memory.Models;

/// <summary>
/// Core memory entity model. Maps to a concept node in mem.MemoryEntity.
/// Content and embeddings live in MemoryChunk — they are populated from the primary chunk when loaded.
/// Relationships to other entities are populated from MemoryRelationship when requested.
/// </summary>
public class MemoryEntry
{
    /// <summary>Unique identifier (maps to EntityId).</summary>
    public Guid Id { get; set; }

    /// <summary>The agent handle that owns this memory.</summary>
    public string ScopeKey { get; set; } = "";

    /// <summary>Short descriptive title (maps to Name column).</summary>
    public string Title { get; set; } = "";

    /// <summary>Memory taxonomy type (maps to EntityType column).</summary>
    public MemoryType Type { get; set; }

    /// <summary>Memory temperature layer (maps to Visibility column).</summary>
    public MemoryTemperature Temperature { get; set; } = MemoryTemperature.Warm;

    /// <summary>
    /// When true, this memory is a point-in-time snapshot that was stale at creation.
    /// Always receives a freshness warning during recall, regardless of age.
    /// </summary>
    public bool IsPointInTime { get; set; }

    /// <summary>Brief description of the memory.</summary>
    public string? Description { get; set; }

    /// <summary>Extensible metadata (serialized as JSON in the Metadata column).</summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>When the memory was first created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the memory was last updated.</summary>
    public DateTime UpdatedAt { get; set; }

    // ─── Loaded from chunks and relationships (not stored on entity table) ───

    /// <summary>
    /// Content populated from the primary chunk (ChunkIndex=0) by ordinary loading.
    /// Opt-in matched-chunk recall returns bounded evidence; inspect RecalledChunkId and IsContentTruncated.
    /// Not stored on the entity table — loaded from MemoryChunk when the entity is retrieved.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>Source chunk for opt-in matched-chunk recall. Null for ordinary primary-body loading.</summary>
    public Guid? RecalledChunkId { get; set; }
    public int? RecalledChunkIndex { get; set; }
    /// <summary>True when the recalled body is a bounded prefix of its source chunk.</summary>
    public bool IsContentTruncated { get; set; }

    /// <summary>Per-chunk evidence for opt-in multi-chunk recall. Content joins these bounded bodies.
    /// Null for ordinary loading; omitted chunks are not indicated by IsContentTruncated.</summary>
    public IReadOnlyList<MemoryChunkEvidence>? RecalledChunks { get; set; }

    /// <summary>
    /// Vector embedding, populated from the primary chunk.
    /// Not stored on the entity table — loaded from MemoryChunk.
    /// </summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// All chunks belonging to this entity. Populated on explicit request.
    /// Most entities have a single chunk (ChunkIndex=0).
    /// </summary>
    public List<MemoryChunkEntry>? Chunks { get; set; }

    /// <summary>
    /// Graph relationships to other entities. Populated during graph-aware recall.
    /// </summary>
    public List<MemoryRelationshipEntry>? Relationships { get; set; }
}
