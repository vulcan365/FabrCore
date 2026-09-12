using FabrCore.Services.Memory.Models;

namespace FabrCore.Services.Memory.Abstractions;

/// <summary>
/// Low-level storage abstraction for the knowledge graph memory system.
/// Handles SQL operations across all three tables:
///   MemoryEntity (NODE) — concept nodes
///   MemoryChunk — content + embeddings
///   MemoryRelationship (EDGE) — typed edges between nodes
/// </summary>
public interface IMemoryStore
{
    // ─── Entity (Node) Operations ───────────────────────────────────────

    /// <summary>Insert a new entity node (no content/embedding — those go in chunks).</summary>
    Task<MemoryEntry> InsertEntityAsync(string scopeKey, MemoryEntry entry, CancellationToken ct = default);

    /// <summary>Get an entity by ID. Does NOT load chunk content — call GetPrimaryChunkAsync for that.</summary>
    Task<MemoryEntry?> GetEntityByIdAsync(string scopeKey, Guid entityId, CancellationToken ct = default);

    /// <summary>Update entity metadata (description, visibility, etc.). Does NOT touch chunks.</summary>
    Task<MemoryEntry> UpdateEntityAsync(string scopeKey, MemoryEntry entry, CancellationToken ct = default);

    /// <summary>Delete an entity and cascade to its chunks and relationships.</summary>
    Task<bool> DeleteEntityAsync(string scopeKey, Guid entityId, CancellationToken ct = default);

    /// <summary>
    /// Get lightweight headers for an agent's memories, sorted by UpdatedAt descending.
    /// Does not load content, embeddings, or relationships.
    /// </summary>
    Task<IReadOnlyList<MemoryHeader>> GetHeadersAsync(
        string scopeKey, int limit, MemoryType? typeFilter = null, CancellationToken ct = default);

    // ─── Chunk (Content + Embedding) Operations ─────────────────────────

    /// <summary>Insert a new chunk for an entity. Generates embedding from content.</summary>
    Task<MemoryChunkEntry> InsertChunkAsync(string scopeKey, MemoryChunkEntry chunk, CancellationToken ct = default);

    /// <summary>Update an existing chunk's content and regenerate its embedding.</summary>
    Task<MemoryChunkEntry> UpdateChunkAsync(string scopeKey, MemoryChunkEntry chunk, CancellationToken ct = default);

    /// <summary>Get the primary chunk (ChunkIndex=0) for an entity.</summary>
    Task<MemoryChunkEntry?> GetPrimaryChunkAsync(string scopeKey, Guid entityId, CancellationToken ct = default);

    /// <summary>Get all chunks for an entity, ordered by ChunkIndex.</summary>
    Task<IReadOnlyList<MemoryChunkEntry>> GetChunksAsync(string scopeKey, Guid entityId, CancellationToken ct = default);

    // ─── Relationship (Edge) Operations ─────────────────────────────────

    /// <summary>Create a directed, typed, weighted edge between two entities.</summary>
    Task InsertRelationshipAsync(
        string scopeKey, Guid fromEntityId, Guid toEntityId,
        string relationshipType, string? description = null, double weight = 1.0,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// Get all relationships for an entity (both directions: outgoing and incoming).
    /// Returns the related entity's ID, title, and type alongside the relationship metadata.
    /// </summary>
    Task<IReadOnlyList<MemoryRelationshipEntry>> GetRelationshipsAsync(
        string scopeKey, Guid entityId, CancellationToken ct = default);

    // ─── Search Operations ──────────────────────────────────────────────

    /// <summary>
    /// Vector similarity search across memory chunks, JOINed to parent entities.
    /// This is the primary search path — all embeddings live on chunks.
    /// </summary>
    Task<IReadOnlyList<MemorySearchResult>> VectorSearchAsync(
        string scopeKey, float[] queryEmbedding, int limit,
        MemoryType? typeFilter = null, CancellationToken ct = default);

    /// <summary>
    /// Find existing entities whose chunk content is similar to the given embedding.
    /// Used for entity matching on save — detects when new knowledge should update
    /// an existing entity rather than creating a duplicate.
    /// </summary>
    Task<IReadOnlyList<(MemoryEntry Entity, MemoryChunkEntry Chunk, double Distance)>> FindSimilarByContentAsync(
        string scopeKey, float[] queryEmbedding, int limit, double maxDistance,
        CancellationToken ct = default);

    /// <summary>
    /// Find pairs of entities with similar chunk content (for deduplication).
    /// CROSS JOINs chunks, returns pairs of entity IDs with cosine distance below threshold.
    /// </summary>
    Task<IReadOnlyList<(Guid Id1, Guid Id2, double Distance)>> FindDuplicatePairsAsync(
        string scopeKey, double distanceThreshold, MemoryType? typeFilter = null, CancellationToken ct = default);

    // ─── Hot Index Operations ───────────────────────────────────────────

    /// <summary>Get the raw JSON content of the memory index sentinel row.</summary>
    Task<string?> GetIndexContentAsync(string scopeKey, CancellationToken ct = default);

    /// <summary>Create or update the memory index sentinel row.</summary>
    Task UpsertIndexContentAsync(string scopeKey, string indexJson, CancellationToken ct = default);

    /// <summary>
    /// Atomically read-modify-write the memory index sentinel row under a scope-keyed
    /// exclusive lock, so concurrent writers (agents sharing a scope, admin edits) cannot
    /// lose updates. <paramref name="transform"/> receives the current JSON (or null) and
    /// returns the new JSON, or null to leave the row unchanged.
    /// </summary>
    Task ModifyIndexContentAsync(string scopeKey, Func<string?, string?> transform, CancellationToken ct = default);

    // ─── Embedding Operations ───────────────────────────────────────────

    /// <summary>Generate a vector embedding for the given text using the configured IEmbeddings service.</summary>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}
